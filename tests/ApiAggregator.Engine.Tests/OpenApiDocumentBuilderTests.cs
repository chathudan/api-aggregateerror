using System.Text.Json.Nodes;
using ApiAggregator.Engine.Authoring;
using ApiAggregator.Engine.Config;
using ApiAggregator.Engine.Model;

namespace ApiAggregator.Engine.Tests;

public class OpenApiDocumentBuilderTests
{
    // Service "a" returns a customer; service "b" returns a list of orders; "lines" has no schema at all.
    private static readonly JsonNode CustomerSchema = JsonNode.Parse("""{ "type": "object", "properties": { "name": { "type": "string", "description": "Full name" }, "age": { "type": "integer" } } }""")!;
    private static readonly JsonNode OrdersSchema = JsonNode.Parse("""
        { "type": "object", "properties": { "items": { "type": "array", "items": { "type": "object", "properties": { "id": { "type": "string" }, "total": { "type": "number" } } } } } }
        """)!;

    private static ConfigSnapshot Snapshot(params Recipe[] recipes) => new(
    [
        TestData.Service("service-a", new ApiEndpoint { Id = "getCustomer", Path = "/customers/{id}", ResponseSchema = CustomerSchema.DeepClone() }),
        TestData.Service("service-b", new ApiEndpoint { Id = "getOrders", Path = "/orders", ResponseSchema = OrdersSchema.DeepClone() },
            TestData.Endpoint("getLines", "/lines"))
    ], [], recipes, "v1");

    private static Recipe MakeRecipe(string responseJson, params StepDef[] steps) => new()
    {
        Id = "r", Name = "Customer", Route = "customer/{id}", RequireAuthentication = false,
        Inputs = new(StringComparer.OrdinalIgnoreCase) { ["route.id"] = new InputDef { Required = true, Pattern = "^[A-Z0-9-]+$" } },
        Steps = steps.ToList(),
        Response = responseJson.Length == 0 ? null : JsonNode.Parse(responseJson)
    };

    private static readonly StepDef A = TestData.Step("a", "service-a/getCustomer");
    private static readonly StepDef B = TestData.Step("b", "service-b/getOrders");

    private static JsonObject Doc(Recipe recipe, bool internalDetails = false) =>
        OpenApiDocumentBuilder.Build(Snapshot(recipe), new OpenApiBuildOptions { IncludeInternalDetails = internalDetails });

    private static JsonNode Operation(JsonObject doc) => doc["paths"]!["/bff/customer/{id}"]!["get"]!;

    /// <summary>The 200 body schema, as a property of the response object.</summary>
    private static JsonNode Field(Recipe recipe, string name) =>
        Operation(Doc(recipe))["responses"]!["200"]!["content"]!["application/json"]!["schema"]!["properties"]![name]!;

    [Fact]
    public void Field_from_a_step_body_gets_the_source_type()
    {
        var field = Field(MakeRecipe("""{ "n": "{{a.body.name}}" }""", A), "n");
        Assert.Equal("string", field["type"]!.GetValue<string>());
        Assert.Equal("Full name", field["description"]!.GetValue<string>());
    }

    [Fact]
    public void Slice_gives_an_array_of_the_item_schema()
    {
        var field = Field(MakeRecipe("""{ "o": "{{b.body.items[0:5]}}" }""", B), "o");
        Assert.Equal("array", field["type"]!.GetValue<string>());
        Assert.Equal("number", field["items"]!["properties"]!["total"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void Projection_gives_an_array_of_the_property_schema()
    {
        var field = Field(MakeRecipe("""{ "ids": "{{b.body.items[*].id}}" }""", B), "ids");
        Assert.Equal("array", field["type"]!.GetValue<string>());
        Assert.Equal("string", field["items"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void Count_is_an_integer()
    {
        var field = Field(MakeRecipe("""{ "n": "{{b.body.items | count}}" }""", B), "n");
        Assert.Equal("integer", field["type"]!.GetValue<string>());
    }

    [Fact]
    public void Index_and_first_give_the_item_schema()
    {
        var recipe = MakeRecipe("""{ "x": "{{b.body.items[0]}}", "y": "{{b.body.items | first}}" }""", B);
        Assert.Equal("object", Field(recipe, "x")["type"]!.GetValue<string>());
        Assert.Equal("object", Field(recipe, "y")["type"]!.GetValue<string>());
    }

    [Fact]
    public void Unknown_field_is_any_value()
    {
        var recipe = MakeRecipe("""{ "x": "{{a.body.nope}}", "y": "{{zzz.body}}", "z": "{{a.body.name[0]}}" }""", A);
        Assert.Equal(0, Field(recipe, "x").AsObject().Count);
        Assert.Equal(0, Field(recipe, "y").AsObject().Count);
        Assert.Equal(0, Field(recipe, "z").AsObject().Count);
    }

    [Fact]
    public void Step_without_a_schema_is_any_value()
    {
        var recipe = MakeRecipe("""{ "x": "{{l.body.anything}}" }""", TestData.Step("l", "service-b/getLines"));
        Assert.Equal(0, Field(recipe, "x").AsObject().Count);
    }

    [Fact]
    public void Without_a_response_template_each_step_becomes_a_property()
    {
        var properties = Operation(Doc(MakeRecipe("", A, B)))["responses"]!["200"]!["content"]!["application/json"]!["schema"]!["properties"]!;
        Assert.Equal("string", properties["a"]!["properties"]!["name"]!["type"]!.GetValue<string>());
        Assert.Equal("array", properties["b"]!["properties"]!["items"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void For_each_step_gives_an_array()
    {
        var lines = new StepDef { Alias = "lines", Endpoint = "service-a/getCustomer", ForEach = "{{b.body.items[*].id}}" };
        var recipe = MakeRecipe("""{ "l": "{{lines[*].body}}", "s": "{{lines[*].status}}" }""", B, lines);
        var body = Field(recipe, "l");
        Assert.Equal("array", body["type"]!.GetValue<string>());
        Assert.Equal("string", body["items"]!["properties"]!["name"]!["type"]!.GetValue<string>());
        Assert.Equal("integer", Field(recipe, "s")["items"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void For_each_step_without_a_schema_is_an_array_of_any_value()
    {
        var lines = new StepDef { Alias = "lines", Endpoint = "service-b/getLines", ForEach = "{{b.body.items[*].id}}" };
        var field = Field(MakeRecipe("""{ "l": "{{lines[*].body}}" }""", B, lines), "l");
        Assert.Equal("array", field["type"]!.GetValue<string>());
        Assert.Equal(0, field["items"]!.AsObject().Count);
    }

    [Fact]
    public void Text_mixed_with_an_expression_is_a_string_and_request_values_use_the_input_schema()
    {
        var recipe = MakeRecipe("""{ "t": "Hello {{a.body.name}}", "r": "{{request.route.id}}" }""", A);
        Assert.Equal("string", Field(recipe, "t")["type"]!.GetValue<string>());
        Assert.Equal("^[A-Z0-9-]+$", Field(recipe, "r")["pattern"]!.GetValue<string>());
    }

    [Fact]
    public void Optional_steps_add_an_errors_property()
    {
        var optional = new StepDef { Alias = "o", Endpoint = "service-a/getCustomer", Required = false };
        Assert.NotNull(Field(MakeRecipe("""{ "n": "{{a.body.name}}" }""", A, optional), "_errors"));
        Assert.Null(Operation(Doc(MakeRecipe("""{ "n": "{{a.body.name}}" }""", A)))["responses"]!["200"]!["content"]!["application/json"]!["schema"]!["properties"]!["_errors"]);
    }

    [Fact]
    public void Route_input_is_required_with_its_pattern()
    {
        var parameter = Operation(Doc(MakeRecipe("", A)))["parameters"]!.AsArray().Single();
        Assert.Equal("path", parameter["in"]!.GetValue<string>());
        Assert.True(parameter["required"]!.GetValue<bool>());
        Assert.Equal("^[A-Z0-9-]+$", parameter["schema"]!["pattern"]!.GetValue<string>());
    }

    [Fact]
    public void Query_enum_and_default_are_described_with_an_example()
    {
        var recipe = new Recipe
        {
            Id = "r", Route = "x", RequireAuthentication = false, Steps = [A],
            Inputs = new(StringComparer.OrdinalIgnoreCase)
            {
                ["query.status"] = new InputDef { Enum = ["open", "closed"], Default = "open" },
                ["query.page"] = new InputDef { Type = "int", Min = 1, Max = 100, Default = "1" }
            }
        };
        var parameters = OpenApiDocumentBuilder.Build(Snapshot(recipe), new OpenApiBuildOptions())["paths"]!["/bff/x"]!["get"]!["parameters"]!.AsArray();
        var status = parameters.Single(p => p!["name"]!.GetValue<string>() == "status")!;
        Assert.False(status["required"]!.GetValue<bool>());
        Assert.Equal(2, status["schema"]!["enum"]!.AsArray().Count);
        Assert.Equal("open", status["schema"]!["default"]!.GetValue<string>());
        Assert.Equal("open", status["example"]!.GetValue<string>());

        var page = parameters.Single(p => p!["name"]!.GetValue<string>() == "page")!;
        Assert.Equal("integer", page["schema"]!["type"]!.GetValue<string>());
        Assert.Equal(1L, page["schema"]!["default"]!.GetValue<long>());
        Assert.Equal(100.0, page["schema"]!["maximum"]!.GetValue<double>());
    }

    [Fact]
    public void Public_endpoint_has_no_security_and_no_401()
    {
        var operation = Operation(Doc(MakeRecipe("", A)));
        Assert.Null(operation["security"]);
        Assert.Null(operation["responses"]!["401"]);
    }

    [Fact]
    public void Protected_endpoint_has_security_401_403_and_names_its_scope()
    {
        var recipe = new Recipe { Id = "r", Route = "customer/{id}", RequiredScope = "customer.read", Steps = [A] };
        var operation = Operation(OpenApiDocumentBuilder.Build(Snapshot(recipe), new OpenApiBuildOptions()));
        Assert.NotNull(operation["security"]);
        Assert.NotNull(operation["responses"]!["401"]);
        Assert.NotNull(operation["responses"]!["403"]);
        Assert.True(operation["description"]!.GetValue<string>().Contains("`customer.read`"));
    }

    [Fact]
    public void Internal_service_names_are_listed_only_when_asked()
    {
        var recipe = MakeRecipe("", A);
        Assert.False(Operation(Doc(recipe, internalDetails: false))["description"]!.GetValue<string>().Contains("service-a"));
        Assert.True(Operation(Doc(recipe, internalDetails: true))["description"]!.GetValue<string>().Contains("service-a"));
    }

    [Fact]
    public void One_operation_per_recipe_and_a_problem_component_with_every_code()
    {
        var second = new Recipe { Id = "r2", Route = "orders", Steps = [B] };
        var doc = OpenApiDocumentBuilder.Build(Snapshot(MakeRecipe("", A), second), new OpenApiBuildOptions { ServerUrl = "http://localhost:5200/" });

        Assert.Equal(2, doc["paths"]!.AsObject().Count);
        Assert.Equal("http://localhost:5200", doc["servers"]![0]!["url"]!.GetValue<string>());
        var codes = doc["components"]!["schemas"]!["Problem"]!["properties"]!["code"]!["enum"]!.AsArray().Select(c => c!.GetValue<string>()).ToList();
        Assert.True(codes.Contains("auth_failed"));
        Assert.True(codes.Contains("invalid_input"));
        Assert.False(codes.Contains("dependency_failed"));
    }

    [Fact]
    public void Document_never_contains_null_values()
    {
        Assert.False(OpenApiDocumentBuilder.Build(Snapshot(MakeRecipe("", A)), new OpenApiBuildOptions()).ToJsonString().Contains(":null"));
    }
}
