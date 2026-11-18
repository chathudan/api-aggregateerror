using System.Text.Json.Nodes;
using ApiAggregator.Engine.Auth;
using ApiAggregator.Engine.Execution;
using ApiAggregator.Engine.Expressions;
using ApiAggregator.Engine.Model;
using static ApiAggregator.Engine.Tests.TestData;

namespace ApiAggregator.Engine.Tests;

public class RequestAndInputTests
{
    private static Scope ScopeWith(string json) => new(new Dictionary<string, JsonNode?> { ["v"] = JsonNode.Parse(json) });

    private static BuiltRequest Build(string path, string valueJson, params (string From, string To)[] bind) =>
        RequestBuilder.Build(Service("svc"), Endpoint("e", path), bind.Select(b => new BindingDef { From = b.From, To = b.To }), ScopeWith(valueJson));

    [Fact]
    public void Path_values_are_escaped()
    {
        var r = Build("/customers/{id}", """{ "id": "a/b?c" }""", ("v.id", "path.id"));
        Assert.Equal("http://svc.test/api/customers/a%2Fb%3Fc", r.Uri.AbsoluteUri);
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    public void Dot_segments_are_rejected(string value) =>
        Assert.Throws<BindingException>(() => Build("/customers/{id}", $$"""{ "id": "{{value}}" }""", ("v.id", "path.id")));

    [Fact]
    public void Missing_path_value_is_an_error() =>
        Assert.Throws<BindingException>(() => Build("/customers/{id}", "{}", ("v.id", "path.id")));

    [Fact]
    public void Header_injection_is_blocked() =>
        Assert.Throws<BindingException>(() => Build("/x", """{ "h": "ok\r\nX-Evil: 1" }""", ("v.h", "header.X-Test")));

    [Fact]
    public void Array_becomes_repeated_query_parameter()
    {
        var r = Build("/x", """{ "ids": [1, 2] }""", ("v.ids", "query.id"));
        Assert.Equal("?id=1&id=2", r.Uri.Query);
    }

    [Fact]
    public void Body_fields_are_built_from_paths()
    {
        var r = Build("/x", """{ "id": "C-1" }""", ("v.id", "body.customer.id"));
        Assert.Equal("""{"customer":{"id":"C-1"}}""", r.Body!.ToJsonString());
    }

    // ------------------------------------------------------------------ inputs

    private static Recipe RecipeWithInputs() => new()
    {
        Id = "r",
        Route = "x/{id}",
        Inputs = new(StringComparer.OrdinalIgnoreCase)
        {
            ["route.id"] = new InputDef { Required = true, Pattern = "^[A-Z]-[0-9]+$" },
            ["query.status"] = new InputDef { Enum = ["open", "closed"], Default = "open" },
            ["query.page"] = new InputDef { Type = "int", Min = 1, Max = 10 }
        }
    };

    private static (JsonObject Request, List<string> Errors) Bind(string id, string? status = null, string? page = null, string? extra = null)
    {
        var query = new Dictionary<string, string?>();
        if (status is not null) query["status"] = status;
        if (page is not null) query["page"] = page;
        if (extra is not null) query["sneaky"] = extra;
        return InputBinder.Bind(RecipeWithInputs(), new InboundRequest { Route = new Dictionary<string, string?> { ["id"] = id }, Query = query }, CallerContext.Anonymous());
    }

    [Fact]
    public void Valid_inputs_are_typed_and_defaulted()
    {
        var (request, errors) = Bind("C-1", page: "3");
        Assert.Empty(errors);
        Assert.Equal("open", request["query"]!["status"]!.GetValue<string>());
        Assert.Equal(3L, request["query"]!["page"]!.GetValue<long>());
    }

    [Fact] public void Pattern_is_enforced() => Assert.NotEmpty(Bind("C-1;DROP").Errors);
    [Fact] public void Enum_is_enforced() => Assert.NotEmpty(Bind("C-1", status: "weird").Errors);
    [Fact] public void Range_is_enforced() => Assert.NotEmpty(Bind("C-1", page: "99").Errors);

    [Fact]
    public void Undeclared_inputs_never_get_through()
    {
        var (request, errors) = Bind("C-1", extra: "x");
        Assert.Empty(errors);
        Assert.Null(request["query"]!["sneaky"]);
    }
}
