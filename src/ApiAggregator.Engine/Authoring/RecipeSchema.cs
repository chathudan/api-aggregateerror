using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;
using ApiAggregator.Engine.Config;
using ApiAggregator.Engine.Model;

namespace ApiAggregator.Engine.Authoring;

/// <summary>
/// The JSON Schema of a recipe (ADR 0019), generated from the model with the framework's exporter (no package), in the
/// same JSON form the config files and /admin/* use. Editors and AI agents use it to write recipes that load; it says
/// what is allowed, while <c>RecipeValidator</c> still decides what is valid (references, routes, limits).
/// A copy is kept in docs/schemas/recipe.schema.json; a test fails when the two differ.
/// </summary>
public static class RecipeSchema
{
    private static readonly Lazy<string> Text = new(Build);

    /// <summary>A fresh copy (callers may change it).</summary>
    public static JsonNode Get() => JsonNode.Parse(Text.Value)!;

    /// <summary>The schema as indented JSON text, as written to docs/schemas/recipe.schema.json.</summary>
    public static string Json => Text.Value;

    private static string Build()
    {
        var options = new JsonSerializerOptions(AggregatorJson.Options) { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
        var schema = options.GetJsonSchemaAsNode(typeof(Recipe), new JsonSchemaExporterOptions
        {
            TreatNullObliviousAsNonNullable = true,
            TransformSchemaNode = (context, node) =>
            {
                var key = context.PropertyInfo is { } p ? $"{p.DeclaringType.Name}.{p.Name}" : context.TypeInfo.Type.Name;
                if (!Descriptions.TryGetValue(key, out var description)) return node;
                // Any-JSON properties (response, template) come as "true": give them an object so they keep the text.
                var obj = node as JsonObject ?? new JsonObject();
                obj.Insert(0, "description", description);
                return obj;
            }
        });
        var root = schema.AsObject();
        root.Insert(0, "$schema", "https://json-schema.org/draft/2020-12/schema");
        root.Insert(1, "title", "Aggregated endpoint (recipe)");
        // Readable text: "<route>" rather than "\u003Croute\u003E". It's served as JSON, never put into HTML.
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    /// <summary>
    /// What each part means, in the words the guides use. Keyed "Type.jsonName", or the type name for the type itself.
    /// The model's doc comments say the same for developers; these are for whoever writes the JSON.
    /// </summary>
    private static readonly Dictionary<string, string> Descriptions = new(StringComparer.Ordinal)
    {
        ["Recipe"] = "One aggregated endpoint: GET /bff/<route>. Steps call service endpoints; the response template builds one answer. Always saved as a draft; a person publishes it.",
        ["Recipe.id"] = "Stable id: lowercase letters, digits and dashes, e.g. customer-overview.",
        ["Recipe.name"] = "Name people see, e.g. Customer overview.",
        ["Recipe.version"] = "Set by the app when saving; leave it out.",
        ["Recipe.status"] = "Set by the app (always draft when saved); leave it out.",
        ["Recipe.method"] = "HTTP method apps call it with: GET (default) or POST.",
        ["Recipe.route"] = "Address after /bff/, with {name} for route values, e.g. customer/{id}/overview. Each route value must be declared in inputs as route.<name>.",
        ["Recipe.requireAuthentication"] = "true (default): callers need a valid token.",
        ["Recipe.requiredScope"] = "A scope the caller's token must have, e.g. customer.read.",
        ["Recipe.inputs"] = "The only caller values that reach services. Keys: route.<name>, query.<name> or header.<Name>.",
        ["Recipe.steps"] = "Service calls. They run in parallel unless one uses another's value.",
        ["Recipe.combine"] = "Named lists merged from several steps by exact key; used in the response as {{combine.<name>}}.",
        ["Recipe.response"] = "The answer: any JSON with {{ }} expressions in its texts, e.g. { \"customer\": \"{{a.body}}\" }.",
        ["Recipe.cache"] = "Whole-answer cache. Off by default; per caller by default.",
        ["Recipe.timeoutMs"] = "Limit for the whole call, in ms (default 10000).",
        ["InputDef.type"] = "string, int, number, bool or date.",
        ["InputDef.required"] = "A request without it is refused with 400.",
        ["InputDef.pattern"] = "A regular expression the whole value must match, e.g. ^C-[0-9]{5}$.",
        ["InputDef.enum"] = "The only values allowed.",
        ["InputDef.maxLength"] = "Longest value allowed (default 200).",
        ["InputDef.default"] = "Used when the caller doesn't send it.",
        ["StepDef.alias"] = "Short name used in expressions, e.g. customer (letters, digits, _). {{customer.body}} is its answer.",
        ["StepDef.displayName"] = "Name people see, e.g. Get customer.",
        ["StepDef.endpoint"] = "The service endpoint it calls: <serviceId>/<endpointId>, e.g. service-a/getCustomer.",
        ["StepDef.bind"] = "Values sent with the call: each moves a value (from) or a template to a target (to).",
        ["StepDef.required"] = "true (default): if it fails, the endpoint answers 502. false: a partial answer with _errors.",
        ["StepDef.when"] = "Run only when this is true, e.g. {{a.body.isPremium}} or {{a.body.status}} == active.",
        ["StepDef.forEach"] = "Run once per item of this list, e.g. {{b.body.items[*].id}}; use item in bind.",
        ["StepDef.maxConcurrency"] = "For forEach: calls at once (1-32, default 4).",
        ["StepDef.distinct"] = "For forEach: call each unique item once (default true).",
        ["StepDef.timeoutMs"] = "Limit for this call, in ms (default: the service's).",
        ["StepDef.cache"] = "Cache this call's answer. Off by default; per caller by default.",
        ["BindingDef.from"] = "A path without braces, e.g. request.route.id, a.body.id or item; may end with | transform.",
        ["BindingDef.template"] = "A JSON template instead of from, mainly for request bodies.",
        ["BindingDef.to"] = "Target: path.<name>, query.<name>, header.<Name>, cookie.<name>, body or body.<field>.",
        ["BindingDef.default"] = "Sent when the value is missing.",
        ["CacheSettings.perCaller"] = "true (default): each caller has their own copy. false only for data that is the same for everyone; never for customer or patient data.",
        ["CombineDef.sources"] = "The lists to merge, in priority order.",
        ["CombineDef.normalize"] = "Applied to keys before comparing: trim, lower.",
        ["CombineDef.onConflict"] = "priority (the first source wins) or newest:<field>.",
        ["CombineDef.owners"] = "Field -> source name whose value always wins.",
        ["CombineDef.withoutKey"] = "keep or drop records without a key.",
        ["CombineSource.from"] = "Path (no braces) to a list, e.g. crm.body.contacts.",
        ["CombineSource.key"] = "Field that identifies a record, e.g. userId; several separated by commas. Matched exactly, never fuzzily.",
        ["CombineSource.via"] = "A mapping list for services that don't share an id."
    };
}
