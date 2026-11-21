using System.Text.Json.Nodes;
using ApiAggregator.Engine.Config;
using ApiAggregator.Engine.Expressions;
using ApiAggregator.Engine.Model;

namespace ApiAggregator.Engine.Authoring;

/// <summary>One choice in the admin UI's "use value from" dropdown.</summary>
public sealed record SourceOption(string Expression, string Label, string Group);

/// <summary>
/// One choice in the admin UI's "Once for each item in a list" dropdown, e.g. "Orders from CRM → each order's contactId"
/// for orders.body.orders[*].contactId. <see cref="ItemSchema"/> describes one item (null when unknown).
/// </summary>
public sealed record LoopOption(string Expression, string Label, string Group, JsonNode? ItemSchema);

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

            // A step that runs for each item has one { status, headers, body } per call, so its values are lists: notes[*].body.
            var each = string.IsNullOrWhiteSpace(step.ForEach) ? "" : "[*]";
            var suffix = each.Length == 0 ? "" : " (from every call)";
            options.Add(new SourceOption($"{step.Alias}{each}.body", $"{label} → whole response{suffix}", label));
            foreach (var field in Fields(endpoint?.ResponseSchema, "", 0))
                options.Add(new SourceOption($"{step.Alias}{each}.body.{field}", $"{label} → {field}{suffix}", label));
            options.Add(new SourceOption($"{step.Alias}{each}.status", $"{label} → status code{suffix}", label));
        }

        // The response (beforeStep == null) may also use the combined lists; steps may not.
        if (beforeStep is null)
            foreach (var name in recipe.Combine.Keys.Order(StringComparer.Ordinal))
                options.Add(new SourceOption($"combine.{name}", $"Combined → {name}", "Combined lists"));

        // "Current item" is always offered in a step, so choosing it before a loop is set can say "choose what to loop over first".
        if (beforeStep is not null && recipe.Steps.FirstOrDefault(s => s.Alias == beforeStep) is { } current)
        {
            options.Add(new SourceOption("item", "Current item (for each)", "For each"));
            if (LoopPath(current) is { } loop)
                foreach (var field in Fields(SchemaAt(recipe, loop, config), "", 0).Where(f => !f.Contains("[*]")))
                    options.Add(new SourceOption($"item.{field}", $"Current item → {field}", "For each"));
        }

        return options;
    }

    /// <summary>
    /// The lists a step can run once for each item of: every list in an earlier step's response, and every field inside one.
    ///   "Orders from CRM → each order"              orders.body.orders[*]
    ///   "Orders from CRM → each order's contactId"  orders.body.orders[*].contactId
    /// Steps that run for each item themselves are left out: their result is a list of whole responses.
    /// </summary>
    public IReadOnlyList<LoopOption> LoopOptions(Recipe recipe, string? beforeStep, ConfigSnapshot config)
    {
        var options = new List<LoopOption>();
        foreach (var step in recipe.Steps)
        {
            if (step.Alias == beforeStep) break;
            if (!string.IsNullOrWhiteSpace(step.ForEach)) continue;
            var label = step.DisplayName ?? step.Alias;
            if (config.FindEndpoint(step.Endpoint, out _)?.ResponseSchema is JsonObject schema)
                WalkLists(schema, $"{step.Alias}.body", label, null, null, 0, options);
        }
        return options;
    }

    /// <summary>The path inside a step's "for each", when it is exactly one {{ value }}; otherwise null.</summary>
    public static ValuePath? LoopPath(StepDef step)
    {
        var text = step.ForEach?.Trim();
        if (string.IsNullOrEmpty(text) || !Template.IsSingleExpression(text)) return null;
        try { return ValuePath.Parse(text[2..^2]); }
        catch (FormatException) { return null; }
    }

    /// <summary>
    /// The schema of the value a path points at, following an earlier step's response schema: for "orders.body.orders[*].contactId"
    /// the schema of one contactId. Null when the path isn't a step's body or the schema doesn't say.
    /// </summary>
    public static JsonNode? SchemaAt(Recipe recipe, ValuePath path, ConfigSnapshot config)
    {
        if (path.TransformCalls.Count > 0) return null; // a transform changes the shape
        var step = recipe.Steps.FirstOrDefault(s => s.Alias == path.Root);
        if (step is null) return null;

        // A step that runs for each item holds one answer per call: notes[*].body.x is the x of every call.
        var segments = path.Segments.ToList();
        var forEach = !string.IsNullOrWhiteSpace(step.ForEach) && segments.Count >= 2
            && segments[0].Kind == ValuePath.SegmentKind.Wildcard && segments[1] is { Kind: ValuePath.SegmentKind.Property, Name: "body" };
        if (!forEach && path.FirstProperty != "body") return null;

        var schema = config.FindEndpoint(step.Endpoint, out _)?.ResponseSchema;
        foreach (var segment in segments.Skip(forEach ? 2 : 1))
        {
            if (schema is not JsonObject s) return null;
            schema = segment.Kind == ValuePath.SegmentKind.Property
                ? (s["properties"] as JsonObject)?[segment.Name!]
                : s["type"]?.ToString() == "array" ? s["items"] : null;
        }
        return schema;
    }

    private static void WalkLists(JsonObject schema, string path, string label, string? listName, string? parentNoun, int depth, List<LoopOption> options)
    {
        if (depth > MaxDepth) return;

        if (schema["type"]?.ToString() == "array")
        {
            var noun = Singular(listName);
            var each = parentNoun is null ? $"each {noun}" : $"each {noun} in every {parentNoun}";
            var itemPath = $"{path}[*]";
            var items = schema["items"] as JsonObject;
            options.Add(new LoopOption(itemPath, $"{label} → {each}", label, items));

            if (items?["properties"] is not JsonObject itemProps) return;
            foreach (var field in Fields(items, "", 0).Where(f => !f.Contains("[*]")))
                options.Add(new LoopOption($"{itemPath}.{field}", $"{label} → {Possessive(each)} {field}", label, SchemaAt(items, field)));
            foreach (var (name, child) in itemProps)
                if (child is JsonObject c && c["type"]?.ToString() == "array")
                    WalkLists(c, $"{itemPath}.{name}", label, name, noun, depth + 1, options);
            return;
        }

        if (schema["properties"] is not JsonObject props) return;
        foreach (var (name, child) in props)
            if (child is JsonObject c) WalkLists(c, $"{path}.{name}", label, name, parentNoun, depth + 1, options);
    }

    /// <summary>The schema of a dotted field ("customer.id") inside an object schema.</summary>
    private static JsonNode? SchemaAt(JsonObject schema, string dottedField)
    {
        JsonNode? current = schema;
        foreach (var part in dottedField.Split('.')) current = (current?["properties"] as JsonObject)?[part];
        return current;
    }

    /// <summary>"orders" → "order", "entries" → "entry", "addresses" → "address"; a name that isn't plural gives "item in data".</summary>
    private static string Singular(string? list)
    {
        if (string.IsNullOrEmpty(list)) return "item";
        if (list.Length > 3 && list.EndsWith("ies", StringComparison.Ordinal)) return list[..^3] + "y";
        if (list.EndsWith("sses", StringComparison.Ordinal) || list.EndsWith("xes", StringComparison.Ordinal)
            || list.EndsWith("ches", StringComparison.Ordinal) || list.EndsWith("shes", StringComparison.Ordinal)) return list[..^2];
        if (list.Length > 1 && list.EndsWith('s') && !list.EndsWith("ss", StringComparison.Ordinal)) return list[..^1];
        return $"item in {list}";
    }

    /// <summary>"each order" → "each order's"; "each item in data" → "each item in data:" (no odd apostrophe).</summary>
    private static string Possessive(string each) => each.Contains(" in ", StringComparison.Ordinal) ? $"{each}:" : $"{each}'s";

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
