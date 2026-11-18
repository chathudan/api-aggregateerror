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
/// </summary>
public sealed class RecipeRouter(IConfigStore store)
{
    private volatile RouteTable? _table;

    public async ValueTask<RouteMatch?> MatchAsync(string method, string path, CancellationToken ct)
    {
        var config = await store.GetSnapshotAsync(ct);
        var table = _table;
        if (table is null || table.Version != config.Version) _table = table = RouteTable.Build(config);

        var pathString = new PathString("/" + path.TrimStart('/'));
        foreach (var entry in table.Entries)
        {
            if (!string.Equals(entry.Recipe.Method, method, StringComparison.OrdinalIgnoreCase)) continue;
            var values = new RouteValueDictionary();
            if (entry.Matcher.TryMatch(pathString, values))
                return new RouteMatch(entry.Recipe, values.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString(), StringComparer.OrdinalIgnoreCase));
        }
        return null;
    }

    private sealed record Entry(Recipe Recipe, TemplateMatcher Matcher, int Literals, int Parameters);

    private sealed record RouteTable(string Version, IReadOnlyList<Entry> Entries)
    {
        public static RouteTable Build(ConfigSnapshot config)
        {
            var entries = config.Recipes
                .Where(r => r.Status == RecipeStatus.Published)
                .Select(r =>
                {
                    var template = TemplateParser.Parse(r.Route.Trim('/'));
                    var literals = template.Segments.Count(s => s.IsSimple && s.Parts[0].IsLiteral);
                    return new Entry(r, new TemplateMatcher(template, new RouteValueDictionary()), literals, template.Parameters.Count);
                })
                // Most specific first: "customer/me/overview" wins over "customer/{id}/overview".
                .OrderByDescending(e => e.Literals).ThenBy(e => e.Parameters)
                .ToList();
            return new RouteTable(config.Version, entries);
        }
    }
}
