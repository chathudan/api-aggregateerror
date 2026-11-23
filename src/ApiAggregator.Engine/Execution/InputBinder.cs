using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ApiAggregator.Engine.Auth;
using ApiAggregator.Engine.Model;

namespace ApiAggregator.Engine.Execution;

/// <summary>
/// Validates and converts the caller's inputs. Only DECLARED inputs get through;
/// everything else the caller sends is ignored and never reaches a downstream service.
/// </summary>
public static class InputBinder
{
    // Input patterns, parsed once each. Bounded: cleared if config edits ever pile up more than this many patterns.
    private static readonly ConcurrentDictionary<string, Regex> Patterns = new(StringComparer.Ordinal);
    private const int MaxCachedPatterns = 1000;

    // Only for patterns the non-backtracking engine can't run (lookarounds, backreferences). Generous, because the
    // clock also runs during garbage collections and while the thread waits for a CPU: the benchmark caught a 50 ms
    // limit turning valid values into 400s under load.
    private static readonly TimeSpan BacktrackingTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>Builds the "request" object used by expressions: request.route.*, request.query.*, request.header.*, request.claims.*</summary>
    public static (JsonObject Request, List<string> Errors) Bind(Recipe recipe, InboundRequest inbound, CallerContext caller)
    {
        var route = new JsonObject();
        var query = new JsonObject();
        var header = new JsonObject();
        var errors = new List<string>();

        foreach (var (key, def) in recipe.Inputs)
        {
            var parts = key.Split('.', 2);
            if (parts.Length != 2) continue;
            var (source, name) = (parts[0].ToLowerInvariant(), parts[1]);

            var raw = source switch
            {
                "route" => inbound.Route.GetValueOrDefault(name),
                "query" => Lookup(inbound.Query, name),
                "header" => Lookup(inbound.Headers, name),
                _ => null
            };
            if (string.IsNullOrEmpty(raw)) raw = def.Default;

            if (string.IsNullOrEmpty(raw))
            {
                if (def.Required || source == "route") errors.Add($"'{name}' is required.");
                continue;
            }

            var (value, error) = Convert(name, raw, def);
            if (error is not null) { errors.Add(error); continue; }

            var target = source switch { "route" => route, "query" => query, _ => header };
            target[name] = value;
        }

        var request = new JsonObject
        {
            ["route"] = route,
            ["query"] = query,
            ["header"] = header,
            ["headers"] = header.DeepClone(), // alias, so both request.header.X and request.headers.X work
            ["claims"] = caller.Claims.DeepClone()
        };
        return (request, errors);
    }

    /// <summary>
    /// The pattern as a Regex. Non-backtracking when possible: its time grows linearly with the input, so a pattern can't
    /// be made to run for ages (ReDoS) and needs no time limit.
    /// </summary>
    private static Regex Pattern(string pattern)
    {
        if (Patterns.TryGetValue(pattern, out var cached)) return cached;
        if (Patterns.Count >= MaxCachedPatterns) Patterns.Clear();
        Regex regex;
        try { regex = new Regex(pattern, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking); }
        catch (NotSupportedException) { regex = new Regex(pattern, RegexOptions.CultureInvariant, BacktrackingTimeout); }
        return Patterns.GetOrAdd(pattern, regex);
    }

    private static string? Lookup(IReadOnlyDictionary<string, string?> values, string name)
    {
        if (values.TryGetValue(name, out var v)) return v;
        foreach (var (k, value) in values)
            if (string.Equals(k, name, StringComparison.OrdinalIgnoreCase)) return value;
        return null;
    }

    private static (JsonNode? Value, string? Error) Convert(string name, string raw, InputDef def)
    {
        if (raw.Length > def.MaxLength) return (null, $"'{name}' is longer than {def.MaxLength} characters.");
        if (raw.Any(char.IsControl)) return (null, $"'{name}' contains invalid characters.");

        if (def.Enum is { Length: > 0 } && !def.Enum.Contains(raw, StringComparer.OrdinalIgnoreCase))
            return (null, $"'{name}' must be one of: {string.Join(", ", def.Enum)}.");

        if (def.Pattern is not null)
        {
            try
            {
                if (!Pattern(def.Pattern).IsMatch(raw))
                    return (null, $"'{name}' is not in the expected format.");
            }
            catch (RegexMatchTimeoutException) { return (null, $"'{name}' is not in the expected format."); }
        }

        switch (def.Type)
        {
            case "int":
                if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return (null, $"'{name}' must be a whole number.");
                return InRange(name, l, def) ?? (JsonValue.Create(l), null);
            case "number":
                if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || !double.IsFinite(d)) return (null, $"'{name}' must be a number.");
                return InRange(name, d, def) ?? (JsonValue.Create(d), null);
            case "bool":
                return bool.TryParse(raw, out var b) ? (JsonValue.Create(b), null) : (null, $"'{name}' must be true or false.");
            case "date":
                return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _)
                    ? (JsonValue.Create(raw), null)
                    : (null, $"'{name}' must be a date such as 2026-11-18.");
            default:
                return (JsonValue.Create(raw), null);
        }
    }

    private static (JsonNode?, string?)? InRange(string name, double value, InputDef def)
    {
        if (def.Min is { } min && value < min) return (null, $"'{name}' must be at least {min}.");
        if (def.Max is { } max && value > max) return (null, $"'{name}' must be at most {max}.");
        return null;
    }
}
