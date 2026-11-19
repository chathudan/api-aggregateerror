using System.Text.Json.Nodes;

namespace ApiAggregator.Engine.Expressions;

/// <summary>Reads a property from a JSON object by a dotted name such as "customer.id". No arrays, no code.</summary>
internal static class JsonFields
{
    /// <summary>The value, or null when any part of the path is missing (or the value is JSON null).</summary>
    public static JsonNode? Get(JsonNode? node, string dottedPath)
    {
        var current = node;
        foreach (var name in dottedPath.Split('.', StringSplitOptions.TrimEntries))
        {
            // Same matching as the path language: exact name first, then ignoring case.
            if (current is not JsonObject obj) return null;
            current = ValuePath.GetProperty(obj, name);
        }
        return current;
    }
}
