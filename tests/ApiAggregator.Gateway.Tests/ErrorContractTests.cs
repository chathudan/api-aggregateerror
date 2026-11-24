using ApiAggregator.Engine.BrowserAccess;
using ApiAggregator.Engine.Config;
using ApiAggregator.Engine.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ApiAggregator.Gateway.Tests;

/// <summary>Error bodies callers see: stable codes, problem details, nothing internal.</summary>
public class ErrorContractTests(GatewayApp app) : IClassFixture<GatewayApp>
{
    [Fact]
    public async Task An_unknown_address_gives_404_not_found()
    {
        var response = await app.CreateClient().GetAsync("/bff/nothing/here");

        await Problems.AssertProblem(response, 404, "not_found");
    }

    [Fact]
    public async Task A_downstream_sign_in_failure_gives_502_auth_failed_without_naming_the_secret()
    {
        var response = await app.CreateClient().GetAsync("/bff/locked/T-1");

        var body = await Problems.AssertProblem(response, 502, "auth_failed");
        Assert.False(body["retryable"]?.GetValue<bool>() ?? false);
        Assert.False(response.Headers.Contains("Retry-After")); // retrying won't fix a sign-in problem
        var json = body.ToJsonString();
        Assert.DoesNotContain("locked-api-key", json);
        Assert.DoesNotContain("secret:", json);
        Assert.DoesNotContain("locked-key", json); // the auth profile id is internal too
    }

    [Fact]
    public async Task Too_many_requests_gives_429_rate_limited_with_retry_after()
    {
        using var limited = new GatewayApp { Settings = { ["Gateway:RequestsPerMinutePerCaller"] = "1" } };
        var client = limited.CreateClient();

        await client.GetAsync("/bff/nothing/here"); // uses up the one request allowed this minute
        var response = await client.GetAsync("/bff/nothing/here");

        var body = await Problems.AssertProblem(response, 429, "rate_limited");
        Assert.True(body["retryable"]!.GetValue<bool>());
        Assert.Equal(TimeSpan.FromSeconds(60), response.Headers.RetryAfter?.Delta);
    }

    [Fact]
    public async Task An_unexpected_exception_gives_500_internal_error_without_the_exception_text()
    {
        using var broken = new GatewayApp { OverrideServices = s => s.Replace(ServiceDescriptor.Singleton<IConfigStore, ExplodingConfigStore>()) };

        var response = await broken.CreateClient().GetAsync("/bff/people/P-1");

        var body = await Problems.AssertProblem(response, 500, "internal_error");
        var json = body.ToJsonString();
        Assert.DoesNotContain(ExplodingConfigStore.Message, json);
        Assert.DoesNotContain(nameof(InvalidOperationException), json);
        Assert.DoesNotContain("ExplodingConfigStore", json); // no stack trace
        Assert.Null(body["detail"]);
    }

    /// <summary>Fails every call with a message that must never reach a caller.</summary>
    private sealed class ExplodingConfigStore : IConfigStore
    {
        public const string Message = "internal detail: patient P-SECRET-42 in /var/data";

        public ValueTask<ConfigSnapshot> GetSnapshotAsync(CancellationToken ct = default) => throw new InvalidOperationException(Message);
        public Task SaveServiceAsync(ApiService service, CancellationToken ct = default) => throw new InvalidOperationException(Message);
        public Task SaveAuthProfileAsync(AuthProfile profile, CancellationToken ct = default) => throw new InvalidOperationException(Message);
        public Task<IReadOnlyList<Recipe>> GetDraftsAsync(CancellationToken ct = default) => throw new InvalidOperationException(Message);
        public Task<Recipe?> GetDraftAsync(string id, CancellationToken ct = default) => throw new InvalidOperationException(Message);
        public Task SaveDraftAsync(Recipe draft, CancellationToken ct = default) => throw new InvalidOperationException(Message);
        public Task<bool> DeleteServiceAsync(string id, CancellationToken ct = default) => throw new InvalidOperationException(Message);
        public Task<bool> DeleteDraftAsync(string id, CancellationToken ct = default) => throw new InvalidOperationException(Message);
        public Task PublishAsync(Recipe recipe, ChangeInfo change, CancellationToken ct = default) => throw new InvalidOperationException(Message);
        public Task<bool> UnpublishAsync(string id, ChangeInfo change, CancellationToken ct = default) => throw new InvalidOperationException(Message);
        public Task<IReadOnlyList<string>> GetHistoryIdsAsync(CancellationToken ct = default) => throw new InvalidOperationException(Message);
        public Task<IReadOnlyList<RecipeHistoryEntry>> GetHistoryAsync(string id, CancellationToken ct = default) => throw new InvalidOperationException(Message);
        public Task SaveBrowserOriginsAsync(IReadOnlyList<BrowserOrigin> origins, BrowserAccessHistoryEntry change, CancellationToken ct = default) => throw new InvalidOperationException(Message);
        public Task<IReadOnlyList<BrowserAccessHistoryEntry>> GetBrowserAccessHistoryAsync(CancellationToken ct = default) => throw new InvalidOperationException(Message);
        public Task AppendActivityAsync(ApiAggregator.Engine.Config.ActivityEntry entry, CancellationToken ct = default) => throw new InvalidOperationException(Message);
        public Task<IReadOnlyList<ApiAggregator.Engine.Config.ActivityEntry>> GetActivityAsync(string? kind, string? id, int max, CancellationToken ct = default) => throw new InvalidOperationException(Message);
    }
}
