using System.Text.Json.Nodes;
using ApiAggregator.Admin.Services;
using ApiAggregator.Engine.Expressions;
using ApiAggregator.Engine.Execution;
using ApiAggregator.Engine.Model;

namespace ApiAggregator.Admin.Tests;

/// <summary>A step's values in plain words, "Send another value", and a whole JSON body split into one value per field.</summary>
public class ValueTargetsTests
{
    [Theory]
    [InlineData("path.customerId", "customerId", "path")]
    [InlineData("query.status", "status", "query")]
    [InlineData("header.X-Tier", "X-Tier", "header")]
    [InlineData("body.customerId", "customerId", "body")]
    [InlineData("body", "Whole body", "body")]
    public void Each_value_is_named_with_where_it_goes(string target, string name, string where) =>
        Assert.Equal((name, where), ValueTargets.Describe(target));

    [Theory]
    [InlineData("header", " X-Channel ", "header.X-Channel")]
    [InlineData("body", "status", "body.status")]
    [InlineData("header", "  ", null)]       // no name yet
    [InlineData("path", "id", null)]         // path values come from the endpoint itself
    [InlineData("query", null, null)]
    public void Another_value_is_added_only_with_a_name_and_a_place_on_offer(string kind, string? name, string? expected) =>
        Assert.Equal(expected, ValueTargets.Compose(kind, name));

    private static BindingForm Body(string json, string? @default = null) =>
        new() { Mode = BindingMode.FixedValue, FixedIsJson = true, FixedValue = json, Default = @default };

    [Fact]
    public void A_whole_body_of_simple_fields_splits_into_one_value_per_field()
    {
        var split = ValueTargets.SplitBody(Body("""{ "customerId": "{{customer.body.id}}", "status": "open", "limit": 5, "note": "for {{customer.body.id}}" }"""))!;

        Assert.Equal(["body.customerId", "body.status", "body.limit", "body.note"], split.Keys);
        Assert.Equal((BindingMode.FromSource, "customer.body.id"), (split["body.customerId"].Mode, split["body.customerId"].Expression));
        Assert.Equal((BindingMode.FixedValue, "open", false), (split["body.status"].Mode, split["body.status"].FixedValue, split["body.status"].FixedIsJson));
        Assert.Equal(("5", true), (split["body.limit"].FixedValue, split["body.limit"].FixedIsJson));
        Assert.Equal("for {{customer.body.id}}", split["body.note"].FixedValue); // text around a value stays a template
    }

    [Theory]
    [InlineData("""{ "filter": { "status": "open" } }""")]   // nested object
    [InlineData("""{ "ids": ["a", "b"] }""")]                  // list
    [InlineData("""{ "status": null }""")]
    [InlineData("""{ "page.size": 5 }""")]                    // a dot would nest it
    [InlineData("""{}""")]
    [InlineData("""["a"]""")]
    [InlineData("""{ not json""")]
    public void A_body_that_would_change_if_split_is_left_as_written(string json) =>
        Assert.Null(ValueTargets.SplitBody(Body(json)));

    [Fact]
    public void A_body_with_a_default_or_from_one_value_is_left_as_written()
    {
        Assert.Null(ValueTargets.SplitBody(Body("""{ "status": "open" }""", @default: "x")));
        Assert.Null(ValueTargets.SplitBody(new BindingForm { Mode = BindingMode.FromSource, Expression = "customer.body" }));
        Assert.Null(ValueTargets.SplitBody(null));
    }

    [Fact]
    public void A_split_body_sends_exactly_the_same_request_body()
    {
        var whole = Body("""{ "customerId": "{{customer.body.id}}", "status": "{{request.query.status}}", "limit": 5, "note": "for {{customer.body.id}}" }""");
        var scope = new Scope(new Dictionary<string, JsonNode?>
        {
            ["customer"] = JsonNode.Parse("""{ "body": { "id": "C-10042" } }"""),
            ["request"] = JsonNode.Parse("""{ "query": { "status": "open" } }""")
        });
        var service = new ApiService { Id = "b", BaseUrl = "http://b.test" };
        var endpoint = new ApiEndpoint { Id = "search", Method = "POST", Path = "/orders/search" };

        var before = RequestBuilder.Build(service, endpoint, [whole.ToDef("body")], scope).Body;
        var after = RequestBuilder.Build(service, endpoint, ValueTargets.SplitBody(whole)!.Select(kv => kv.Value.ToDef(kv.Key)), scope).Body;

        Assert.True(JsonNode.DeepEquals(before, after), $"{before?.ToJsonString()} != {after?.ToJsonString()}");
    }
}
