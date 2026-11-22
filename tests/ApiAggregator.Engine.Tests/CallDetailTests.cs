using System.Net;
using System.Text.Json.Nodes;
using ApiAggregator.Engine.Auth;
using ApiAggregator.Engine.Execution;
using ApiAggregator.Engine.Model;
using static ApiAggregator.Engine.Tests.TestData;

namespace ApiAggregator.Engine.Tests;

/// <summary>
/// Try it shows each call's request and response (ADR 0016): only in admin test runs, without sign-in values, capped,
/// and a failed call's answer never reaches the app's response.
/// </summary>
public class CallDetailTests
{
    private static Recipe CustomerAndOrders() => new()
    {
        Id = "details",
        Route = "customer/{id}",
        RequireAuthentication = false,
        Inputs = new(StringComparer.OrdinalIgnoreCase) { ["route.id"] = new InputDef { Required = true }, ["query.status"] = new InputDef() },
        Steps =
        [
            Step("a", "a/getCustomer", true, ("request.route.id", "path.customerId")),
            Step("b", "b/getOrders", true, ("a.body.id", "header.X-Customer-Id"), ("request.query.status", "query.status"))
        ]
    };

    private static InboundRequest Request() => new()
    {
        Route = new Dictionary<string, string?> { ["id"] = "C-1" },
        Query = new Dictionary<string, string?> { ["status"] = "open" }
    };

    private static Task<CompositionResult> Run(Recipe recipe, FakeDownstream downstream, ExecutionMode mode) =>
        Executor(SnapshotWithAuth("b", BasicProfile(), recipe), downstream)
            .ExecuteAsync(recipe, Request(), CallerContext.Anonymous("corr-1"), mode, default);

    private static FakeDownstream Healthy() => new FakeDownstream()
        .On("a.test/api/customers/C-1", new { id = "C-1", name = "Alex" })
        .On("b.test/api/orders", new { tier = "gold" });

    [Fact]
    public async Task Try_it_shows_each_calls_full_request_and_the_services_answer()
    {
        var result = await Run(CustomerAndOrders(), Healthy(), ExecutionMode.AdminTest);

        var b = Assert.Single(result.Trace.Single(t => t.Alias == "b").Details!);
        Assert.Equal("GET http://b.test/api/orders?status=open", b.Request); // query values shown here
        Assert.Equal("C-1", b.RequestHeaders!["X-Customer-Id"]!.GetValue<string>());
        Assert.Equal(200, b.Status);
        Assert.Equal("gold", b.Response!["body"]!["tier"]!.GetValue<string>());
        Assert.False(b.Truncated);
        // The summary line stays as before (and as the command line prints it).
        Assert.Equal("GET http://b.test/api/orders?status=…", result.Trace.Single(t => t.Alias == "b").Request);
    }

    [Fact]
    public async Task Sign_in_values_never_appear_in_the_details()
    {
        var result = await Run(CustomerAndOrders(), Healthy(), ExecutionMode.AdminTest);

        var details = string.Join("\n", result.Trace.SelectMany(t => t.Details ?? []).Select(d => System.Text.Json.JsonSerializer.Serialize(d)));
        Assert.DoesNotContain("Authorization", details);
        Assert.DoesNotContain(SecretValue, details);
        Assert.DoesNotContain(Convert.ToBase64String("svc-b:s3cr3t-value"u8.ToArray()), details);
    }

    [Fact]
    public async Task The_gateway_never_gets_call_details()
    {
        var result = await Run(CustomerAndOrders(), Healthy(), ExecutionMode.Gateway);

        Assert.All(result.Trace, t => Assert.Null(t.Details));
    }

    [Fact]
    public async Task A_failed_call_shows_what_the_service_answered_but_the_app_never_gets_it()
    {
        var downstream = Healthy().On("b.test/api/orders", new { message = "no such account C-1" }, HttpStatusCode.NotFound);

        var result = await Run(CustomerAndOrders(), downstream, ExecutionMode.AdminTest);

        var b = Assert.Single(result.Trace.Single(t => t.Alias == "b").Details!);
        Assert.Equal(404, b.Status);
        Assert.Equal("no such account C-1", b.Response!["body"]!["message"]!.GetValue<string>());
        Assert.DoesNotContain("no such account", result.Body!.ToJsonString());
    }

    [Fact]
    public async Task A_very_large_answer_is_cut_in_the_details_only()
    {
        var big = new string('x', CallDetail.MaxBodyChars + 100);
        var downstream = Healthy().On("a.test/api/customers/C-1", new { id = "C-1", notes = big });

        var result = await Run(CustomerAndOrders(), downstream, ExecutionMode.AdminTest);

        var a = Assert.Single(result.Trace.Single(t => t.Alias == "a").Details!);
        Assert.True(a.Truncated);
        Assert.True(a.Response!.GetValue<string>().Length <= CallDetail.MaxBodyChars + 1);
        Assert.Equal("C-1", result.Trace.Single(t => t.Alias == "b").Details![0].RequestHeaders!["X-Customer-Id"]!.GetValue<string>()); // the step itself used the full answer
    }

    [Fact]
    public async Task A_step_that_runs_for_each_item_shows_the_first_twenty_calls()
    {
        var ids = Enumerable.Range(1, 25).Select(i => $"C-{i}").ToArray();
        var downstream = new FakeDownstream().On("c.test/api/recommendations", new { ids });
        foreach (var id in ids) downstream.On($"a.test/api/customers/{id}", new { id });
        var recipe = Recipe(Step("list", "c/getRecs"), ForEachStep("each", "a/getCustomer", "{{list.body.ids[*]}}"));

        var result = await Executor(Snapshot(recipe), downstream)
            .ExecuteAsync(recipe, new InboundRequest { Route = new Dictionary<string, string?> { ["id"] = "x" } }, CallerContext.Anonymous(), ExecutionMode.AdminTest, default);

        var each = result.Trace.Single(t => t.Alias == "each");
        Assert.Equal(25, each.Calls);
        Assert.Equal(CallDetail.MaxCallsShown, each.Details!.Count);
    }
}
