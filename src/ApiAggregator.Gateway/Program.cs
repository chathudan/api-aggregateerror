// API Aggregator gateway: serves every PUBLISHED recipe under /bff/...
// Read docs/architecture/01-overview.md first.

using System.Threading.RateLimiting;
using ApiAggregator.Engine;
using ApiAggregator.Engine.Authoring;
using ApiAggregator.Engine.BrowserAccess;
using ApiAggregator.Engine.Config;
using ApiAggregator.Engine.Execution;
using ApiAggregator.Engine.Logging.Live;
using ApiAggregator.Engine.Routing;
using ApiAggregator.Gateway;
using ApiAggregator.Hosting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Cors.Infrastructure;

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
builder.Services.AddDownstreamResilience(builder.Configuration); // retries, timeouts, one circuit breaker per service (ADR 0017)

// ---------------------------------------------------------------- inbound auth (JWT)
InboundAuth.ValidateSettings(builder.Configuration.GetSection("Auth"), builder.Environment); // fail at startup, not on the first request
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o => InboundAuth.Configure(o, builder.Configuration.GetSection("Auth"), builder.Environment));
// Who may read the live logs: the same people who author or publish recipes.
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("log-reader", p => p.RequireAssertion(c => InboundAuth.HasScope(c.User, "aggregator.author") || InboundAuth.HasScope(c.User, "aggregator.publisher")));

// ---------------------------------------------------------------- browser access (CORS, ADR 0015)
// /bff/* only. Origins: Gateway:Cors:AllowedOrigins (operations) plus those added in the admin app (config/cors.json),
// within Gateway:Cors:AllowedOriginPatterns. Never credentials. Invalid settings stop the gateway here, with a plain message.
var corsSettings = builder.Configuration.GetSection(BrowserAccessSettings.Section).Get<BrowserAccessSettings>() ?? new BrowserAccessSettings();
OriginRules.ValidateSettings(corsSettings, builder.Environment.IsDevelopment());
builder.Services.Configure<BrowserAccessSettings>(builder.Configuration.GetSection(BrowserAccessSettings.Section));
builder.Services.AddCors();
builder.Services.AddSingleton<ICorsPolicyProvider, BffCorsPolicyProvider>();

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
        // The CORS answer depends on the Origin header. The framework only says so when several origins are allowed.
        if (http.Request.Path.StartsWithSegments("/bff") && !h.Vary.Any(v => v?.Contains("Origin", StringComparison.OrdinalIgnoreCase) == true))
            h.Append("Vary", "Origin");
        return Task.CompletedTask;
    });
    await next();
});
app.UseExceptionHandler();
// Routing, then CORS (it answers preflights itself, so they never reach authentication, the rate limiter or the
// catch-all), then authentication, authorization and the rate limiter. CORS headers on actual requests are added
// as the response starts, so error responses (including the exception handler's) keep them.
app.UseRouting();
app.UseCors();
// Overload protection (ADR 0017): at most Gateway:MaxConcurrentRequests /bff requests at once. Beyond that the gateway
// answers 503 straight away (cheap, with Retry-After) instead of queueing work until memory or threads run out.
// After CORS, so browser apps can read the 503 and preflights are never counted; before authentication, which costs CPU.
var inFlight = new InFlightRequests();
var maxInFlight = builder.Configuration.GetValue("Gateway:MaxConcurrentRequests", 1000);
app.Use(async (http, next) =>
{
    if (!http.Request.Path.StartsWithSegments("/bff")) { await next(); return; }
    if (inFlight.Enter() > maxInFlight)
    {
        inFlight.Leave();
        inFlight.Rejected();
        http.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        http.Response.Headers.RetryAfter = "5";
        await GatewayProblems.WriteAsync(http, ErrorBody.Problem("overloaded", "The gateway is busy. Try again in a few seconds.", 503,
            InboundAuth.CorrelationIdFrom(http), retryable: true), http.RequestAborted);
        return;
    }
    try { await next(); }
    finally { inFlight.Leave(); }
});
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

// Runtime numbers (memory, collections, threads, requests in flight) for the benchmark and operations. Off by default.
if (builder.Configuration.GetValue("Gateway:Diagnostics:Enabled", false))
    app.MapRuntimeDiagnostics("/_admin/runtime", "log-reader", inFlight);

// OpenAPI document for all published aggregated endpoints (web/mobile teams generate clients from it),
// and the Swagger UI page that shows it. Both are anonymous: callers need the spec before they have a token.
// Literal routes win over the "/bff/{**path}" catch-all below, and they skip the per-caller rate limit
// (which is attached to the catch-all only).
var devTokenEnabled = app.Environment.IsDevelopment() && !string.IsNullOrEmpty(builder.Configuration["Auth:DevSigningKey"]);

if (builder.Configuration.GetValue("Gateway:OpenApi:Enabled", true))
{
    // Rebuilt only when the config changes; a few addresses at most (the Host header is the caller's to choose).
    var specs = new OpenApiSpecCache(builder.Configuration.GetValue("Gateway:OpenApi:IncludeInternalDetails", false));

    app.MapGet("/bff/openapi.json", async (HttpContext http, IConfigStore store, CancellationToken ct) =>
    {
        var snapshot = await store.GetSnapshotAsync(ct);
        return Results.Content(specs.Get(snapshot, $"{http.Request.Scheme}://{http.Request.Host}"), "application/json");
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
}).RequireRateLimiting("per-caller").RequireCors(BffCorsPolicyProvider.PolicyName);

// Development-only token issuer so QA can call the gateway without an identity provider.
if (devTokenEnabled)
{
    app.MapGet("/dev/token", (string? sub, string? scope) =>
        Results.Ok(new { access_token = InboundAuth.CreateDevToken(builder.Configuration.GetSection("Auth"), sub ?? "dev-user", scope ?? "customer.read") }));
}

app.Run();

public partial class Program;
