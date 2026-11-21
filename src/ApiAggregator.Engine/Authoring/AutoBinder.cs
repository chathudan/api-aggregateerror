using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ApiAggregator.Engine.Config;
using ApiAggregator.Engine.Expressions;
using ApiAggregator.Engine.Model;

namespace ApiAggregator.Engine.Authoring;

/// <summary>A suggested value for one parameter, with a short reason shown in the UI.</summary>
public sealed record BindingSuggestion(string Target, string Expression, string Reason);

/// <summary>
/// Pre-fills a step's parameters so technicians mostly just confirm. Suggestions are hints only:
/// the UI marks them "suggested" and the validator still checks everything.
///
/// Rules, first match wins:
///   0. In a step that runs for each item: the current item
///        looping over orders[*].contactId          contactId   &lt;- item
///        looping over orders[*] (whole orders)     contactId   &lt;- item.contactId
///   1. A caller input with the same name           customerId  &lt;- request.query.customerId
///   2. A field with the same name in an earlier step  accountNo   &lt;- getCustomer.body.accountNo
///   3. "&lt;thing&gt;Id" and an earlier step about &lt;thing&gt;s  X-Customer-Id &lt;- getCustomer.body.id
///   4. First step's only path parameter + one route input   customerId  &lt;- request.route.id
/// </summary>
public static partial class AutoBinder
{
    public static IReadOnlyList<BindingSuggestion> Suggest(Recipe recipe, string stepAlias, ConfigSnapshot config)
    {
        var stepIndex = recipe.Steps.FindIndex(s => s.Alias == stepAlias);
        if (stepIndex < 0) return [];
        var step = recipe.Steps[stepIndex];
        var endpoint = config.FindEndpoint(step.Endpoint, out _);
        if (endpoint is null) return [];

        var earlier = recipe.Steps.Take(stepIndex)
            .Select(s => (Step: s, Endpoint: config.FindEndpoint(s.Endpoint, out _)))
            .Where(x => x.Endpoint is not null)
            .Reverse() // nearest earlier step first
            .ToList();

        var routeInputs = recipe.Inputs.Keys.Where(k => k.StartsWith("route.", StringComparison.OrdinalIgnoreCase)).ToList();
        var targets = PathParams(endpoint.Path).Select(p => $"path.{p}")
            .Concat(endpoint.Parameters.Where(p => p.In != "path").Select(p => $"{p.In}.{p.Name}"))
            .Concat(BodyFields(endpoint).Select(f => $"body.{f}"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var (loopField, itemFields, loopLabel) = Loop(recipe, step, config);

        var suggestions = new List<BindingSuggestion>();
        foreach (var target in targets)
        {
            var name = target.Split('.', 2)[1];
            var key = Normalize(name);

            // 0. the current item of a for-each step
            if (loopField is not null && Normalize(loopField) == key)
            { suggestions.Add(new(target, "item", $"The current item ({loopLabel})")); continue; }
            var itemField = itemFields.FirstOrDefault(f => Normalize(f) == key);
            if (itemField is not null) { suggestions.Add(new(target, $"item.{itemField}", $"From the current item ({loopLabel})")); continue; }

            // 1. same-named caller input
            var input = recipe.Inputs.Keys.FirstOrDefault(k => Normalize(k.Split('.', 2)[1]) == key);
            if (input is not null) { suggestions.Add(new(target, $"request.{input}", "Same name as an input")); continue; }

            // 2. same-named field in an earlier step
            var field = earlier
                .SelectMany(e => TopFields(e.Endpoint!.ResponseSchema).Select(f => (e.Step, Field: f)))
                .FirstOrDefault(x => Normalize(x.Field) == key);
            if (field.Step is not null) { suggestions.Add(new(target, $"{field.Step.Alias}.body.{field.Field}", $"Same name in {Label(field.Step)}")); continue; }

            // 3. <thing>Id -> id of an earlier step about <thing>
            if (key.EndsWith("id") && key.Length > 2)
            {
                var thing = key[..^2];
                var about = earlier.FirstOrDefault(e =>
                    Normalize(e.Endpoint!.Path).Contains(thing) && TopFields(e.Endpoint.ResponseSchema).Any(f => f.Equals("id", StringComparison.OrdinalIgnoreCase)));
                if (about.Step is not null) { suggestions.Add(new(target, $"{about.Step.Alias}.body.id", $"The id from {Label(about.Step)}")); continue; }
            }

            // 4. first step, single route input, path parameter
            if (stepIndex == 0 && target.StartsWith("path.") && routeInputs.Count == 1)
                suggestions.Add(new(target, $"request.{routeInputs[0]}", "From the endpoint's address"));
        }
        return suggestions;
    }

    private static string Label(StepDef s) => s.DisplayName ?? s.Alias;

    /// <summary>
    /// What a for-each step loops over: one field of each item ("contactId" in orders[*].contactId), or whole items
    /// (orders[*], whose fields are then listed). The label is the loop picker's words, e.g. "each order's contactId".
    /// </summary>
    private static (string? Field, IReadOnlyList<string> ItemFields, string Label) Loop(Recipe recipe, StepDef step, ConfigSnapshot config)
    {
        if (SourceCatalog.LoopPath(step) is not { } path || path.TransformCalls.Count > 0) return (null, [], "");
        var segments = path.Segments;
        if (!segments.Any(s => s.Kind == ValuePath.SegmentKind.Wildcard)) return (null, [], "");

        var option = new SourceCatalog().LoopOptions(recipe, step.Alias, config).FirstOrDefault(o => o.Expression == path.Text);
        var label = option?.Label.Split(" → ", 2) is [_, var words] ? words : "each item";

        if (segments[^1].Kind == ValuePath.SegmentKind.Property) return (segments[^1].Name, [], label);
        if (segments[^1].Kind != ValuePath.SegmentKind.Wildcard) return (null, [], "");
        return (null, TopFields(SourceCatalog.SchemaAt(recipe, path, config)).ToList(), label);
    }

    /// <summary>"X-Customer-Id", "customer_id", "customerId" all become "customerid".</summary>
    public static string Normalize(string s)
    {
        var lower = s.ToLowerInvariant();
        if (lower.StartsWith("x-")) lower = lower[2..];
        return new string(lower.Where(char.IsLetterOrDigit).ToArray());
    }

    private static IEnumerable<string> TopFields(JsonNode? schema) =>
        schema?["properties"] is JsonObject props ? props.Select(p => p.Key) : [];

    /// <summary>
    /// The top-level fields of the endpoint's request body (its requestSchema), each sent on its own as body.&lt;field&gt;.
    /// A name with a dot is left out: body.a.b would build a nested object, not a field called "a.b".
    /// </summary>
    public static IEnumerable<string> BodyFields(ApiEndpoint endpoint) =>
        TopFields(endpoint.RequestSchema).Where(f => f.Length > 0 && !f.Contains('.'));

    public static IEnumerable<string> PathParams(string path) =>
        PathParamRegex().Matches(path).Select(m => m.Groups[1].Value);

    [GeneratedRegex(@"\{([A-Za-z0-9_]+)\}")] private static partial Regex PathParamRegex();
}
