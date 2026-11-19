using System.Collections.Concurrent;
using ApiAggregator.Engine.BrowserAccess;
using ApiAggregator.Engine.Config;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Extensions.Options;

namespace ApiAggregator.Gateway;

/// <summary>
/// The CORS policy for /bff/* (ADR 0015): the operations origins plus the ones added in the admin app, the methods
/// of the published recipes and the headers their inputs declare. Rebuilt whenever the config version changes
/// (a recipe published, an origin added or removed), so no restart is needed. Never allows credentials.
/// The CORS middleware asks for it only when a request carries an Origin header.
/// </summary>
internal sealed class BffCorsPolicyProvider(
    IConfigStore store,
    IOptions<BrowserAccessSettings> settings,
    IHostEnvironment env,
    ILogger<BffCorsPolicyProvider> logger) : ICorsPolicyProvider
{
    public const string PolicyName = "bff";

    private const int MaxRemembered = 500;
    private static readonly long LogEveryMs = (long)TimeSpan.FromMinutes(1).TotalMilliseconds;

    private sealed record Built(BrowserAccessPolicy Access, CorsPolicy Cors);

    private Built? _current;    // replaced as a whole, so reading it is safe across requests
    private Built? _withoutConfig;
    private readonly ConcurrentDictionary<string, long> _lastRejectionLog = new(StringComparer.Ordinal);

    public async Task<CorsPolicy?> GetPolicyAsync(HttpContext context, string? policyName)
    {
        if (policyName != PolicyName) return null;
        var built = await CurrentAsync(context.RequestAborted);
        LogIfRejected(context, built.Access);
        return built.Cors;
    }

    private async Task<Built> CurrentAsync(CancellationToken ct)
    {
        ConfigSnapshot snapshot;
        try { snapshot = await store.GetSnapshotAsync(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The request is about to fail anyway (it needs the config too). Keep the last good policy so its error
            // response still carries CORS headers and the page can read the error, instead of a CORS failure.
            return _current ?? (_withoutConfig ??= Build(new ConfigSnapshot([], [], [], "unavailable")));
        }

        var hit = _current;
        if (hit is not null && hit.Access.ConfigVersion == snapshot.Version) return hit;

        var next = Build(snapshot);
        _current = next;
        foreach (var skipped in next.Access.Skipped)
            logger.BrowserOriginSkipped(OriginRules.ForLog(skipped.Origin), skipped.Reason);
        return next;
    }

    private Built Build(ConfigSnapshot snapshot)
    {
        var access = BrowserAccessPolicy.From(snapshot, settings.Value, env.IsDevelopment());
        var cors = new CorsPolicyBuilder()
            .WithOrigins(access.Origins.Select(o => o.Origin).ToArray())
            .WithMethods(access.Methods.ToArray())
            .WithHeaders(access.Headers.ToArray())
            .WithExposedHeaders(BrowserAccessPolicy.ExposedHeaders.ToArray())
            .SetPreflightMaxAge(TimeSpan.FromSeconds(access.PreflightMaxAgeSeconds))
            .DisallowCredentials() // callers send a bearer token; cookies are never accepted cross-origin
            .Build();
        return new Built(access, cors);
    }

    /// <summary>
    /// One line per rejected origin per minute (so a flood can't fill the Live logs), with the origin only:
    /// never the path, query or other headers. The gateway's own pages (the API docs) are same-origin: not logged.
    /// </summary>
    private void LogIfRejected(HttpContext context, BrowserAccessPolicy access)
    {
        var header = context.Request.Headers.Origin.ToString();
        var origin = OriginRules.ForLog(header);
        if (access.Allows(origin)) return;
        if (string.Equals(header, $"{context.Request.Scheme}://{context.Request.Host}", StringComparison.OrdinalIgnoreCase)) return;

        var now = Environment.TickCount64;
        if (_lastRejectionLog.TryGetValue(origin, out var last) && now - last < LogEveryMs) return;
        if (_lastRejectionLog.Count >= MaxRemembered) _lastRejectionLog.Clear();
        _lastRejectionLog[origin] = now;

        var preflight = HttpMethods.IsOptions(context.Request.Method) && context.Request.Headers.ContainsKey("Access-Control-Request-Method");
        logger.BrowserOriginRejected(origin, context.Request.Method, preflight);
    }
}
