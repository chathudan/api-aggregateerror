using System.Globalization;
using ApiAggregator.Engine.Config;

namespace ApiAggregator.Engine.BrowserAccess;

public enum OriginSource { Operations, Admin }

/// <summary>An origin browsers may call /bff/* from, and where it came from.</summary>
public sealed record AllowedOrigin(string Origin, OriginSource Source, string? Note, DateTimeOffset? AddedAt, string? AddedBy, string? AddedByName);

/// <summary>An admin entry that is NOT applied, because it no longer fits the rules or the operations limits.</summary>
public sealed record SkippedOrigin(string Origin, string Reason);

/// <summary>The answer to "may a page on this origin call the gateway?".</summary>
public sealed record OriginTest(bool Allowed, string Reason);

/// <summary>
/// What browsers may do with /bff/* for one config version (ADR 0015): one description used by the gateway's CORS
/// policy, the admin app's Browser access page and "Test an origin". Never allows credentials.
/// </summary>
public sealed class BrowserAccessPolicy
{
    /// <summary>Always allowed, whatever recipes ask for.</summary>
    public static readonly IReadOnlyList<string> BaseHeaders = ["Authorization", "Content-Type", "X-Correlation-Id"];

    /// <summary>Response headers a page may read.</summary>
    public static readonly IReadOnlyList<string> ExposedHeaders = ["X-Correlation-Id", "X-Aggregator-Partial", "X-Aggregator-Cache", "Retry-After"];

    private readonly Dictionary<string, AllowedOrigin> _byOrigin;
    private readonly Dictionary<string, SkippedOrigin> _skippedByOrigin;

    private BrowserAccessPolicy(string configVersion, List<AllowedOrigin> origins, List<SkippedOrigin> skipped, IReadOnlyList<string> patterns,
        IReadOnlyList<string> methods, IReadOnlyList<string> headers, int maxAgeSeconds)
    {
        ConfigVersion = configVersion;
        Origins = origins;
        Skipped = skipped;
        Patterns = patterns;
        Methods = methods;
        Headers = headers;
        PreflightMaxAgeSeconds = maxAgeSeconds;
        _byOrigin = origins.ToDictionary(o => o.Origin, StringComparer.Ordinal);
        _skippedByOrigin = skipped.GroupBy(s => s.Origin).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
    }

    public string ConfigVersion { get; }
    public IReadOnlyList<AllowedOrigin> Origins { get; }
    public IReadOnlyList<SkippedOrigin> Skipped { get; }
    public IReadOnlyList<string> Patterns { get; }
    public IReadOnlyList<string> Methods { get; }
    public IReadOnlyList<string> Headers { get; }
    public int PreflightMaxAgeSeconds { get; }

    /// <summary>Exact, canonical match only.</summary>
    public bool Allows(string? origin) => origin is not null && _byOrigin.ContainsKey(origin);

    /// <summary>
    /// Builds the policy. Admin entries are checked again here (the gateway may have stricter settings than the app
    /// that added them): those that don't pass are skipped, with the reason.
    /// </summary>
    public static BrowserAccessPolicy From(ConfigSnapshot snapshot, BrowserAccessSettings settings, bool isDevelopment)
    {
        var origins = OriginRules.OperationsOrigins(settings, isDevelopment)
            .Order(StringComparer.Ordinal)
            .Select(o => new AllowedOrigin(o, OriginSource.Operations, null, null, null, null))
            .ToList();
        var skipped = new List<SkippedOrigin>();
        var seen = origins.Select(o => o.Origin).ToHashSet(StringComparer.Ordinal);
        var adminCount = 0;

        foreach (var entry in snapshot.BrowserOrigins)
        {
            var check = OriginRules.CheckOrigin(entry.Origin, isDevelopment);
            var reason = check.Problem
                ?? (check.Value != entry.Origin ? "It isn't written in the exact form browsers send. Remove it and add it again." : null)
                ?? OriginRules.PatternProblem(check.Value!, settings)
                ?? (adminCount >= OriginRules.MaxAdminEntries ? $"Only the first {OriginRules.MaxAdminEntries} origins added here are used." : null);
            if (reason is not null)
            {
                skipped.Add(new SkippedOrigin(entry.Origin, reason));
                continue;
            }
            if (!seen.Add(check.Value!)) continue; // operations already allow it
            adminCount++;
            origins.Add(new AllowedOrigin(check.Value!, OriginSource.Admin, entry.Note, entry.AddedAt, entry.AddedBy, entry.AddedByName));
        }

        var methods = snapshot.Recipes.Select(r => r.Method.ToUpperInvariant()).Append("OPTIONS")
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

        var headers = BaseHeaders.Concat(snapshot.Recipes
                .SelectMany(r => r.Inputs.Keys)
                .Where(k => k.StartsWith("header.", StringComparison.OrdinalIgnoreCase))
                .Select(k => k["header.".Length..])
                .Where(IsHeaderName))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new BrowserAccessPolicy(snapshot.Version, origins, skipped, OriginRules.Patterns(settings), methods, headers,
            Math.Clamp(settings.PreflightMaxAgeSeconds, 0, 86_400));
    }

    /// <summary>"Test an origin" in the admin app: allowed or not, and why, in plain English (the reason doesn't repeat the verdict).</summary>
    public OriginTest Test(string? origin, bool isDevelopment)
    {
        var check = OriginRules.CheckOrigin(origin, isDevelopment);
        if (!check.Ok) return new OriginTest(false, check.Problem!);
        var canonical = check.Value!;

        if (_byOrigin.TryGetValue(canonical, out var allowed))
            return new OriginTest(true, allowed.Source == OriginSource.Operations
                ? "Operations set it in the app settings."
                : $"Added here{By(allowed)}.");
        if (_skippedByOrigin.TryGetValue(canonical, out var skipped))
            return new OriginTest(false, $"It's on the list but not used: {skipped.Reason}");
        if (OriginRules.PatternProblem(canonical, PatternsOnly) is { } patternProblem)
            return new OriginTest(false, $"It isn't on the list, and it can't be added: {patternProblem}");
        return new OriginTest(false, "It isn't on the list. Someone with the admin role can add it.");
    }

    private BrowserAccessSettings PatternsOnly => new() { AllowedOriginPatterns = Patterns.ToArray() };

    private static string By(AllowedOrigin o)
    {
        var who = o.AddedByName ?? o.AddedBy;
        var when = o.AddedAt?.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
        return (who, when) switch
        {
            (not null, not null) => $" by {who} on {when}",
            (not null, null) => $" by {who}",
            (null, not null) => $" on {when}",
            _ => ""
        };
    }

    /// <summary>An HTTP header name (RFC 9110 token): anything else is never offered to browsers.</summary>
    private static bool IsHeaderName(string name) =>
        name.Length is > 0 and <= 100 && name.All(c => char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-.^_`|~".Contains(c));
}
