using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using ApiAggregator.Engine.Config;
using ApiAggregator.Engine.Execution;
using ApiAggregator.Engine.Expressions;
using ApiAggregator.Engine.Model;

namespace ApiAggregator.Engine.Authoring;

/// <summary>Settings for <see cref="OpenApiDocumentBuilder"/>.</summary>
public sealed class OpenApiBuildOptions
{
    public string Title { get; init; } = "Aggregated endpoints";

    /// <summary>Where the gateway is reachable, e.g. "http://localhost:5200". Left out of the document when null.</summary>
    public string? ServerUrl { get; init; }

    /// <summary>
    /// When true the description lists each step with its internal service and endpoint names.
    /// Off by default: the document is public, and internal names must not leak.
    /// </summary>
    public bool IncludeInternalDetails { get; init; }
}

/// <summary>
/// Describes every published aggregated endpoint as an OpenAPI 3.0.3 document, so web and mobile
/// teams can read it, try it in Swagger UI, or generate clients from it.
///
/// The 200 response shape is worked out from the recipe's response template and the response
/// schemas of the services it calls. Anything that can't be worked out for certain becomes
/// "any value" ({}). It never guesses.
/// </summary>
public static class OpenApiDocumentBuilder
{
    /// <summary>Every code a caller can see in the "code" field of an error.</summary>
    private static readonly string[] DownstreamCodes =
    [
        DownstreamErrorCode.AuthFailed, DownstreamErrorCode.Unauthorized, DownstreamErrorCode.Forbidden,
        DownstreamErrorCode.NotFound, DownstreamErrorCode.BadRequest, DownstreamErrorCode.RateLimited,
        DownstreamErrorCode.ServerError, DownstreamErrorCode.Timeout, DownstreamErrorCode.Unreachable,
        DownstreamErrorCode.InvalidResponse, DownstreamErrorCode.MissingValue, DownstreamErrorCode.CombineFailed
        // dependency_failed is left out on purpose: only the root cause of a failure is ever reported.
    ];

    private static readonly string[] GatewayCodes =
        [ErrorBody.InvalidInput, "unauthenticated", "insufficient_scope", "not_found", "rate_limited", "internal_error"];

    public static JsonObject Build(ConfigSnapshot config, OpenApiBuildOptions options)
    {
        var paths = new JsonObject();
        foreach (var recipe in config.Recipes.OrderBy(r => r.Route, StringComparer.Ordinal))
        {
            var path = "/bff/" + recipe.Route.Trim('/');
            var pathItem = paths[path] as JsonObject ?? new JsonObject();
            pathItem[recipe.Method.ToLowerInvariant()] = new RecipeDescriber(config, recipe, options).Operation();
            paths[path] = pathItem;
        }

        var doc = new JsonObject
        {
            ["openapi"] = "3.0.3",
            ["info"] = new JsonObject
            {
                ["title"] = options.Title,
                ["version"] = config.Version,
                ["description"] =
                    "Each endpoint combines several service calls into one answer. " +
                    "Errors use problem details (application/problem+json) with a stable `code` you can switch on. " +
                    "Every response has an `X-Correlation-Id` header: quote it when you report a problem."
            },
            ["paths"] = paths,
            ["components"] = Components()
        };
        if (!string.IsNullOrWhiteSpace(options.ServerUrl))
            doc["servers"] = new JsonArray(new JsonObject { ["url"] = options.ServerUrl!.TrimEnd('/') });

        RemoveNulls(doc);
        return doc;
    }

    // ------------------------------------------------------------------ shared components

    private static JsonObject Components()
    {
        var codes = GatewayCodes.Concat(DownstreamCodes).Distinct().Select(c => (JsonNode?)JsonValue.Create(c)).ToArray();
        var downstream = DownstreamCodes.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray();

        return new JsonObject
        {
            ["securitySchemes"] = new JsonObject
            {
                ["bearer"] = new JsonObject { ["type"] = "http", ["scheme"] = "bearer", ["bearerFormat"] = "JWT" }
            },
            ["headers"] = new JsonObject
            {
                ["CorrelationId"] = Header("Identifies this call in the gateway's logs. Quote it when you report a problem.", "string"),
                ["Partial"] = Header("\"true\" when an optional step failed. The answer is still usable; see `_errors`.", "string"),
                ["RetryAfter"] = Header("Seconds to wait before trying again.", "integer")
            },
            ["schemas"] = new JsonObject
            {
                ["Problem"] = new JsonObject
                {
                    ["type"] = "object",
                    ["description"] = "An error, as RFC 7807 problem details.",
                    ["properties"] = new JsonObject
                    {
                        ["type"] = new JsonObject { ["type"] = "string", ["description"] = "The code as a URN, e.g. urn:api-aggregator:error:not_found." },
                        ["title"] = new JsonObject { ["type"] = "string", ["description"] = "A short, plain-English summary." },
                        ["status"] = new JsonObject { ["type"] = "integer", ["description"] = "The HTTP status." },
                        ["code"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(codes), ["description"] = "Stable reason. Safe to switch on." },
                        ["detail"] = new JsonObject { ["type"] = "string", ["description"] = "More about what went wrong." },
                        ["correlationId"] = new JsonObject { ["type"] = "string", ["description"] = "Same value as the X-Correlation-Id header." },
                        ["retryable"] = new JsonObject { ["type"] = "boolean", ["description"] = "True when trying again later may work." },
                        ["requiredScope"] = new JsonObject { ["type"] = "string", ["description"] = "On 403: the scope your token needs." },
                        ["errors"] = new JsonObject
                        {
                            ["type"] = "array",
                            ["description"] = "On 502/504: the service calls that failed.",
                            ["items"] = Ref("ErrorEntry")
                        }
                    },
                    ["required"] = new JsonArray("type", "title", "status", "code", "correlationId")
                },
                ["ErrorEntry"] = new JsonObject
                {
                    ["type"] = "object",
                    ["description"] = "One failed service call.",
                    ["properties"] = new JsonObject
                    {
                        ["step"] = new JsonObject { ["type"] = "string", ["description"] = "Step alias in the endpoint." },
                        ["name"] = new JsonObject { ["type"] = "string", ["description"] = "Friendly step name." },
                        ["service"] = new JsonObject { ["type"] = "string" },
                        ["code"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(downstream) },
                        ["status"] = new JsonObject { ["type"] = "integer", ["nullable"] = true, ["description"] = "The service's HTTP status, when it answered." },
                        ["message"] = new JsonObject { ["type"] = "string" },
                        ["retryable"] = new JsonObject { ["type"] = "boolean" }
                    }
                }
            },
            ["responses"] = new JsonObject
            {
                ["InvalidInput"] = ProblemResponse("An input is missing or not valid. `detail` says which."),
                ["Unauthenticated"] = ProblemResponse("Missing or invalid token.", retryAfter: false),
                ["Forbidden"] = ProblemResponse("The token doesn't have the scope this endpoint needs (`requiredScope`)."),
                ["TooManyRequests"] = ProblemResponse("Too many calls. Wait for `Retry-After` seconds.", retryAfter: true),
                ["InternalError"] = ProblemResponse("Unexpected error in the gateway. Quote the correlation id."),
                ["BadGateway"] = ProblemResponse("A required service call failed. See `code`, `retryable` and `errors`.", retryAfter: true),
                ["GatewayTimeout"] = ProblemResponse("A required service took too long. Try again shortly.", retryAfter: true)
            }
        };
    }

    private static JsonObject Header(string description, string type) =>
        new() { ["description"] = description, ["schema"] = new JsonObject { ["type"] = type } };

    private static JsonObject Ref(string schema) => new() { ["$ref"] = $"#/components/schemas/{schema}" };

    private static JsonObject ProblemResponse(string description, bool retryAfter = false)
    {
        var headers = new JsonObject { ["X-Correlation-Id"] = new JsonObject { ["$ref"] = "#/components/headers/CorrelationId" } };
        if (retryAfter) headers["Retry-After"] = new JsonObject { ["$ref"] = "#/components/headers/RetryAfter" };
        return new JsonObject
        {
            ["description"] = description,
            ["headers"] = headers,
            ["content"] = new JsonObject { ["application/problem+json"] = new JsonObject { ["schema"] = Ref("Problem") } }
        };
    }

    private static JsonObject ResponseRef(string name) => new() { ["$ref"] = $"#/components/responses/{name}" };

    /// <summary>Removes null-valued keys everywhere, so the document never contains "key": null.</summary>
    private static void RemoveNulls(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            foreach (var key in obj.Where(p => p.Value is null).Select(p => p.Key).ToList()) obj.Remove(key);
            foreach (var (_, value) in obj) RemoveNulls(value);
        }
        else if (node is JsonArray arr)
        {
            foreach (var item in arr) RemoveNulls(item);
        }
    }

    // ------------------------------------------------------------------ one operation

    /// <summary>Works out the operation (parameters, 200 schema, errors) for one recipe.</summary>
    private sealed class RecipeDescriber
    {
        private readonly ConfigSnapshot _config;
        private readonly Recipe _recipe;
        private readonly OpenApiBuildOptions _options;

        /// <summary>What each root name in an expression looks like: step aliases, "request" and "ctx".</summary>
        private readonly Dictionary<string, JsonObject?> _roots = new(StringComparer.OrdinalIgnoreCase);

        public RecipeDescriber(ConfigSnapshot config, Recipe recipe, OpenApiBuildOptions options)
        {
            _config = config;
            _recipe = recipe;
            _options = options;
            foreach (var step in recipe.Steps) _roots[step.Alias] = StepSchema(step);
            _roots["request"] = RequestSchema();
            _roots["ctx"] = Object(("correlationId", Type("string")), ("subject", Type("string")));
            // Last: combined lists are described from the step schemas above (and can't use each other).
            if (recipe.Combine.Count > 0) _roots["combine"] = CombineRoot();
        }

        public JsonObject Operation()
        {
            var tag = _recipe.Route.Trim('/').Split('/')[0];
            var operation = new JsonObject
            {
                ["operationId"] = _recipe.Id,
                ["summary"] = string.IsNullOrWhiteSpace(_recipe.Name) ? _recipe.Id : _recipe.Name,
                ["description"] = Description(),
                ["tags"] = new JsonArray(tag.StartsWith('{') ? "other" : tag),
                ["parameters"] = Parameters(),
                ["responses"] = Responses()
            };
            if (_recipe.RequireAuthentication)
                operation["security"] = new JsonArray(new JsonObject { ["bearer"] = new JsonArray() });
            return operation;
        }

        // -------------------------------------------------------------- text

        private string Description()
        {
            var text = new StringBuilder($"Combines {_recipe.Steps.Count} service call{(_recipe.Steps.Count == 1 ? "" : "s")}.");
            if (!_recipe.RequireAuthentication) text.Append(" No sign-in needed.");
            if (_recipe.RequireAuthentication && !string.IsNullOrEmpty(_recipe.RequiredScope))
                text.Append($" Requires scope `{_recipe.RequiredScope}`.");
            if (_recipe.Cache.Enabled)
                text.Append($" Responses are cached for {_recipe.Cache.TtlSeconds} s{(_recipe.Cache.PerCaller ? " per caller" : "")}.");

            if (_options.IncludeInternalDetails)
            {
                text.Append("\n\nSteps:\n\n");
                var number = 1;
                foreach (var step in _recipe.Steps)
                {
                    var extras = (step.Required ? "" : ", optional") + (step.ForEach is null ? "" : ", once per item");
                    text.Append($"{number++}. {step.DisplayName ?? step.Alias} ({step.Endpoint.Replace('/', ' ')}{extras})\n");
                }
            }
            return text.ToString().TrimEnd();
        }

        // -------------------------------------------------------------- parameters

        private JsonArray Parameters()
        {
            var parameters = new JsonArray();
            foreach (var (key, input) in _recipe.Inputs)
            {
                var parts = key.Split('.', 2);
                if (parts.Length != 2) continue;
                // OpenAPI says these three headers can't be declared as parameters.
                if (parts[0] == "header" && parts[1].ToLowerInvariant() is "accept" or "content-type" or "authorization") continue;

                var schema = InputSchema(input);
                var parameter = new JsonObject
                {
                    ["name"] = parts[1],
                    ["in"] = parts[0] == "route" ? "path" : parts[0],
                    ["required"] = parts[0] == "route" || input.Required, // route values are always needed
                    ["schema"] = schema
                };
                if (schema["default"] is { } example) parameter["example"] = example.DeepClone();
                parameters.Add(parameter);
            }
            return parameters;
        }

        private static JsonObject InputSchema(InputDef input)
        {
            var schema = input.Type switch
            {
                "int" => new JsonObject { ["type"] = "integer", ["format"] = "int64" },
                "number" => new JsonObject { ["type"] = "number" },
                "bool" => new JsonObject { ["type"] = "boolean" },
                "date" => new JsonObject { ["type"] = "string", ["format"] = "date" },
                _ => new JsonObject { ["type"] = "string" }
            };

            if (input.Type is "int" or "number")
            {
                if (input.Min is { } min) schema["minimum"] = min;
                if (input.Max is { } max) schema["maximum"] = max;
            }
            if (input.Type is null or "string")
            {
                if (!string.IsNullOrEmpty(input.Pattern)) schema["pattern"] = input.Pattern;
                if (input.Enum is { Length: > 0 }) schema["enum"] = new JsonArray(input.Enum.Select(e => (JsonNode?)JsonValue.Create(e)).ToArray());
                if (input.MaxLength > 0) schema["maxLength"] = input.MaxLength;
            }
            if (input.Default is not null && DefaultValue(input) is { } value) schema["default"] = value;
            return schema;
        }

        /// <summary>The default as the right JSON type, or null when it doesn't fit the type (then it's left out).</summary>
        private static JsonNode? DefaultValue(InputDef input) => input.Type switch
        {
            "int" => long.TryParse(input.Default, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? JsonValue.Create(i) : null,
            "number" => double.TryParse(input.Default, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? JsonValue.Create(d) : null,
            "bool" => bool.TryParse(input.Default, out var b) ? JsonValue.Create(b) : null,
            _ => JsonValue.Create(input.Default)
        };

        // -------------------------------------------------------------- responses

        private JsonObject Responses()
        {
            var responses = new JsonObject
            {
                ["200"] = new JsonObject
                {
                    ["description"] = "The combined answer.",
                    ["headers"] = new JsonObject
                    {
                        ["X-Correlation-Id"] = new JsonObject { ["$ref"] = "#/components/headers/CorrelationId" },
                        ["X-Aggregator-Partial"] = new JsonObject { ["$ref"] = "#/components/headers/Partial" }
                    },
                    ["content"] = new JsonObject { ["application/json"] = new JsonObject { ["schema"] = ResponseSchema() } }
                },
                ["400"] = ResponseRef("InvalidInput")
            };
            if (_recipe.RequireAuthentication) responses["401"] = ResponseRef("Unauthenticated");
            if (_recipe.RequireAuthentication && !string.IsNullOrEmpty(_recipe.RequiredScope)) responses["403"] = ResponseRef("Forbidden");
            responses["429"] = ResponseRef("TooManyRequests");
            responses["500"] = ResponseRef("InternalError");
            responses["502"] = ResponseRef("BadGateway");
            responses["504"] = ResponseRef("GatewayTimeout");
            return responses;
        }

        /// <summary>The shape of the 200 body, from the response template (or the default "one property per step").</summary>
        private JsonObject ResponseSchema()
        {
            JsonObject schema;
            if (_recipe.Response is not null)
                schema = TemplateSchema(_recipe.Response);
            else
            {
                // No template: the body has one property per step, holding that step's body.
                schema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };
                foreach (var step in _recipe.Steps)
                    schema["properties"]![step.Alias] = StepBodySchema(step);
                // ...and one per combined list, under its name.
                foreach (var (name, def) in _recipe.Combine)
                    schema["properties"]![name] = CombineSchema(def);
            }

            // Optional steps that fail are reported in "_errors" instead of failing the call.
            if ((_recipe.Steps.Any(s => !s.Required) || _recipe.Combine.Values.Any(c => !c.Required)) && schema["type"]?.GetValue<string>() == "object" && schema["properties"] is JsonObject props)
            {
                props["_errors"] = new JsonObject
                {
                    ["type"] = "array",
                    ["description"] = "Present only when an optional step failed (partial response).",
                    ["items"] = Ref("ErrorEntry")
                };
            }
            return schema;
        }

        // -------------------------------------------------------------- template -> schema

        private JsonObject TemplateSchema(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject obj:
                    var properties = new JsonObject();
                    foreach (var (name, value) in obj) properties[name] = TemplateSchema(value);
                    return new JsonObject { ["type"] = "object", ["properties"] = properties };

                case JsonArray arr:
                    var items = arr.Select(TemplateSchema).ToList();
                    var same = items.Count > 0 && items.All(i => JsonNode.DeepEquals(i, items[0]));
                    return new JsonObject { ["type"] = "array", ["items"] = same ? items[0] : new JsonObject() };

                case JsonValue v when v.TryGetValue<string>(out var text):
                    if (Template.IsSingleExpression(text.Trim())) return SchemaFor(text.Trim()[2..^2]);
                    return Type("string"); // plain text or text mixed with {{ }}

                case JsonValue v when v.TryGetValue<bool>(out _):
                    return Type("boolean");

                case JsonValue v when v.TryGetValue<long>(out _):
                    return Type("integer");

                case JsonValue:
                    return Type("number");

                default:
                    return new JsonObject(); // null: nothing to say
            }
        }

        /// <summary>What one "{{ expression }}" produces. Anything uncertain is "any" ({}).</summary>
        private JsonObject SchemaFor(string expression)
        {
            ValuePath path;
            try { path = ValuePath.Parse(expression); }
            catch (FormatException) { return new JsonObject(); }

            if (!_roots.TryGetValue(path.Root, out var current) || current is null) return new JsonObject();

            var projected = false; // true once [*] or [a:b] has turned the result into a list
            foreach (var segment in path.Segments)
            {
                if (current is null) return new JsonObject(); // items of the list are unknown, so nothing below them is known

                if (segment.Kind == ValuePath.SegmentKind.Property)
                {
                    current = PropertyOf(current, segment.Name!);
                    if (current is null) return new JsonObject();
                    continue;
                }

                // [n], [a:b], [*] all need a list.
                if (current["type"]?.GetValue<string>() != "array") return new JsonObject();
                current = current["items"] as JsonObject; // may be null: items not described
                if (segment.Kind != ValuePath.SegmentKind.Index) projected = true;
            }

            var result = projected ? Array((JsonObject)(current?.DeepClone() ?? new JsonObject())) : current;
            if (result is null) return new JsonObject();

            foreach (var transform in path.TransformCalls)
            {
                result = transform.Name switch
                {
                    "count" => Type("integer"),
                    "join" or "upper" or "lower" or "trim" or "string" => Type("string"),
                    "first" or "last" => result["type"]?.GetValue<string>() == "array"
                        ? (result["items"] as JsonObject ?? new JsonObject())
                        : result,
                    _ => result // default keeps the shape
                };
            }
            return (JsonObject)result.DeepClone();
        }

        private static JsonObject? PropertyOf(JsonObject schema, string name)
        {
            if (schema["properties"] is JsonObject properties)
            {
                if (properties.TryGetPropertyValue(name, out var exact)) return exact as JsonObject;
                // The path language also matches names ignoring case (headers, loose JSON).
                foreach (var (key, value) in properties)
                    if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase)) return value as JsonObject;
            }
            return schema["additionalProperties"] as JsonObject; // e.g. headers: any name -> string
        }

        // -------------------------------------------------------------- roots

        /// <summary>A step's output: { status, headers, body }, or a list of those for a forEach step.</summary>
        private JsonObject StepSchema(StepDef step)
        {
            var properties = new JsonObject
            {
                ["status"] = Type("integer"),
                ["headers"] = new JsonObject { ["type"] = "object", ["additionalProperties"] = Type("string") }
            };
            properties["body"] = EndpointResponseSchema(step) ?? new JsonObject(); // no schema known: any value

            var output = new JsonObject { ["type"] = "object", ["properties"] = properties };
            return step.ForEach is null ? output : Array(output);
        }

        /// <summary>What "{{alias.body}}" is: the body schema, or a list of them for a forEach step.</summary>
        private JsonObject StepBodySchema(StepDef step)
        {
            var body = EndpointResponseSchema(step) ?? new JsonObject();
            return step.ForEach is null ? body : Array(body);
        }

        private JsonObject? EndpointResponseSchema(StepDef step) =>
            _config.FindEndpoint(step.Endpoint, out _)?.ResponseSchema is JsonObject source ? CleanSchema(source) : null;

        /// <summary>"{{combine.users}}" and friends: one property per combined list.</summary>
        private JsonObject CombineRoot()
        {
            var lists = new JsonObject();
            foreach (var (name, def) in _recipe.Combine) lists[name] = CombineSchema(def);
            return new JsonObject { ["type"] = "object", ["properties"] = lists };
        }

        /// <summary>
        /// A combined list is an array of objects with the union of its sources' properties.
        /// Where two sources describe the same property differently, the first source wins (it has priority).
        /// </summary>
        private JsonObject CombineSchema(CombineDef def)
        {
            var properties = new JsonObject();
            foreach (var source in def.Sources.Where(s => !string.IsNullOrWhiteSpace(s.From) && !s.From.Contains("{{")))
            {
                var described = SchemaFor(source.From); // the same schema walk as for steps
                // A list of records, or one record on its own.
                var record = described["type"]?.GetValue<string>() == "array" ? described["items"] as JsonObject : described;
                if (record?["properties"] is not JsonObject known) continue;

                foreach (var (field, schema) in known)
                    if (!properties.ContainsKey(field)) properties[field] = schema?.DeepClone();
            }
            if (def.TrackSources) properties["_sources"] = Array(Type("string"));

            var list = Array(new JsonObject { ["type"] = "object", ["properties"] = properties });
            if (!def.Required) list["nullable"] = true; // null when an optional combine fails
            return list;
        }

        /// <summary>"request.route.id" and friends: each declared input, grouped by where it comes from.</summary>
        private JsonObject RequestSchema()
        {
            var groups = new Dictionary<string, JsonObject>();
            foreach (var (key, input) in _recipe.Inputs)
            {
                var parts = key.Split('.', 2);
                if (parts.Length != 2) continue;
                if (!groups.TryGetValue(parts[0], out var group))
                    groups[parts[0]] = group = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };
                group["properties"]![parts[1]] = InputSchema(input);
            }

            var properties = new JsonObject();
            foreach (var (name, group) in groups) properties[name] = group;
            return new JsonObject { ["type"] = "object", ["properties"] = properties };
        }

        // -------------------------------------------------------------- small helpers

        private static JsonObject Type(string type) => new() { ["type"] = type };

        private static JsonObject Array(JsonObject items) => new() { ["type"] = "array", ["items"] = items };

        private static JsonObject Object(params (string Name, JsonObject Schema)[] properties)
        {
            var bag = new JsonObject();
            foreach (var (name, schema) in properties) bag[name] = schema;
            return new JsonObject { ["type"] = "object", ["properties"] = bag };
        }
    }

    // ------------------------------------------------------------------ cleaning service schemas

    /// <summary>Keywords that are copied from a service's schema. Everything else is dropped.</summary>
    private static readonly string[] CopiedKeywords =
    [
        "format", "description", "enum", "nullable", "example", "default", "title", "pattern",
        "minimum", "maximum", "minLength", "maxLength", "minItems", "maxItems", "required"
    ];

    /// <summary>
    /// A plain, self-contained copy of a service's response schema. $refs can't be followed here,
    /// so a $ref becomes "any". Unknown keywords are dropped so the result is always valid OpenAPI 3.0.
    /// </summary>
    private static JsonObject CleanSchema(JsonObject source)
    {
        if (source.ContainsKey("$ref")) return new JsonObject();

        var clean = new JsonObject();
        foreach (var keyword in CopiedKeywords)
            if (source[keyword] is { } value) clean[keyword] = value.DeepClone();

        // JSON Schema allows "type": ["string", "null"]; OpenAPI 3.0 wants type + nullable.
        if (source["type"] is JsonArray types)
        {
            var names = types.Select(t => t?.GetValue<string>()).Where(t => t is not null).ToList();
            if (names.Remove("null")) clean["nullable"] = true;
            if (names.Count == 1) clean["type"] = names[0];
        }
        else if (source["type"] is { } type)
            clean["type"] = type.DeepClone();

        if (source["properties"] is JsonObject properties)
        {
            var cleaned = new JsonObject();
            foreach (var (name, value) in properties)
                cleaned[name] = value is JsonObject child ? CleanSchema(child) : new JsonObject();
            clean["properties"] = cleaned;
        }

        if (source["items"] is JsonObject items) clean["items"] = CleanSchema(items);
        else if (clean["type"]?.GetValue<string>() == "array") clean["items"] = new JsonObject(); // OpenAPI needs "items" on arrays

        if (source["additionalProperties"] is JsonObject extra) clean["additionalProperties"] = CleanSchema(extra);
        else if (source["additionalProperties"] is JsonValue flag && flag.TryGetValue<bool>(out var allowed)) clean["additionalProperties"] = allowed;

        // allOf / oneOf / anyOf are kept only when every member can be copied as it is.
        foreach (var keyword in new[] { "allOf", "oneOf", "anyOf" })
        {
            if (source[keyword] is not JsonArray members) continue;
            if (members.Any(m => m is not JsonObject o || o.ContainsKey("$ref"))) continue;
            clean[keyword] = new JsonArray(members.Select(m => (JsonNode?)CleanSchema((JsonObject)m!)).ToArray());
        }
        return clean;
    }
}
