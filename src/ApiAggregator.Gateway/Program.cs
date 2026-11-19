// API Aggregator gateway: serves every PUBLISHED recipe under /bff/...
// Read docs/architecture/01-overview.md first.

using System.Threading.RateLimiting;
using ApiAggregator.Engine;
using ApiAggregator.Engine.Authoring;
using ApiAggregator.Engine.Config;
using ApiAggregator.Engine.Execution;
using ApiAggregator.Engine.Logging.Live;
using ApiAggregator.Engine.Routing;
using ApiAggregator.Gateway;
using ApiAggregator.Hosting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Http.Resilience;

var builder = WebApplication.CreateBuilder(args);
builder.AddLocalSecretsFile(); // Development only: secrets saved from the admin UI

// Logs: readable single lines on a developer machine, one JSON object per line (with scopes) everywhere else.
if (builder.Environment.IsDevelopment())
    builder.Logging.AddSimpleConsole(o => { o.IncludeScopes = true; o.SingleLine = true; });
else
    builder.Logging.ClearProviders().AddJsonConsole(o =>
    {
        o.IncludeScopes = true;
        o.UseUtcTimestamp = true;
        o.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ ";
    });

// Keep the latest lines in memory for the admin UI's Logs page. After the console setup above: ClearProviders would remove it.
builder.Logging.AddLiveLogs(builder.Configuration.GetSection("LiveLogs"));

builder.WebHost.ConfigureKestrel(k =>
{
    k.AddServerHeader = false;
    k.Limits.MaxRequestBodySize = 1 * 1024 * 1024;
    k.Limits.MaxRequestHeadersTotalSize = 32 * 1024;
});

// ---------------------------------------------------------------- engine + downstream resilience
builder.Services.AddAggregatorEngine(builder.Configuration);
builder.Services.AddHttpClient(EngineHttpClients.Downstream)
    .AddStandardResilienceHandler(o =>
    {
        // Retry only safe, idempotent calls (GET/HEAD/OPTIONS...), never POST/PUT/PATCH/DELETE.
        o.Retry.DisableForUnsafeHttpMethods();
        o.Retry.MaxRetryAttempts = 2;
    });

// ---------------------------------------------------------------- inbound auth (JWT)
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o => InboundAuth.Configure(o, builder.Configuration.GetSection("Auth"), builder.Environment));
// Who may read the live logs: the same people who author or publish recipes.
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("log-reader", p => p.RequireAssertion(c => InboundAuth.HasScope(c.User, "aggregator.author") || InboundAuth.HasScope(c.User, "aggregator.publisher")));

// ---------------------------------------------------------------- abuse protection
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    // Too many requests: same problem-details shape as every other error, and say when to come back.
    o.OnRejected = async (context, ct) =>
    {
        var http = context.HttpContext;
        http.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        http.Response.Headers.RetryAfter = "60";
        await GatewayProblems.WriteAsync(http, ErrorBody.Problem("rate_limited", "Too many requests. Wait a minute and try again.", 429,
            InboundAuth.CorrelationIdFrom(http), retryable: true), ct);
    };
    o.AddPolicy("per-caller", http => RateLimitPartition.GetFixedWindowLimiter(
        http.User.FindFirst("sub")?.Value ?? http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = builder.Configuration.GetValue("Gateway:RequestsPerMinutePerCaller", 600),
            Window = TimeSpan.FromMinutes(1)
        }));
});

builder.Services.AddHealthChecks();

// Unexpected errors: a generic problem-details body. The exception message is never sent to the caller.
builder.Services.AddProblemDetails(o => o.CustomizeProblemDetails = context =>
{
    var problem = context.ProblemDetails;
    problem.Type = "urn:api-aggregator:error:internal_error";
    problem.Title = "Something went wrong in the gateway.";
    problem.Detail = null;
    problem.Extensions["code"] = "internal_error";
    problem.Extensions["correlationId"] = InboundAuth.CorrelationIdFrom(context.HttpContext);
});

var app = builder.Build();

app.UseCorrelationId(); // first, so every response and log line has the id
app.UseMiddleware<RequestLogMiddleware>();
// Security headers on EVERY response, including error pages written by the exception handler
// (which clears headers set earlier), hence OnStarting rather than setting them up front.
app.Use(async (http, next) =>
{
    http.Response.OnStarting(() =>
    {
        var h = http.Response.Headers;
        h.XContentTypeOptions = "nosniff";
        h.CacheControl = "no-store"; // responses may hold personal data: never cache in browsers or proxies
        h["Referrer-Policy"] = "no-referrer";
        return Task.CompletedTask;
    });
    await next();
});
app.UseExceptionHandler();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapHealthChecks("/health/live");
app.MapGet("/health/ready", async (IConfigStore store, CancellationToken ct) =>
{
    var snapshot = await store.GetSnapshotAsync(ct);
    return Results.Ok(new { status = "ready", recipes = snapshot.Recipes.Count, configVersion = snapshot.Version });
});

// Live logs for the admin UI. Outside /bff on purpose, and skipped by the request log (see RequestLogMiddleware).
app.MapLiveLogs("/_admin/logs", "log-reader");

// OpenAPI document for all published aggregated endpoints (web/mobile teams generate clients from it),
// and the Swagger UI page that shows it. Both are anonymous: callers need the spec before they have a token.
// Literal routes win over the "/bff/{**path}" catch-all below, and they skip the per-caller rate limit
// (which is attached to the catch-all only).
var devTokenEnabled = app.Environment.IsDevelopment() && !string.IsNullOrEmpty(builder.Configuration["Auth:DevSigningKey"]);

if (builder.Configuration.GetValue("Gateway:OpenApi:Enabled", true))
{
    var includeInternalDetails = builder.Configuration.GetValue("Gateway:OpenApi:IncludeInternalDetails", false);
    CachedSpec? cachedSpec = null; // rebuilt only when the config or the address changes

    app.MapGet("/bff/openapi.json", async (HttpContext http, IConfigStore store, CancellationToken ct) =>
    {
        var snapshot = await store.GetSnapshotAsync(ct);
        var server = $"{http.Request.Scheme}://{http.Request.Host}";
        var hit = cachedSpec; // a class, so reading and replacing it is safe across requests
        if (hit is null || hit.Version != snapshot.Version || hit.Server != server)
        {
            var spec = OpenApiDocumentBuilder.Build(snapshot, new OpenApiBuildOptions { ServerUrl = server, IncludeInternalDetails = includeInternalDetails });
            cachedSpec = hit = new CachedSpec(snapshot.Version, server, spec.ToJsonString());
        }
        return Results.Content(hit.Json, "application/json");
    });

    if (builder.Configuration.GetValue("Gateway:ApiDocs:Enabled", app.Environment.IsDevelopment()))
    {
        app.MapGet("/bff/docs", async (HttpContext http, IConfigStore store, CancellationToken ct) =>
        {
            // Development: sign the page in with a test token that has every scope the endpoints ask for.
            string? scopes = null;
            if (devTokenEnabled)
            {
                var snapshot = await store.GetSnapshotAsync(ct);
                scopes = string.Join(' ', snapshot.Recipes.Select(r => r.RequiredScope).Append("customer.read")
                    .Where(s => !string.IsNullOrEmpty(s)).Distinct());
            }
            http.Response.Headers.ContentSecurityPolicy = ApiDocsPage.ContentSecurityPolicy;
            return Results.Content(ApiDocsPage.Html(scopes), "text/html; charset=utf-8");
        });
    }
}

// Every aggregated endpoint goes through this one handler.
app.Map("/bff/{**path}", async (HttpContext http, string? path, RecipeRouter router, CompositionExecutor executor, CancellationToken ct) =>
{
    var match = await router.MatchAsync(http.Request.Method, path ?? "", ct);
    var correlationId = InboundAuth.CorrelationIdFrom(http);
    if (match is null)
        return GatewayProblems.Result(ErrorBody.Problem("not_found", "There's no aggregated endpoint at this address.", 404, correlationId));

    var recipe = match.Recipe;
    http.Items[GatewayLog.EndpointItemKey] = recipe.Id; // for the request log line: the recipe id, never the raw path

    if (recipe.RequireAuthentication && http.User.Identity?.IsAuthenticated != true)
    {
        http.Response.Headers.WWWAuthenticate = "Bearer";
        return GatewayProblems.Result(ErrorBody.Problem("unauthenticated", "Sign in to use this endpoint.", 401, correlationId));
    }
    if (recipe.RequiredScope is { Length: > 0 } scope && !InboundAuth.HasScope(http.User, scope))
    {
        var forbidden = ErrorBody.Problem("insufficient_scope", "Your sign-in doesn't have permission to use this endpoint.", 403, correlationId);
        forbidden["requiredScope"] = scope;
        return GatewayProblems.Result(forbidden);
    }

    var caller = InboundAuth.CallerFrom(http);
    var inbound = new InboundRequest
    {
        Route = match.RouteValues,
        Query = http.Request.Query.ToDictionary(q => q.Key, q => (string?)q.Value.ToString(), StringComparer.OrdinalIgnoreCase),
        Headers = http.Request.Headers.ToDictionary(h => h.Key, h => (string?)h.Value.ToString(), StringComparer.OrdinalIgnoreCase)
    };

    var result = await executor.ExecuteAsync(recipe, inbound, caller, ExecutionMode.Gateway, ct);

    if (result.Partial) http.Response.Headers["X-Aggregator-Partial"] = "true";
    if (result.FromCache) http.Response.Headers["X-Aggregator-Cache"] = "hit";

    if (result.StatusCode < 400) return Results.Json(result.Body, statusCode: result.StatusCode);

    // Temporary problems: tell well-behaved clients to wait a little before trying again.
    if (result.Retryable && result.StatusCode is 502 or 503 or 504) http.Response.Headers.RetryAfter = "5";
    return GatewayProblems.Result(result.Body, result.StatusCode);
}).RequireRateLimiting("per-caller");

// Development-only token issuer so QA can call the gateway without an identity provider.
if (devTokenEnabled)
{
    app.MapGet("/dev/token", (string? sub, string? scope) =>
        Results.Ok(new { access_token = InboundAuth.CreateDevToken(builder.Configuration.GetSection("Auth"), sub ?? "dev-user", scope ?? "customer.read") }));
}

app.Run();

/// <summary>The last OpenAPI document built, for one config version and address.</summary>
sealed record CachedSpec(string Version, string Server, string Json);

public partial class Program;
