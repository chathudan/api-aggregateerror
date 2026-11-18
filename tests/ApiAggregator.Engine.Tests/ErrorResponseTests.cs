using System.Net;
using ApiAggregator.Engine.Config;
using ApiAggregator.Engine.Auth;
using ApiAggregator.Engine.Execution;
using ApiAggregator.Engine.Model;
using Microsoft.Extensions.Logging;
using static ApiAggregator.Engine.Tests.TestData;

namespace ApiAggregator.Engine.Tests;

/// <summary>The error contract: stable codes, problem-details bodies, no secrets or internal detail for callers.</summary>
public class ErrorResponseTests
{
    private static InboundRequest Customer(string id) => new() { Route = new Dictionary<string, string?> { ["id"] = id } };

    private static Recipe AbcRecipe(bool bRequired = true, bool cRequired = false, int? bTimeoutMs = null)
    {
        var b = Step("b", "b/getOrders", bRequired, ("a.body.id", "header.X-Customer-Id"));
        return new Recipe
        {
            Id = "abc",
            Route = "customer/{id}",
            RequireAuthentication = false,
            Inputs = new(StringComparer.OrdinalIgnoreCase) { ["route.id"] = new InputDef { Required = true } },
            Steps =
            [
                Step("a", "a/getCustomer", true, ("request.route.id", "path.customerId")),
                new StepDef { Alias = b.Alias, Endpoint = b.Endpoint, Required = b.Required, Bind = b.Bind, TimeoutMs = bTimeoutMs },
                Step("c", "c/getRecs", cRequired, ("b.body.tier", "header.X-Tier"))
            ]
        };
    }

    private static FakeDownstream Healthy() => new FakeDownstream()
        .On("a.test/api/customers/C-1", new { id = "C-1", name = "Alex" })
        .On("b.test/api/orders", new { tier = "gold" })
        .On("c.test/api/recommendations", new { items = new[] { "r1" } });

    private static Task<CompositionResult> Run(Recipe recipe, FakeDownstream downstream, ConfigSnapshot? snapshot = null,
        ExecutionMode? mode = null, ILogger<CompositionExecutor>? logger = null) =>
        Executor(snapshot ?? Snapshot(recipe), downstream, logger)
            .ExecuteAsync(recipe, Customer("C-1"), CallerContext.Anonymous("corr-1"), mode ?? ExecutionMode.Gateway, default);

    // ---------------------------------------------------------------- downstream sign-in

    [Fact]
    public async Task Failed_login_gives_502_auth_failed_and_no_secret_in_the_body()
    {
        var recipe = AbcRecipe();
        var downstream = Healthy().On("b.test/login", new { }, HttpStatusCode.Unauthorized);
        var result = await Run(recipe, downstream, SnapshotWithAuth("b", SessionLoginProfile(), recipe));

        Assert.Equal(502, result.StatusCode);
        Assert.Equal("auth_failed", result.ErrorCode);
        Assert.False(result.Retryable);
        Assert.Equal("auth_failed", result.Body!["code"]!.GetValue<string>());
        Assert.Equal("urn:api-aggregator:error:auth_failed", result.Body["type"]!.GetValue<string>());
        Assert.Equal("The gateway couldn't authenticate with a required service.", result.Body["title"]!.GetValue<string>());
        Assert.Equal("corr-1", result.Body["correlationId"]!.GetValue<string>());

        var entry = Assert.Single(result.Body["errors"]!.AsArray());
        Assert.Equal("b", entry!["service"]!.GetValue<string>());
        Assert.False(entry["retryable"]!.GetValue<bool>());

        var json = result.Body.ToJsonString();
        Assert.False(json.Contains(SecretValue));
        Assert.False(json.Contains("secret:"));
        Assert.False(json.Contains("service-b-session")); // the internal reason stays out of the caller's body
    }

    [Fact]
    public async Task Service_answering_401_to_a_basic_profile_gives_unauthorized()
    {
        var recipe = AbcRecipe();
        var downstream = Healthy().On("b.test/api/orders", new { }, HttpStatusCode.Unauthorized);
        var result = await Run(recipe, downstream, SnapshotWithAuth("b", BasicProfile(), recipe));

        Assert.Equal(502, result.StatusCode);
        Assert.Equal("unauthorized", result.ErrorCode);
        Assert.Equal("The gateway couldn't authenticate with a required service.", result.Body!["title"]!.GetValue<string>());
        Assert.Equal(401, result.Body["errors"]![0]!["status"]!.GetValue<int>());
    }

    [Fact]
    public async Task Admin_test_runs_show_the_internal_detail_for_an_auth_failure()
    {
        var recipe = AbcRecipe();
        var downstream = Healthy().On("b.test/login", new { }, HttpStatusCode.Unauthorized);
        var result = await Run(recipe, downstream, SnapshotWithAuth("b", SessionLoginProfile(), recipe), ExecutionMode.AdminTest);

        var trace = result.Trace.Single(t => t.Alias == "b");
        Assert.Equal("auth_failed", trace.ErrorCode);
        Assert.Equal("Login for profile 'service-b-session' failed with 401.", trace.Detail);
        Assert.False((trace.Detail ?? "").Contains(SecretValue));
    }

    [Fact]
    public async Task Gateway_runs_never_include_the_internal_detail()
    {
        var recipe = AbcRecipe();
        var downstream = Healthy().On("b.test/login", new { }, HttpStatusCode.Unauthorized);
        var result = await Run(recipe, downstream, SnapshotWithAuth("b", SessionLoginProfile(), recipe), ExecutionMode.Gateway);

        Assert.Null(result.Trace.Single(t => t.Alias == "b").Detail);
    }

    [Fact]
    public async Task Missing_secret_gives_auth_failed_without_naming_the_secret_to_the_caller()
    {
        var recipe = AbcRecipe();
        var profile = SessionLoginProfile();
        profile.Steps[0].Settings["password"] = "secret:not-configured";
        var result = await Run(recipe, Healthy(), SnapshotWithAuth("b", profile, recipe));

        Assert.Equal("auth_failed", result.ErrorCode);
        Assert.False(result.Body!.ToJsonString().Contains("not-configured"));
    }

    // ---------------------------------------------------------------- downstream answers

    [Fact]
    public async Task Service_503_on_a_required_step_gives_502_and_is_retryable()
    {
        var recipe = AbcRecipe();
        var result = await Run(recipe, Healthy().On("b.test/api/orders", new { }, HttpStatusCode.ServiceUnavailable));

        Assert.Equal(502, result.StatusCode);
        Assert.Equal("server_error", result.ErrorCode);
        Assert.True(result.Retryable);
        Assert.True(result.Body!["retryable"]!.GetValue<bool>());
        Assert.Equal("A required service call failed.", result.Body["title"]!.GetValue<string>());
        Assert.Equal("b: b answered 503.", result.Body["detail"]!.GetValue<string>());
    }

    [Fact]
    public async Task Service_503_on_an_optional_step_gives_a_partial_200_with_the_error_listed()
    {
        var recipe = AbcRecipe(bRequired: false);
        var result = await Run(recipe, Healthy().On("b.test/api/orders", new { }, HttpStatusCode.ServiceUnavailable));

        Assert.Equal(200, result.StatusCode);
        Assert.True(result.Partial);
        Assert.Equal("server_error", result.Body!["_errors"]![0]!["code"]!.GetValue<string>());
        Assert.Equal(503, result.Body["_errors"]![0]!["status"]!.GetValue<int>());
        Assert.Single(result.Body["_errors"]!.AsArray()); // c was skipped because of b: not a root cause
    }

    [Theory]
    [InlineData(403, "forbidden", false)]
    [InlineData(404, "not_found", false)]
    [InlineData(409, "bad_request", false)]
    [InlineData(422, "bad_request", false)]
    [InlineData(429, "rate_limited", true)]
    [InlineData(500, "server_error", true)]
    public async Task Downstream_status_maps_to_a_stable_code(int status, string code, bool retryable)
    {
        var recipe = AbcRecipe();
        var result = await Run(recipe, Healthy().On("b.test/api/orders", new { }, (HttpStatusCode)status));

        Assert.Equal(code, result.ErrorCode);
        Assert.Equal(retryable, result.Retryable);
    }

    [Fact]
    public async Task Invalid_json_from_a_service_gives_invalid_response()
    {
        var recipe = AbcRecipe();
        var downstream = Healthy();
        downstream.OnRaw("b.test/api/orders", "{ not json", "application/json");
        var result = await Run(recipe, downstream);

        Assert.Equal("invalid_response", result.ErrorCode);
        Assert.False(result.Retryable);
    }

    [Fact]
    public async Task Required_step_that_times_out_gives_504()
    {
        var recipe = AbcRecipe(bTimeoutMs: 50);
        var downstream = Healthy().Slow("b.test/api/orders", TimeSpan.FromSeconds(5));
        var result = await Run(recipe, downstream);

        Assert.Equal(504, result.StatusCode);
        Assert.Equal("timeout", result.ErrorCode);
        Assert.True(result.Retryable);
        Assert.Equal("b didn't answer in time.", result.Body!["errors"]![0]!["message"]!.GetValue<string>());
    }

    // ---------------------------------------------------------------- invalid input

    [Fact]
    public async Task Invalid_input_gives_400_problem_details()
    {
        var recipe = AbcRecipe();
        var result = await Executor(Snapshot(recipe), Healthy())
            .ExecuteAsync(recipe, new InboundRequest(), CallerContext.Anonymous("corr-2"), ExecutionMode.Gateway, default);

        Assert.Equal(400, result.StatusCode);
        Assert.Equal("invalid_input", result.ErrorCode);
        Assert.Equal("invalid_input", result.Body!["code"]!.GetValue<string>());
        Assert.Equal("corr-2", result.Body["correlationId"]!.GetValue<string>());
        Assert.Equal("invalid_input", result.Body["errors"]![0]!["code"]!.GetValue<string>());
        Assert.False(string.IsNullOrEmpty(result.Body["errors"]![0]!["message"]!.GetValue<string>()));
    }

    // ---------------------------------------------------------------- logging

    [Fact]
    public async Task Auth_failure_is_logged_as_StepFailed_and_no_log_line_contains_the_secret()
    {
        var recipe = AbcRecipe();
        var logger = new CapturingLogger<CompositionExecutor>();
        var downstream = Healthy().On("b.test/login", new { }, HttpStatusCode.Unauthorized);
        await Run(recipe, downstream, SnapshotWithAuth("b", SessionLoginProfile(), recipe), logger: logger);

        Assert.Contains(logger.Entries, e => e.Event.Id == 1101 && e.Level == LogLevel.Warning && e.Message.Contains("auth_failed"));
        Assert.Contains(logger.Entries, e => e.Event.Id == 1200);
        Assert.Contains(logger.Entries, e => e.Event.Id == 1002);
        Assert.DoesNotContain(logger.Entries, e => e.Message.Contains(SecretValue));
        Assert.DoesNotContain(logger.Entries, e => e.Message.Contains("secret:"));
    }
}
