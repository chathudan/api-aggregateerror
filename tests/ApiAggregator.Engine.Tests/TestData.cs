using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ApiAggregator.Engine.Config;
using ApiAggregator.Engine.Model;

namespace ApiAggregator.Engine.Tests;

/// <summary>Small builders so each test reads like the scenario it checks.</summary>
internal static class TestData
{
    public static ApiService Service(string id, params ApiEndpoint[] endpoints) =>
        new() { Id = id, Name = id, BaseUrl = $"http://{id}.test/api", Endpoints = endpoints.ToList() };

    public static ApiEndpoint Endpoint(string id, string path, params ParamDef[] parameters) =>
        new() { Id = id, Path = path, Parameters = parameters.ToList() };

    public static StepDef Step(string alias, string endpoint, bool required = true, params (string From, string To)[] bind) =>
        new() { Alias = alias, Endpoint = endpoint, Required = required, Bind = bind.Select(b => new BindingDef { From = b.From, To = b.To }).ToList() };

    public static Recipe Recipe(params StepDef[] steps) => new()
    {
        Id = "test",
        Route = "customer/{id}",
        RequireAuthentication = false,
        Inputs = new(StringComparer.OrdinalIgnoreCase) { ["route.id"] = new InputDef { Required = true } },
        Steps = steps.ToList()
    };

    /// <summary>Services A (customers), B (orders), C (recommendations), like the samples.</summary>
    public static ConfigSnapshot Snapshot(params Recipe[] recipes) => new(
    [
        Service("a", Endpoint("getCustomer", "/customers/{customerId}")),
        Service("b", Endpoint("getOrders", "/orders", new ParamDef { Name = "X-Customer-Id", In = "header", Required = true })),
        Service("c", Endpoint("getRecs", "/recommendations"))
    ], [], recipes, "v1");
}

internal sealed class InMemoryConfigStore(ConfigSnapshot snapshot) : IConfigStore
{
    public ValueTask<ConfigSnapshot> GetSnapshotAsync(CancellationToken ct = default) => ValueTask.FromResult(snapshot);
    public Task SaveServiceAsync(ApiService service, CancellationToken ct = default) => Task.CompletedTask;
    public Task SaveAuthProfileAsync(AuthProfile profile, CancellationToken ct = default) => Task.CompletedTask;
    public Task<IReadOnlyList<Recipe>> GetDraftsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Recipe>>([]);
    public Task<Recipe?> GetDraftAsync(string id, CancellationToken ct = default) => Task.FromResult<Recipe?>(null);
    public Task SaveDraftAsync(Recipe draft, CancellationToken ct = default) => Task.CompletedTask;
    public Task PublishAsync(Recipe recipe, CancellationToken ct = default) => Task.CompletedTask;
    public Task<IReadOnlyList<Recipe>> GetHistoryAsync(string id, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Recipe>>([]);
}

/// <summary>Fake downstream: answers by "host path" and records every request it sees.</summary>
internal sealed class FakeDownstream : HttpMessageHandler, IHttpClientFactory
{
    private readonly Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> _routes = new(StringComparer.OrdinalIgnoreCase);
    public List<HttpRequestMessage> Requests { get; } = [];

    public FakeDownstream On(string hostAndPath, object body, HttpStatusCode status = HttpStatusCode.OK)
    {
        _routes[hostAndPath] = _ => new HttpResponseMessage(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        return this;
    }

    public int CallsTo(string hostAndPath) => Requests.Count(r => Key(r) == hostAndPath);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        lock (Requests) Requests.Add(request);
        return Task.FromResult(_routes.TryGetValue(Key(request), out var respond)
            ? respond(request)
            : new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private static string Key(HttpRequestMessage r) => $"{r.RequestUri!.Host}{r.RequestUri.AbsolutePath}";

    public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
}

internal static class Json
{
    public static string Of(JsonNode? node) => node?.ToJsonString() ?? "null";
}
