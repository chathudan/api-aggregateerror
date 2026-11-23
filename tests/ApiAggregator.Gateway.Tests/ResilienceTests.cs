using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using ApiAggregator.Engine.Config;
using ApiAggregator.Engine.Execution;
using ApiAggregator.Engine.Model;
using Microsoft.Extensions.DependencyInjection;
using Polly.CircuitBreaker;
using Polly.RateLimiting;
using Polly.Timeout;

namespace ApiAggregator.Gateway.Tests;

/// <summary>
/// What the benchmark found (ADR 0017): a broken service must never turn into a 500, must not stop calls to other
/// services, and too much traffic gets a quick 503 instead of exhausting the gateway.
/// </summary>
public class ResilienceTests
{
    [Theory]
    [InlineData("open circuit")]
    [InlineData("pipeline timeout")]
    [InlineData("too many calls")]
    public async Task A_call_the_resilience_pipeline_refuses_is_a_502_never_a_500(string rejection)
    {
        Exception thrown = rejection switch
        {
            "open circuit" => new BrokenCircuitException("open"),
            "pipeline timeout" => new TimeoutRejectedException("late"),
            _ => new RateLimiterRejectedException("busy")
        };
        await using var app = new GatewayApp { OverrideServices = s => Downstream(s, _ => throw thrown) };

        var response = await app.ClientWithToken("people.read").GetAsync("/bff/people/P-1");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("unreachable", JsonNode.Parse(await response.Content.ReadAsStringAsync())!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_failing_service_opens_only_its_own_circuit()
    {
        await using var app = new GatewayApp
        {
            OverrideServices = s => Downstream(s, request => request.RequestUri!.Host == "people.test"
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(GatewayApp.PersonJson, Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.InternalServerError))
        };
        AddFailingService(app.ConfigDir);
        var client = app.ClientWithToken("people.read");

        // Enough failures (with retries) to open the failing service's circuit breaker.
        var failures = await Task.WhenAll(Enumerable.Range(0, 150).Select(_ => client.GetAsync("/bff/failing/F-1")));
        Assert.All(failures, f => Assert.Equal(HttpStatusCode.BadGateway, f.StatusCode));

        // Another service is still called.
        var other = await client.GetAsync("/bff/people/P-1");
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
    }

    [Fact]
    public async Task Calls_to_one_service_beyond_its_limit_fail_at_once_instead_of_piling_up()
    {
        var release = new TaskCompletionSource();
        var firstArrived = new TaskCompletionSource();
        await using var app = new GatewayApp
        {
            Settings = new() { ["Gateway:MaxCallsInFlightPerService"] = "1" },
            OverrideServices = s => s.AddHttpClient(EngineHttpClients.Downstream).ConfigurePrimaryHttpMessageHandler(() => new Held(firstArrived, release.Task))
        };
        var client = app.ClientWithToken("people.read");

        var first = client.GetAsync("/bff/people/P-1"); // holds the only place
        await firstArrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = await client.GetAsync("/bff/people/P-1");
        release.SetResult();

        Assert.Equal(HttpStatusCode.BadGateway, second.StatusCode);
        Assert.Equal("unreachable", JsonNode.Parse(await second.Content.ReadAsStringAsync())!["code"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.OK, (await first).StatusCode);
    }

    [Fact]
    public async Task Too_many_requests_at_once_get_a_quick_503_with_retry_after_and_health_checks_still_answer()
    {
        await using var app = new GatewayApp { Settings = new() { ["Gateway:MaxConcurrentRequests"] = "0" } };
        var client = app.ClientWithToken("people.read");

        var busy = await client.GetAsync("/bff/people/P-1");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, busy.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(5), busy.Headers.RetryAfter?.Delta);
        Assert.Equal("overloaded", JsonNode.Parse(await busy.Content.ReadAsStringAsync())!["code"]!.GetValue<string>());
        Problems.AssertStandardHeaders(busy);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
    }

    [Fact]
    public void The_OpenAPI_document_is_built_for_a_few_addresses_only_whatever_Host_callers_send()
    {
        var cache = new OpenApiSpecCache(includeInternalDetails: false, maxServers: 4);
        var v1 = new ConfigSnapshot([], [], [new Recipe { Id = "a", Route = "a", Status = RecipeStatus.Published }], "v1");

        for (var i = 0; i < 50; i++) cache.Get(v1, $"http://host-{i}.example");
        Assert.Equal(5, cache.Builds); // 4 addresses of their own, then one shared copy without a server

        Assert.Contains("http://host-0.example", cache.Get(v1, "http://host-0.example"));
        Assert.DoesNotContain("host-49", cache.Get(v1, "http://host-49.example"));
        Assert.Equal(5, cache.Builds);

        cache.Get(new ConfigSnapshot([], [], [], "v1"), "http://host-0.example"); // same version: no rebuild
        cache.Get(new ConfigSnapshot([], [], [], "v2"), "http://host-0.example");
        Assert.Equal(6, cache.Builds); // a new version is built again
    }

    // ------------------------------------------------------------------ helpers

    private static void Downstream(IServiceCollection services, Func<HttpRequestMessage, HttpResponseMessage> answer) =>
        services.AddHttpClient(EngineHttpClients.Downstream).ConfigurePrimaryHttpMessageHandler(() => new Answering(answer));

    private sealed class Answering(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(answer(request));
    }

    /// <summary>Holds each call until released, and says when the first one arrives.</summary>
    private sealed class Held(TaskCompletionSource arrived, Task release) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            arrived.TrySetResult();
            await release.WaitAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(GatewayApp.PersonJson, Encoding.UTF8, "application/json") };
        }
    }

    /// <summary>A second, anonymous endpoint on a service whose every call fails (written before the first request loads the config).</summary>
    private static void AddFailingService(string dir)
    {
        var services = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "services.json")))!.AsArray();
        services.Add(JsonNode.Parse("""
            { "id": "failing", "baseUrl": "http://failing.test/api", "endpoints": [
                { "id": "getThing", "method": "GET", "path": "/things/{thingId}",
                  "parameters": [ { "name": "thingId", "in": "path", "required": true, "type": "string" } ] } ] }
            """));
        File.WriteAllText(Path.Combine(dir, "services.json"), services.ToJsonString());
        File.WriteAllText(Path.Combine(dir, "recipes", "failing.json"), """
            {
              "id": "failing", "name": "Failing thing", "status": "published", "method": "GET", "route": "failing/{id}",
              "requireAuthentication": false,
              "inputs": { "route.id": { "type": "string", "required": true, "pattern": "^[A-Za-z0-9-]{1,20}$" } },
              "steps": [ { "alias": "t", "endpoint": "failing/getThing", "bind": [ { "from": "request.route.id", "to": "path.thingId" } ] } ],
              "response": { "thing": "{{t.body}}" }
            }
            """);
    }
}
