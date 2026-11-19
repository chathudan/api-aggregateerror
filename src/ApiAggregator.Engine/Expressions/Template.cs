using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ApiAggregator.Engine.Expressions;

/// <summary>
/// Resolves "{{ expression }}" placeholders.
///   "{{a.body}}"                  whole string is one expression  -> keeps the JSON type (object, number...)
///   "Customer {{a.body.name}}"    mixed text                      -> string interpolation
/// </summary>
public static class Template
{
    public static JsonNode? Resolve(string text, IValueScope scope)
    {
        var trimmed = text.Trim();
        if (IsSingleExpression(trimmed))
            return ValuePath.Parse(trimmed[2..^2]).Evaluate(scope)?.DeepClone();

        if (!text.Contains("{{", StringComparison.Ordinal)) return JsonValue.Create(text);

        var sb = new StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            var open = text.IndexOf("{{", i, StringComparison.Ordinal);
            if (open < 0) { sb.Append(text, i, text.Length - i); break; }
            var close = text.IndexOf("}}", open + 2, StringComparison.Ordinal);
            if (close < 0) throw new FormatException($"Missing '}}}}' in '{text}'.");
            sb.Append(text, i, open - i);
            sb.Append(JsonText.Of(ValuePath.Parse(text[(open + 2)..close]).Evaluate(scope)));
            i = close + 2;
        }
        return JsonValue.Create(sb.ToString());
    }

    /// <summary>Walks a JSON template and resolves every string value.</summary>
    public static JsonNode? ResolveNode(JsonNode? template, IValueScope scope) => template switch
    {
        null => null,
        JsonObject obj => new JsonObject(obj.Select(p => KeyValuePair.Create(p.Key, ResolveNode(p.Value, scope)))),
        JsonArray arr => new JsonArray(arr.Select(n => ResolveNode(n, scope)).ToArray()),
        JsonValue v when v.GetValueKind() == JsonValueKind.String => Resolve(v.GetValue<string>(), scope),
        _ => template.DeepClone()
    };

    /// <summary>Every expression inside "{{ }}" in a string (used for dependency detection and validation).</summary>
    public static IEnumerable<string> FindExpressions(string? text)
    {
        if (string.IsNullOrEmpty(text)) yield break;
        var i = 0;
        while (true)
        {
            var open = text.IndexOf("{{", i, StringComparison.Ordinal);
            if (open < 0) yield break;
            var close = text.IndexOf("}}", open + 2, StringComparison.Ordinal);
            if (close < 0) yield break;
            yield return text[(open + 2)..close].Trim();
            i = close + 2;
        }
    }

    public static IEnumerable<string> FindExpressions(JsonNode? node) => node switch
    {
        JsonObject obj => obj.SelectMany(p => FindExpressions(p.Value)),
        JsonArray arr => arr.SelectMany(FindExpressions),
        JsonValue v when v.GetValueKind() == JsonValueKind.String => FindExpressions(v.GetValue<string>()),
        _ => []
    };

    /// <summary>True when the whole text is exactly one "{{ ... }}" (so the JSON type is kept).</summary>
    public static bool IsSingleExpression(string s) =>
        s.StartsWith("{{", StringComparison.Ordinal) && s.EndsWith("}}", StringComparison.Ordinal)
        && s.IndexOf("{{", 2, StringComparison.Ordinal) < 0;
}

/// <summary>Converts JSON values to plain text for URLs, headers and string templates.</summary>
public static class JsonText
{
    public static string? Of(JsonNode? node) => node switch
    {
        null => null,
        JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>(),
        JsonValue v => v.ToJsonString(),
        _ => node.ToJsonString()
    };
}
