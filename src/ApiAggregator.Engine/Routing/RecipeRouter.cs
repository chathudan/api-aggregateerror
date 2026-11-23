using ApiAggregator.Engine.Config;
using ApiAggregator.Engine.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Template;

namespace ApiAggregator.Engine.Routing;

public sealed record RouteMatch(Recipe Recipe, IReadOnlyDictionary<string, string?> RouteValues);

/// <summary>
/// Maps "GET /customer/C-1/overview" to the published recipe whose route is "customer/{id}/overview".
/// The route table is rebuilt only when configuration changes; matching is in-memory.
/// With thousands of endpoints, a cheap check (same number of segments, same literal words) skips the ones that can't
/// match before the framework's matcher runs, so a request costs microseconds and almost no memory.
/// </summary>
public sealed class RecipeRouter(IConfigStore store)
{
    private volatile RouteTable? _table;

    public async ValueTask<RouteMatch?> MatchAsync(string method, string path, CancellationToken ct)
    {
        var config = await store.GetSnapshotAsync(ct);
        var table = _table;
        if (table is null || table.Version != config.Version) _table = table = RouteTable.Build(config);

        var trimmed = path.Trim('/');
        var segments = trimmed.Split('/');
        // An empty segment ("a//b") is unusual: skip the shortcut and let the framework's matcher decide for every entry.
        var exact = !segments.Contains("");
        var pathString = new PathString("/" + trimmed);
        foreach (var entry in table.Candidates(method, exact ? segments.Length : -1))
        {
            if (exact && !entry.CouldMatch(segments)) continue;
            var values = new RouteValueDictionary();
            if (entry.Matcher.TryMatch(pathString, values))
                return new RouteMatch(entry.Recipe, values.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString(), StringComparer.OrdinalIgnoreCase));
        }
        return null;
    }

    /// <summary>One published route. Simple routes (each segment one literal or one plain parameter) can be pre-checked.</summary>
    private sealed class Entry
    {
        public required Recipe Recipe { get; init; }
        public required TemplateMatcher Matcher { get; init; }
        public required int Literals { get; init; }
        public required int Parameters { get; init; }

        /// <summary>Segment count, or -1 when the route has an optional or catch-all part (always checked in full).</summary>
        public required int SegmentCount { get; init; }

        /// <summary>(position, word) for every literal segment of a simple route.</summary>
        public required (int Index, string Text)[] LiteralSegments { get; init; }

        public bool CouldMatch(string[] segments)
        {
            if (SegmentCount < 0) return true;
            foreach (var (index, text) in LiteralSegments)
                if (!string.Equals(segments[index], text, StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }
    }

    private sealed record RouteTable(string Version, Dictionary<string, Dictionary<int, Entry[]>> ByMethod, Dictionary<string, Entry[]> ComplexOnly)
    {
        private static readonly Entry[] None = [];

        /// <summary>The entries worth trying, most specific first. -1 segments: every entry of the method.</summary>
        public Entry[] Candidates(string method, int segments)
        {
            if (!ByMethod.TryGetValue(method, out var bySegments)) return None;
            if (bySegments.TryGetValue(segments, out var list)) return list;
            return ComplexOnly.GetValueOrDefault(method) ?? None;
        }

        public static RouteTable Build(ConfigSnapshot config)
        {
            var entries = config.Recipes
                .Where(r => r.Status == RecipeStatus.Published)
                .Select(Describe)
                // Most specific first: "customer/me/overview" wins over "customer/{id}/overview".
                .OrderByDescending(e => e.Literals).ThenBy(e => e.Parameters)
                .ToList();

            var byMethod = new Dictionary<string, Dictionary<int, Entry[]>>(StringComparer.OrdinalIgnoreCase);
            var complexOnly = new Dictionary<string, Entry[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var method in entries.GroupBy(e => e.Recipe.Method, StringComparer.OrdinalIgnoreCase))
            {
                var list = method.ToList(); // keeps the order above
                var complex = list.Where(e => e.SegmentCount < 0).ToArray();
                var buckets = list.Where(e => e.SegmentCount >= 0).Select(e => e.SegmentCount).Distinct()
                    .ToDictionary(n => n, n => list.Where(e => e.SegmentCount == n || e.SegmentCount < 0).ToArray());
                buckets[-1] = list.ToArray(); // unusual paths: try everything
                byMethod[method.Key] = buckets;
                complexOnly[method.Key] = complex;
            }
            return new RouteTable(config.Version, byMethod, complexOnly);
        }

        private static Entry Describe(Recipe recipe)
        {
            var template = TemplateParser.Parse(recipe.Route.Trim('/'));
            var simple = template.Segments.All(s => s.IsSimple && (s.Parts[0].IsLiteral || s.Parts[0] is { IsParameter: true, IsOptional: false, IsCatchAll: false, DefaultValue: null }));
            return new Entry
            {
                Recipe = recipe,
                Matcher = new TemplateMatcher(template, new RouteValueDictionary()),
                Literals = template.Segments.Count(s => s.IsSimple && s.Parts[0].IsLiteral),
                Parameters = template.Parameters.Count,
                SegmentCount = simple ? template.Segments.Count : -1,
                LiteralSegments = simple
                    ? template.Segments.Select((s, i) => (s, i)).Where(x => x.s.Parts[0].IsLiteral).Select(x => (x.i, x.s.Parts[0].Text!)).ToArray()
                    : []
            };
        }
    }
}
