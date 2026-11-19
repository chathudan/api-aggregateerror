using ApiAggregator.Engine.Logging;
using System.Diagnostics;
using ApiAggregator.Hosting;
using Microsoft.AspNetCore.Routing;

namespace ApiAggregator.Gateway;

/// <summary>Gateway log lines (event ids 2000s). Same rule as the engine: ids, statuses and timings only.</summary>
internal static partial class GatewayLog
{
    [LoggerMessage(EventId = 2000, Level = LogLevel.Information,
        Message = "{Method} {Endpoint} -> {Status} in {DurationMs} ms; subject {Subject}; correlation {CorrelationId}")]
    public static partial void RequestCompleted(this ILogger logger, string method, string endpoint, int status, long durationMs, string subject, string correlationId);

    [LoggerMessage(EventId = 2001, Level = LogLevel.Warning,
        Message = "Browser origin {Origin} is not allowed to call /bff; method {Method}; preflight {Preflight}. Add it on the Browser access page if it should be.")]
    public static partial void BrowserOriginRejected(this ILogger logger, string origin, string method, bool preflight);

    [LoggerMessage(EventId = 2002, Level = LogLevel.Warning,
        Message = "Browser origin {Origin} from the admin app is not used: {Reason}")]
    public static partial void BrowserOriginSkipped(this ILogger logger, string origin, string reason);

    /// <summary>HttpContext.Items key the /bff handler uses to say which recipe answered.</summary>
    public const string EndpointItemKey = "ApiAggregator.Endpoint";
}

/// <summary>
/// Writes ONE log line per request. The endpoint is the recipe id (or the route pattern), never the raw
/// path: paths contain ids that can be personal data. Health checks and the live-log endpoint are skipped (they are polled constantly).
/// </summary>
internal sealed class RequestLogMiddleware(RequestDelegate next, ILogger<RequestLogMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext http)
    {
        // Health checks run every few seconds, and the Logs page polls /_admin/logs: logging either would fill the log with itself.
        if (http.Request.Path.StartsWithSegments("/health") || http.Request.Path.StartsWithSegments("/_admin/logs"))
        {
            await next(http);
            return;
        }

        var clock = Stopwatch.StartNew();
        var correlationId = InboundAuth.CorrelationIdFrom(http);

        // Everything logged while this request runs (including the engine's lines) carries the correlation id.
        using var scope = logger.BeginScope(new LogScope(("CorrelationId", correlationId)));
        try { await next(http); }
        finally
        {
            var endpoint = (http.Items.TryGetValue(GatewayLog.EndpointItemKey, out var recipeId) ? recipeId as string : null)
                ?? (http.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText
                ?? "unmatched";
            logger.RequestCompleted(http.Request.Method, endpoint, http.Response.StatusCode, clock.ElapsedMilliseconds,
                http.User.FindFirst("sub")?.Value ?? "anonymous", correlationId);
        }
    }
}
