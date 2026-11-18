using System.Text.Json.Nodes;
using ApiAggregator.Engine.Authoring;
using ApiAggregator.Engine.Config;
using ApiAggregator.Engine.Model;

namespace ApiAggregator.Engine.Tests;

public class AutoBinderTests
{
    private static readonly JsonNode CustomerSchema = JsonNode.Parse("""
        { "type": "object", "properties": { "id": {}, "accountNo": {}, "segmentCode": {} } }
        """)!;

    private static ConfigSnapshot Config() => new(
    [
        new ApiService
        {
            Id = "a", BaseUrl = "http://a.test",
            Endpoints = [ new ApiEndpoint { Id = "getCustomer", Path = "/customers/{customerId}", ResponseSchema = CustomerSchema } ]
        },
        new ApiService
        {
            Id = "b", BaseUrl = "http://b.test",
            Endpoints =
            [
                new ApiEndpoint
                {
                    Id = "getOrders", Path = "/orders",
                    Parameters =
                    [
                        new ParamDef { Name = "X-Customer-Id", In = "header", Required = true },
                        new ParamDef { Name = "account", In = "query" },
                        new ParamDef { Name = "accountNo", In = "query" },
                        new ParamDef { Name = "status", In = "query" }
                    ]
                }
            ]
        }
    ], [], [], "v1");

    private static Recipe Recipe() => new()
    {
        Id = "r",
        Route = "customer/{id}",
        Inputs = new(StringComparer.OrdinalIgnoreCase)
        {
            ["route.id"] = new InputDef { Required = true },
            ["query.status"] = new InputDef()
        },
        Steps =
        [
            new StepDef { Alias = "getCustomer", DisplayName = "Get customer", Endpoint = "a/getCustomer" },
            new StepDef { Alias = "getOrders", Endpoint = "b/getOrders" }
        ]
    };

    private static Dictionary<string, string> Suggest(string alias) =>
        AutoBinder.Suggest(Recipe(), alias, Config()).ToDictionary(s => s.Target, s => s.Expression);

    [Fact] public void First_step_path_param_uses_the_route_input() =>
        Assert.Equal("request.route.id", Suggest("getCustomer")["path.customerId"]);

    [Fact] public void Thing_id_header_uses_the_earlier_steps_id() =>
        Assert.Equal("getCustomer.body.id", Suggest("getOrders")["header.X-Customer-Id"]);

    [Fact] public void Same_named_field_from_an_earlier_step() =>
        Assert.Equal("getCustomer.body.accountNo", Suggest("getOrders")["query.accountNo"]);

    [Fact] public void Same_named_input_wins() =>
        Assert.Equal("request.query.status", Suggest("getOrders")["query.status"]);

    [Fact] public void No_guess_when_nothing_matches() =>
        Assert.False(Suggest("getOrders").ContainsKey("query.account"));

    [Theory]
    [InlineData("X-Customer-Id", "customerid")]
    [InlineData("customer_id", "customerid")]
    [InlineData("customerId", "customerid")]
    public void Names_are_normalised(string input, string expected) => Assert.Equal(expected, AutoBinder.Normalize(input));
}
