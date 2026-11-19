using System.Text.Json.Nodes;
using ApiAggregator.Engine.Expressions;

namespace ApiAggregator.Engine.Tests;

public class ExpressionTests
{
    private static readonly Scope Scope = new(new Dictionary<string, JsonNode?>
    {
        ["a"] = JsonNode.Parse("""
            { "status": 200, "headers": { "ETag": "v1" },
              "body": { "id": "C-1", "isPremium": true, "items": [ { "id": "o1" }, { "id": "o2" }, { "id": "o3" } ], "empty": "" } }
            """),
        ["request"] = JsonNode.Parse("""{ "route": { "id": "C-1" }, "query": {} }""")
    });

    private static JsonNode? Eval(string path) => ValuePath.Parse(path).Evaluate(Scope);

    [Fact] public void Property() => Assert.Equal("C-1", Eval("a.body.id")!.GetValue<string>());
    [Fact] public void Missing_property_is_null() => Assert.Null(Eval("a.body.nope.deeper"));
    [Fact] public void Index() => Assert.Equal("o2", Eval("a.body.items[1].id")!.GetValue<string>());
    [Fact] public void Negative_index() => Assert.Equal("o3", Eval("a.body.items[-1].id")!.GetValue<string>());
    [Fact] public void Slice_projects() => Assert.Equal("""["o1","o2"]""", Eval("a.body.items[0:2].id")!.ToJsonString());
    [Fact] public void Wildcard_projects() => Assert.Equal(3, Eval("a.body.items[*].id")!.AsArray().Count);
    [Fact] public void Quoted_property() => Assert.Equal("v1", Eval("a.headers['ETag']")!.GetValue<string>());
    [Fact] public void Case_insensitive_fallback() => Assert.Equal("v1", Eval("a.headers.etag")!.GetValue<string>());

    [Fact] public void Join() => Assert.Equal("o1,o2,o3", Eval("a.body.items[*].id | join(',')")!.GetValue<string>());
    [Fact] public void Count() => Assert.Equal(3, Eval("a.body.items | count")!.GetValue<int>());
    [Fact] public void Default_on_empty() => Assert.Equal("n/a", Eval("a.body.empty | default('n/a')")!.GetValue<string>());
    [Fact] public void Unknown_transform_throws() => Assert.Throws<FormatException>(() => Eval("a.body.id | explode"));
    [Fact] public void Bad_path_throws() => Assert.Throws<FormatException>(() => ValuePath.Parse("a.body.items[x"));

    [Fact]
    public void Single_expression_keeps_json_type()
    {
        Assert.True(Template.Resolve("{{a.body.isPremium}}", Scope)!.GetValue<bool>());
        Assert.IsType<JsonArray>(Template.Resolve("{{a.body.items}}", Scope));
    }

    [Fact] public void Mixed_text_interpolates() => Assert.Equal("Customer C-1 (200)", Template.Resolve("Customer {{a.body.id}} ({{a.status}})", Scope)!.GetValue<string>());

    [Fact]
    public void Template_object_is_resolved_and_detached()
    {
        var result = Template.ResolveNode(JsonNode.Parse("""{ "who": "{{a.body.id}}", "orders": "{{a.body.items[0:1]}}", "fixed": 5 }"""), Scope)!;
        Assert.Equal("""{"who":"C-1","orders":[{"id":"o1"}],"fixed":5}""", result.ToJsonString());
    }

    [Theory]
    [InlineData("{{a.body.isPremium}}", true)]
    [InlineData("{{a.body.isPremium}} == true", true)]
    [InlineData("{{a.body.id}} == c-1", true)]
    [InlineData("{{a.body.id}} != C-1", false)]
    [InlineData("{{a.body.empty}}", false)]
    [InlineData("{{a.body.missing}}", false)]
    public void Conditions(string condition, bool expected) =>
        Assert.Equal(expected, ApiAggregator.Engine.Execution.Conditions.IsTrue(condition, Scope));

    // ---- distinct

    private static string Distinct(string arrayJson, string transform = "distinct")
    {
        var scope = new Scope(new Dictionary<string, JsonNode?> { ["x"] = JsonNode.Parse(arrayJson) });
        return ValuePath.Parse($"x | {transform}").Evaluate(scope)!.ToJsonString();
    }

    [Fact] public void Distinct_removes_repeated_scalars_and_keeps_first_seen_order() => Assert.Equal("""["b","a","c"]""", Distinct("""["b","a","b","c","a"]"""));
    [Fact] public void Distinct_treats_the_number_1_and_the_text_1_as_different() => Assert.Equal("""[1,"1"]""", Distinct("""[1,"1",1]"""));
    [Fact] public void Distinct_compares_objects_by_their_full_JSON() =>
        Assert.Equal("""[{"id":1,"n":"a"},{"id":1,"n":"b"}]""", Distinct("""[{"id":1,"n":"a"},{"id":1,"n":"b"},{"id":1,"n":"a"}]"""));
    [Fact] public void Distinct_by_key_keeps_the_first_object_for_each_value() =>
        Assert.Equal("""[{"id":1,"n":"a"},{"id":2,"n":"c"}]""", Distinct("""[{"id":1,"n":"a"},{"id":1,"n":"b"},{"id":2,"n":"c"}]""", "distinct('id')"));
    [Fact] public void Distinct_by_dotted_key_reads_nested_values() =>
        Assert.Equal("""[{"customer":{"id":"C1"}}]""", Distinct("""[{"customer":{"id":"C1"}},{"customer":{"id":"C1"}}]""", "distinct('customer.id')"));
    [Fact] public void Distinct_by_key_keeps_items_where_the_key_is_missing_or_null() =>
        Assert.Equal("""[{"n":"a"},{"id":null},{"n":"a"},{"id":1}]""", Distinct("""[{"n":"a"},{"id":null},{"n":"a"},{"id":1},{"id":1}]""", "distinct('id')"));
    [Fact] public void Distinct_by_key_compares_the_exact_value() => Assert.Equal("""[{"id":"A"},{"id":"a"}]""", Distinct("""[{"id":"A"},{"id":"a"},{"id":"A"}]""", "distinct('id')"));
    [Fact] public void Distinct_on_something_that_is_not_an_array_changes_nothing() => Assert.Equal("""{"a":1}""", Distinct("""{"a":1}"""));
}
