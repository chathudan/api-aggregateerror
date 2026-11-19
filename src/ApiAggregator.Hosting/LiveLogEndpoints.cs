using ApiAggregator.Engine.Logging.Live;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ApiAggregator.Hosting;

/// <summary>
/// The live-log endpoint of the gateway (read by the admin app's Logs page) and of the admin app (/admin/logs, for scripts).
/// It only serves what the in-memory <see cref="LiveLogBuffer"/> holds: log lines that already exclude
/// request values and secrets. It is protected by the authorization policy the host passes in.
/// </summary>
public static class LiveLogEndpoints
{
    private const int MaxWaitSeconds = 25;

    /// <summary>
    /// GET {pattern}?after={seq}&amp;wait={seconds 0-25}&amp;max={n}
    /// With wait > 0 and nothing new, the call waits (long poll) until a line arrives or the time is up.
    /// </summary>
    public static IEndpointRouteBuilder MapLiveLogs(this IEndpointRouteBuilder app, string pattern, string policy)
    {
        app.MapGet(pattern, async (HttpContext http, IHostEnvironment env, long? after, int? wait, int? max) =>
        {
            http.Response.Headers.CacheControl = "no-store";

            // The buffer only exists when LiveLogs:Enabled is true.
            var buffer = http.RequestServices.GetService<LiveLogBuffer>();
            if (buffer is null)
                return Results.Json(new { code = "live_logs_disabled", title = "Live logs are turned off for this process." }, statusCode: StatusCodes.Status404NotFound);

            var afterSeq = Math.Max(0, after ?? 0);
            var waitSeconds = Math.Clamp(wait ?? 0, 0, MaxWaitSeconds);
            var maxEntries = Math.Clamp(max ?? 500, 1, 1000);

            var entries = buffer.Read(afterSeq, maxEntries);
            if (entries.Count == 0 && waitSeconds > 0)
            {
                try { await buffer.WaitForNewAsync(afterSeq, TimeSpan.FromSeconds(waitSeconds), http.RequestAborted); }
                catch (OperationCanceledException) { return Results.Empty; } // the caller went away
                entries = buffer.Read(afterSeq, maxEntries);
            }

            return Results.Json(new { source = FriendlyName(env.ApplicationName), latest = buffer.LatestSeq, capacity = buffer.Capacity, entries });
        }).RequireAuthorization(policy);

        return app;
    }

    private static string FriendlyName(string applicationName) => applicationName switch
    {
        "ApiAggregator.Gateway" => "Gateway",
        "ApiAggregator.Admin" => "Admin",
        _ => applicationName
    };
}
