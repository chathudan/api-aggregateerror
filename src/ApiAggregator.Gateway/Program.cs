// API Aggregator gateway: serves every PUBLISHED recipe under /bff/...
// Read docs/architecture/01-overview.md first.

using System.Threading.RateLimiting;
using ApiAggregator.Engine;
using ApiAggregator.Engine.Config;
using ApiAggregator.Engine.Execution;
using ApiAggregator.Engine.Routing;
using ApiAggregator.Gateway;
using ApiAggregator.Hosting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Http.Resilience;

var builder = WebApplication.CreateBuilder(args);

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
builder.Services.AddAuthorization();

// ---------------------------------------------------------------- abuse protection
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("per-caller", http => RateLimitPartition.GetFixedWindowLimiter(
        http.User.FindFirst("sub")?.Value ?? http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = builder.Configuration.GetValue("Gateway:RequestsPerMinutePerCaller", 600),
            Window = TimeSpan.FromMinutes(1)
        }));
});

builder.Services.AddHealthChecks();
builder.Services.AddProblemDetails();

var app = builder.Build();

app.UseExceptionHandler();
app.Use(async (http, next) =>
{
    http.Response.Headers.XContentTypeOptions = "nosniff";
    http.Response.Headers.CacheControl = "no-store"; // responses may hold personal data: never cache in browsers or proxies
    http.Response.Headers["Referrer-Policy"] = "no-referrer";
    await next();
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

// OpenAPI document for all published aggregated endpoints (web/mobile teams generate clients from it).
app.MapGet("/bff/openapi.json", async (IConfigStore store, CancellationToken ct) =>
    Results.Json(GatewayOpenApi.Build(await store.GetSnapshotAsync(ct)), statusCode: 200));

// Every aggregated endpoint goes through this one handler.
app.Map("/bff/{**path}", async (HttpContext http, string? path, RecipeRouter router, CompositionExecutor executor, CancellationToken ct) =>
{
    var match = await router.MatchAsync(http.Request.Method, path ?? "", ct);
    if (match is null) return Results.Json(new { title = "There's no aggregated endpoint at this address." }, statusCode: 404);

    var recipe = match.Recipe;
    if (recipe.RequireAuthentication && http.User.Identity?.IsAuthenticated != true) return Results.Unauthorized();
    if (recipe.RequiredScope is { Length: > 0 } scope && !InboundAuth.HasScope(http.User, scope)) return Results.Forbid();

    var caller = InboundAuth.CallerFrom(http);
    var inbound = new InboundRequest
    {
        Route = match.RouteValues,
        Query = http.Request.Query.ToDictionary(q => q.Key, q => (string?)q.Value.ToString(), StringComparer.OrdinalIgnoreCase),
        Headers = http.Request.Headers.ToDictionary(h => h.Key, h => (string?)h.Value.ToString(), StringComparer.OrdinalIgnoreCase)
    };

    var result = await executor.ExecuteAsync(recipe, inbound, caller, ExecutionMode.Gateway, ct);

    http.Response.Headers["X-Correlation-Id"] = caller.CorrelationId;
    if (result.Partial) http.Response.Headers["X-Aggregator-Partial"] = "true";
    if (result.FromCache) http.Response.Headers["X-Aggregator-Cache"] = "hit";
    return Results.Json(result.Body, statusCode: result.StatusCode);
}).RequireRateLimiting("per-caller");

// Development-only token issuer so QA can call the gateway without an identity provider.
if (app.Environment.IsDevelopment() && !string.IsNullOrEmpty(builder.Configuration["Auth:DevSigningKey"]))
{
    app.MapGet("/dev/token", (string? sub, string? scope) =>
        Results.Ok(new { access_token = InboundAuth.CreateDevToken(builder.Configuration.GetSection("Auth"), sub ?? "dev-user", scope ?? "customer.read") }));
}

app.Run();

public partial class Program;
