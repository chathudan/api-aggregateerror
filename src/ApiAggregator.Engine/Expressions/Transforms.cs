using System.Text.Json.Nodes;

namespace ApiAggregator.Engine.Expressions;

/// <summary>A parsed "name(arg)" call, e.g. join(',') or default('n/a').</summary>
public sealed record TransformCall(string Name, string? Argument)
{
    public static TransformCall Parse(string text)
    {
        text = text.Trim();
        var open = text.IndexOf('(');
        if (open < 0) return new TransformCall(text.ToLowerInvariant(), null);
        if (!text.EndsWith(')')) throw new FormatException($"Transform '{text}' is missing ')'.");

        var arg = text[(open + 1)..^1].Trim();
        if (arg.Length >= 2 && (arg[0] == '\'' || arg[0] == '"') && arg[^1] == arg[0]) arg = arg[1..^1];
        return new TransformCall(text[..open].Trim().ToLowerInvariant(), arg);
    }
}

/// <summary>
/// A fixed, whitelisted set of value transforms. Deliberately small: anything
/// more complex belongs in a downstream service, not in configuration.
/// </summary>
public static class Transforms
{
    public static readonly IReadOnlySet<string> Names = new HashSet<string>
    {
        "default", "join", "first", "last", "count", "upper", "lower", "trim", "string", "distinct"
    };

    public static JsonNode? Apply(TransformCall call, JsonNode? value) => call.Name switch
    {
        "default" => IsEmpty(value) ? JsonValue.Create(call.Argument) : value,
        "join"    => value is JsonArray a ? JsonValue.Create(string.Join(call.Argument ?? ",", a.Select(JsonText.Of))) : value,
        "first"   => value is JsonArray f ? f.FirstOrDefault()?.DeepClone() : value,
        "last"    => value is JsonArray l ? l.LastOrDefault()?.DeepClone() : value,
        "count"   => JsonValue.Create(value is JsonArray c ? c.Count : value is null ? 0 : 1),
        "upper"   => Map(value, s => s.ToUpperInvariant()),
        "lower"   => Map(value, s => s.ToLowerInvariant()),
        "trim"    => Map(value, s => s.Trim()),
        "string"  => value is null ? null : JsonValue.Create(JsonText.Of(value)),
        "distinct" => Distinct(value, call.Argument),
        _ => throw new FormatException($"Unknown transform '{call.Name}'. Allowed: {string.Join(", ", Names)}.")
    };

    /// <summary>
    /// Removes repeated items from an array, keeping the first of each and the original order.
    /// Without an argument, whole items are compared by their JSON text. With a property path
    /// ("id", "customer.id") objects are compared by that property's JSON text.
    /// Items with no value for the property are always kept: silently dropping a record would hide data.
    /// A value that isn't an array is returned unchanged.
    /// </summary>
    private static JsonNode? Distinct(JsonNode? value, string? keyPath)
    {
        if (value is not JsonArray items) return value;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new JsonArray();
        foreach (var item in items)
        {
            var identity = string.IsNullOrEmpty(keyPath)
                ? item?.ToJsonString() ?? "null"
                : JsonFields.Get(item, keyPath)?.ToJsonString(); // null = no key value

            if (identity is null || seen.Add(identity)) result.Add(item?.DeepClone());
        }
        return result;
    }

    private static bool IsEmpty(JsonNode? v) => v is null || (v is JsonValue jv && jv.TryGetValue<string>(out var s) && s.Length == 0);

    private static JsonNode? Map(JsonNode? value, Func<string, string> f) =>
        value is null ? null : JsonValue.Create(f(JsonText.Of(value) ?? ""));
}
