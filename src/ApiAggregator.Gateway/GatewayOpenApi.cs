using System.Text.Json.Nodes;
using ApiAggregator.Engine.Config;

namespace ApiAggregator.Gateway;

/// <summary>Builds an OpenAPI 3.0 document describing every published aggregated endpoint.</summary>
public static class GatewayOpenApi
{
    public static JsonObject Build(ConfigSnapshot config)
    {
        var paths = new JsonObject();
        foreach (var recipe in config.Recipes.OrderBy(r => r.Route))
        {
            var parameters = new JsonArray();
            foreach (var (key, input) in recipe.Inputs)
            {
                var parts = key.Split('.', 2);
                parameters.Add(new JsonObject
                {
                    ["name"] = parts[1],
                    ["in"] = parts[0] == "route" ? "path" : parts[0],
                    ["required"] = parts[0] == "route" || input.Required,
                    ["schema"] = new JsonObject
                    {
                        ["type"] = input.Type switch { "int" => "integer", "bool" => "boolean", "number" => "number", _ => "string" },
                        ["format"] = input.Type == "date" ? "date" : null,
                        ["enum"] = input.Enum is null ? null : new JsonArray(input.Enum.Select(e => (JsonNode?)JsonValue.Create(e)).ToArray()),
                        ["default"] = input.Default
                    }
                });
            }

            var path = "/bff/" + recipe.Route.Trim('/');
            var operation = new JsonObject
            {
                ["operationId"] = recipe.Id,
                ["summary"] = recipe.Name,
                ["parameters"] = parameters,
                ["responses"] = new JsonObject
                {
                    ["200"] = new JsonObject { ["description"] = "Aggregated response. May include \"_errors\" when an optional step failed." },
                    ["400"] = new JsonObject { ["description"] = "Invalid input" },
                    ["401"] = new JsonObject { ["description"] = "Missing or invalid token" },
                    ["403"] = new JsonObject { ["description"] = $"Token lacks scope {recipe.RequiredScope}" },
                    ["502"] = new JsonObject { ["description"] = "A required downstream service failed" }
                }
            };
            if (recipe.RequireAuthentication) operation["security"] = new JsonArray(new JsonObject { ["bearer"] = new JsonArray() });

            var pathItem = paths[path] as JsonObject ?? new JsonObject();
            pathItem[recipe.Method.ToLowerInvariant()] = operation;
            paths[path] = pathItem;
        }

        return new JsonObject
        {
            ["openapi"] = "3.0.3",
            ["info"] = new JsonObject { ["title"] = "API Aggregator - aggregated endpoints", ["version"] = config.Version },
            ["paths"] = paths,
            ["components"] = new JsonObject
            {
                ["securitySchemes"] = new JsonObject
                {
                    ["bearer"] = new JsonObject { ["type"] = "http", ["scheme"] = "bearer", ["bearerFormat"] = "JWT" }
                }
            }
        };
    }
}
