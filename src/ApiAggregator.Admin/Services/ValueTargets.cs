using System.Text.Json;
using System.Text.Json.Nodes;
using ApiAggregator.Engine.Expressions;

namespace ApiAggregator.Admin.Services;

/// <summary>
/// Where a step's value goes, in words people know ("query.status" → status, "query"), and the choices for
/// "Add another value". A value that fills the whole request body is saved as just "body".
/// </summary>
public static class ValueTargets
{
    /// <summary>The places "Add another value" offers, in order. The path's values come from the endpoint itself.</summary>
    public static readonly IReadOnlyList<(string Prefix, string Label)> Kinds =
    [
        ("header", "Header"),
        ("query", "Query (after ? in the address)"),
        ("body", "Body field"),
        ("cookie", "Cookie")
    ];

    /// <summary>"query.status" → (status, query); "body" → (Whole body, body).</summary>
    public static (string Name, string Where) Describe(string target)
    {
        var parts = target.Split('.', 2);
        return (parts.Length == 2 ? parts[1] : "Whole body", parts[0].ToLowerInvariant());
    }

    /// <summary>"header" + " X-Channel " → "header.X-Channel"; null when there's no name or the kind isn't offered.</summary>
    public static string? Compose(string? prefix, string? name)
    {
        var trimmed = name?.Trim() ?? "";
        if (trimmed.Length == 0 || Kinds.All(k => k.Prefix != prefix)) return null;
        return $"{prefix}.{trimmed}";
    }

    /// <summary>
    /// A whole-body value written as JSON, { "customerId": "{{customer.body.id}}", "status": "open" }, as one value per
    /// field (body.customerId, body.status), so each gets its own picker. Sends exactly the same body: "{{x}}" becomes
    /// "use value x", any other text or number stays a fixed value. Null when it can't be split without changing what
    /// is sent: not an object, empty, a nested object or list, a null, or a field name with a dot.
    /// </summary>
    public static Dictionary<string, BindingForm>? SplitBody(BindingForm? body)
    {
        // A default for the whole body would apply to each field differently: keep such a value as it is.
        if (body is not { Mode: BindingMode.FixedValue, FixedIsJson: true, FixedValue: { } json, Default: null }) return null;
        JsonNode? node;
        try { node = JsonNode.Parse(json); }
        catch (JsonException) { return null; }
        if (node is not JsonObject { Count: > 0 } fields) return null;

        var split = new Dictionary<string, BindingForm>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in fields)
        {
            if (name.Length == 0 || name.Contains('.') || split.ContainsKey($"body.{name}")) return null;
            if (FieldValue(value) is not { } form) return null;
            split[$"body.{name}"] = form;
        }
        return split;
    }

    private static BindingForm? FieldValue(JsonNode? value)
    {
        if (value is not JsonValue v) return null; // null, a nested object or a list
        switch (v.GetValueKind())
        {
            case JsonValueKind.String:
                var text = v.GetValue<string>();
                return Template.IsSingleExpression(text) && text == $"{{{{{text[2..^2].Trim()}}}}}"
                    ? new BindingForm { Mode = BindingMode.FromSource, Expression = text[2..^2].Trim() }
                    : new BindingForm { Mode = BindingMode.FixedValue, FixedValue = text };
            case JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False:
                return new BindingForm { Mode = BindingMode.FixedValue, FixedValue = v.ToJsonString(), FixedIsJson = true };
            default:
                return null;
        }
    }
}
