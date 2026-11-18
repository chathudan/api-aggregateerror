using System.Text.Json.Nodes;

namespace ApiAggregator.Engine.Expressions;

/// <summary>
/// Read-only view over everything a step may reference:
///   request.*        validated caller inputs
///   &lt;alias&gt;.*       results of earlier steps
///   item / index     current element inside forEach
///   ctx.*            correlationId, caller subject
/// </summary>
public sealed class Scope(IReadOnlyDictionary<string, JsonNode?> roots, Scope? parent = null) : IValueScope
{
    public JsonNode? GetRoot(string name) =>
        roots.TryGetValue(name, out var value) ? value : parent?.GetRoot(name);

    public Scope With(string name, JsonNode? value, string? name2 = null, JsonNode? value2 = null)
    {
        var extra = new Dictionary<string, JsonNode?>(StringComparer.Ordinal) { [name] = value };
        if (name2 is not null) extra[name2] = value2;
        return new Scope(extra, this);
    }
}
