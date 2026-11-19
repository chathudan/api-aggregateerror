using System.Net;
using ApiAggregator.Engine.Auth;
using ApiAggregator.Engine.Execution;
using ApiAggregator.Engine.Model;
using static ApiAggregator.Engine.Tests.TestData;

namespace ApiAggregator.Engine.Tests;

public class ExecutorTests
{
    private static (CompositionExecutor Executor, FakeDownstream Downstream) Create(Recipe recipe, FakeDownstream downstream) =>
        (Executor(Snapshot(recipe), downstream), downstream);

    private static InboundRequest Customer(string id) => new() { Route = new Dictionary<string, string?> { ["id"] = id } };

    private static Recipe AbcRecipe(bool cRequired = false, CacheSettings? cache = null) => new()
    {
        Id = "abc",
        Route = "customer/{id}",
        RequireAuthentication = false,
        Inputs = new(StringComparer.OrdinalIgnoreCase) { ["route.id"] = new InputDef { Required = true } },
        Steps =
        [
            Step("a", "a/getCustomer", true, ("request.route.id", "path.customerId")),
            Step("b", "b/getOrders", true, ("a.body.id", "header.X-Customer-Id")),
            Step("c", "c/getRecs", cRequired, ("b.body.tier", "header.X-Tier"))
        ],
        Cache = cache ?? new CacheSettings()
    };

    private static FakeDownstream Healthy() => new FakeDownstream()
        .On("a.test/api/customers/C-1", new { id = "C-1", name = "Alex" })
        .On("b.test/api/orders", new { tier = "gold", items = new[] { new { id = "o1" } } })
        .On("c.test/api/recommendations", new { items = new[] { "r1" } });

    [Fact]
    public async Task Values_flow_from_A_to_B_to_C()
    {
        var (executor, downstream) = Create(AbcRecipe(), Healthy());
        var result = await executor.ExecuteAsync(AbcRecipe(), Customer("C-1"), CallerContext.Anonymous(), ExecutionMode.Gateway, default);

        Assert.Equal(200, result.StatusCode);
        var b = downstream.Requests.Single(r => r.RequestUri!.Host == "b.test");
        Assert.Equal("C-1", b.Headers.GetValues("X-Customer-Id").Single());
        var c = downstream.Requests.Single(r => r.RequestUri!.Host == "c.test");
        Assert.Equal("gold", c.Headers.GetValues("X-Tier").Single());
    }

    [Fact]
    public async Task Trying_a_draft_again_after_renaming_a_step_uses_the_new_names()
    {
        // A draft keeps its version number while it is edited, so "Try it" must not reuse the plan of the earlier try.
        var (executor, _) = Create(AbcRecipe(), Healthy());
        Assert.Equal(200, (await executor.ExecuteAsync(AbcRecipe(), Customer("C-1"), CallerContext.Anonymous(), ExecutionMode.AdminTest, default)).StatusCode);

        var renamed = ApiAggregator.Engine.Authoring.RecipeRefactor.RenameStep(AbcRecipe(), "a", "customer").Recipe!;
        var result = await executor.ExecuteAsync(renamed, Customer("C-1"), CallerContext.Anonymous(), ExecutionMode.AdminTest, default);

        Assert.Equal(200, result.StatusCode);
        Assert.Equal(["customer", "b", "c"], result.Trace.Select(t => t.Alias));
    }

    [Fact]
    public async Task Optional_failure_gives_partial_response()
    {
        var downstream = Healthy().On("c.test/api/recommendations", new { }, HttpStatusCode.InternalServerError);
        var (executor, _) = Create(AbcRecipe(), downstream);
        var result = await executor.ExecuteAsync(AbcRecipe(), Customer("C-1"), CallerContext.Anonymous(), ExecutionMode.Gateway, default);

        Assert.Equal(200, result.StatusCode);
        Assert.True(result.Partial);
        Assert.Single(result.Body!["_errors"]!.AsArray());
    }

    [Fact]
    public async Task Required_failure_gives_502_with_root_cause_only()
    {
        var downstream = new FakeDownstream(); // A answers 404
        var (executor, _) = Create(AbcRecipe(), downstream);
        var result = await executor.ExecuteAsync(AbcRecipe(), Customer("C-1"), CallerContext.Anonymous(), ExecutionMode.Gateway, default);

        Assert.Equal(502, result.StatusCode);
        Assert.Single(result.Body!["errors"]!.AsArray());
        Assert.Equal("not_found", result.ErrorCode);
        Assert.Equal(StepOutcome.Failed, result.Trace.Single(t => t.Alias == "b").Outcome);
        Assert.Single(downstream.Requests); // B and C were never called
    }

    [Fact]
    public async Task Invalid_input_gives_400_without_any_downstream_call()
    {
        var (executor, downstream) = Create(AbcRecipe(), Healthy());
        var result = await executor.ExecuteAsync(AbcRecipe(), new InboundRequest(), CallerContext.Anonymous(), ExecutionMode.Gateway, default);
        Assert.Equal(400, result.StatusCode);
        Assert.Empty(downstream.Requests);
    }

    [Fact]
    public async Task Response_cache_serves_repeat_calls()
    {
        var recipe = AbcRecipe(cache: new CacheSettings { Enabled = true, TtlSeconds = 60 });
        var (executor, downstream) = Create(recipe, Healthy());
        var caller = new CallerContext { Subject = "user-1" };

        await executor.ExecuteAsync(recipe, Customer("C-1"), caller, ExecutionMode.Gateway, default);
        var second = await executor.ExecuteAsync(recipe, Customer("C-1"), caller, ExecutionMode.Gateway, default);

        Assert.True(second.FromCache);
        Assert.Equal(1, downstream.CallsTo("a.test/api/customers/C-1"));
    }

    [Fact]
    public async Task Cache_is_never_shared_between_callers()
    {
        var recipe = AbcRecipe(cache: new CacheSettings { Enabled = true, TtlSeconds = 60 });
        var (executor, downstream) = Create(recipe, Healthy());

        await executor.ExecuteAsync(recipe, Customer("C-1"), new CallerContext { Subject = "user-1" }, ExecutionMode.Gateway, default);
        var other = await executor.ExecuteAsync(recipe, Customer("C-1"), new CallerContext { Subject = "user-2" }, ExecutionMode.Gateway, default);

        Assert.False(other.FromCache);
        Assert.Equal(2, downstream.CallsTo("a.test/api/customers/C-1"));
    }

    [Fact]
    public async Task Cache_off_always_calls_downstream()
    {
        var (executor, downstream) = Create(AbcRecipe(), Healthy());
        var caller = new CallerContext { Subject = "user-1" };
        await executor.ExecuteAsync(AbcRecipe(), Customer("C-1"), caller, ExecutionMode.Gateway, default);
        await executor.ExecuteAsync(AbcRecipe(), Customer("C-1"), caller, ExecutionMode.Gateway, default);
        Assert.Equal(2, downstream.CallsTo("a.test/api/customers/C-1"));
    }
}
