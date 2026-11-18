using System.Text.Json.Nodes;
using ApiAggregator.Engine.Config;
using ApiAggregator.Engine.Model;

namespace ApiAggregator.Engine.Authoring;

/// <summary>One choice in the admin UI's "use value from" dropdown.</summary>
public sealed record SourceOption(string Expression, string Label, string Group);

/// <summary>
/// Lists the values a step can use: declared inputs, plus the response fields of earlier steps.
/// Fields come from each endpoint's response schema (imported from OpenAPI or entered by hand).
/// </summary>
public sealed class SourceCatalog
{
    /// <summary>Builds a simple JSON schema from a sample response, for endpoints added by hand without OpenAPI.</summary>
    public static JsonNode InferSchema(JsonNode? sample, int depth = 0) => sample switch
    {
        JsonObject o when depth < 6 => new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject(o.Select(p => KeyValuePair.Create(p.Key, (JsonNode?)InferSchema(p.Value, depth + 1))))
        },
        JsonArray a when depth < 6 => new JsonObject { ["type"] = "array", ["items"] = a.Count > 0 ? InferSchema(a[0], depth + 1) : new JsonObject() },
        JsonValue v => new JsonObject { ["type"] = v.GetValueKind() switch
        {
            System.Text.Json.JsonValueKind.Number => "number",
            System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False => "boolean",
            _ => "string"
        } },
        _ => new JsonObject()
    };

    private const int MaxDepth = 3;

    public IReadOnlyList<SourceOption> For(Recipe recipe, string? beforeStep, ConfigSnapshot config)
    {
        var options = new List<SourceOption>();

        foreach (var key in recipe.Inputs.Keys.Order())
            options.Add(new SourceOption($"request.{key}", $"Request → {key.Split('.', 2)[1]}", "Request"));

        foreach (var step in recipe.Steps)
        {
            if (step.Alias == beforeStep) break;
            var label = step.DisplayName ?? step.Alias;
            var endpoint = config.FindEndpoint(step.Endpoint, out _);

            options.Add(new SourceOption($"{step.Alias}.body", $"{label} → whole response", label));
            foreach (var field in Fields(endpoint?.ResponseSchema, "", 0))
                options.Add(new SourceOption($"{step.Alias}.body.{field}", $"{label} → {field}", label));
            options.Add(new SourceOption($"{step.Alias}.status", $"{label} → status code", label));
        }

        if (recipe.Steps.Any(s => s.Alias == beforeStep && s.ForEach is not null))
            options.Add(new SourceOption("item", "Current item (for each)", "For each"));

        return options;
    }

    /// <summary>Flattens a JSON schema into dotted field paths: id, address.postcode, items[*].id.</summary>
    private static IEnumerable<string> Fields(JsonNode? schema, string prefix, int depth)
    {
        if (schema is not JsonObject s || depth > MaxDepth) yield break;

        if (s["type"]?.ToString() == "array")
        {
            foreach (var f in Fields(s["items"], prefix + "[*]", depth + 1)) yield return f;
            yield break;
        }

        if (s["properties"] is not JsonObject props) yield break;
        foreach (var (name, child) in props)
        {
            var path = prefix.Length == 0 ? name : $"{prefix}.{name}";
            yield return path;
            foreach (var nested in Fields(child, path, depth + 1)) yield return nested;
        }
    }
}
