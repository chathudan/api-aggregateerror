using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ApiAggregator.Engine.Auth;
using ApiAggregator.Engine.Auth.Steps;
using ApiAggregator.Engine.Config;
using ApiAggregator.Engine.Execution;
using ApiAggregator.Engine.Model;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

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

    /// <summary>A step that runs once per item of <paramref name="forEach"/> (with the item as the customer id of service A).</summary>
    public static StepDef ForEachStep(string alias, string endpoint, string forEach, bool distinct = true) => new()
    {
        Alias = alias, Endpoint = endpoint, ForEach = forEach, Distinct = distinct,
        Bind = [new BindingDef { From = "item", To = "path.customerId" }]
    };

    /// <summary>One list feeding a combine.</summary>
    public static CombineSource Source(string from, string key, string? name = null) => new() { From = from, Key = key, Name = name };

    public static Recipe Recipe(params StepDef[] steps) => new()
    {
        Id = "test",
        Route = "customer/{id}",
        RequireAuthentication = false,
        Inputs = new(StringComparer.OrdinalIgnoreCase) { ["route.id"] = new InputDef { Required = true } },
        Steps = steps.ToList()
    };

    /// <summary>
    /// A CRM like the sample service F: listContacts (contacts[]), listOrders (orders[] with id, contactId, total)
    /// and getContactNotes (/contacts/{contactId}/notes).
    /// </summary>
    public static ConfigSnapshot CrmSnapshot() => new(
    [
        new ApiService
        {
            Id = "crm", Name = "CRM", BaseUrl = "http://crm.test",
            Endpoints =
            [
                new ApiEndpoint { Id = "listContacts", Path = "/contacts", ResponseSchema = JsonNode.Parse("""
                    { "type": "object", "properties": { "contacts": { "type": "array", "items": { "type": "object",
                      "properties": { "id": { "type": "string" }, "name": { "type": "string" } } } } } }
                    """) },
                new ApiEndpoint { Id = "listOrders", Path = "/orders", ResponseSchema = JsonNode.Parse("""
                    { "type": "object", "properties": { "orders": { "type": "array", "items": { "type": "object",
                      "properties": { "id": { "type": "string" }, "contactId": { "type": "string" }, "total": { "type": "number" } } } } } }
                    """) },
                new ApiEndpoint { Id = "getContactNotes", Path = "/contacts/{contactId}/notes" }
            ]
        }
    ], [], [], "v1");

    /// <summary>Contacts, orders, and notes for each order's contact (the shape of the sample contacts-unique).</summary>
    public static Recipe CrmRecipe(string? notesForEach = "{{orders.body.orders[*].contactId}}", string notesFrom = "item") => new()
    {
        Id = "crm-notes",
        Route = "contacts/with-orders",
        RequireAuthentication = false,
        Steps =
        [
            new StepDef { Alias = "crm", DisplayName = "Contacts from CRM", Endpoint = "crm/listContacts" },
            new StepDef { Alias = "orders", DisplayName = "Orders from CRM", Endpoint = "crm/listOrders" },
            new StepDef
            {
                Alias = "notes", DisplayName = "Notes", Endpoint = "crm/getContactNotes", ForEach = notesForEach,
                Bind = [new BindingDef { From = notesFrom, To = "path.contactId" }]
            }
        ]
    };

    /// <summary>Services A (customers), B (orders), C (recommendations), like the samples.</summary>
    public static ConfigSnapshot Snapshot(params Recipe[] recipes) => SnapshotWithAuth(null, null, recipes);

    /// <summary>Same services, but <paramref name="authServiceId"/> signs in with <paramref name="profile"/>.</summary>
    public static ConfigSnapshot SnapshotWithAuth(string? authServiceId, AuthProfile? profile, params Recipe[] recipes)
    {
        ApiService Svc(string id, ApiEndpoint endpoint) => new()
        {
            Id = id, Name = id, BaseUrl = $"http://{id}.test/api", Endpoints = [endpoint],
            AuthProfileId = id == authServiceId ? profile?.Id : null
        };
        return new ConfigSnapshot(
        [
            Svc("a", Endpoint("getCustomer", "/customers/{customerId}")),
            Svc("b", Endpoint("getOrders", "/orders", new ParamDef { Name = "X-Customer-Id", In = "header", Required = true })),
            Svc("c", Endpoint("getRecs", "/recommendations"))
        ], profile is null ? [] : [profile], recipes, "v1");
    }

    /// <summary>A session-login profile whose password is the secret "svc-pw" and whose login URL is http://b.test/login.</summary>
    public static AuthProfile SessionLoginProfile(string id = "service-b-session") => new()
    {
        Id = id,
        Steps =
        [
            new AuthStepDef
            {
                Kind = "SessionLogin",
                Settings = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["loginUrl"] = "http://b.test/login", ["username"] = "svc-b", ["password"] = "secret:svc-pw", ["tokenPath"] = "token"
                }
            }
        ]
    };

    /// <summary>A Basic-auth profile (password from the secret "svc-pw").</summary>
    public static AuthProfile BasicProfile(string id = "service-b-basic") => new()
    {
        Id = id,
        Steps =
        [
            new AuthStepDef
            {
                Kind = "Basic",
                Settings = new(StringComparer.OrdinalIgnoreCase) { ["username"] = "svc-b", ["password"] = "secret:svc-pw" }
            }
        ]
    };

    public const string SecretValue = "s3cr3t-value";

    /// <summary>An executor wired to fake downstream services, with the Basic and SessionLogin steps and the secret "svc-pw".</summary>
    public static CompositionExecutor Executor(ConfigSnapshot snapshot, FakeDownstream downstream, ILogger<CompositionExecutor>? logger = null)
    {
        var options = Options.Create(new EngineOptions());
        var secrets = new ConfigurationSecretResolver(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Secrets:svc-pw"] = SecretValue }).Build());
        var auth = new DownstreamAuthenticator([new BasicAuthStep(), new SessionLoginStep()], secrets,
            new DownstreamCredentialCache(new MemoryCache(new MemoryCacheOptions()), NullLogger<DownstreamCredentialCache>.Instance), downstream);
        return new CompositionExecutor(new InMemoryConfigStore(snapshot), auth, downstream,
            new MemoryResponseCache(options), options, logger ?? NullLogger<CompositionExecutor>.Instance);
    }
}

/// <summary>A logger that remembers what was written, so tests can check what is (and isn't) logged.</summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    public List<(EventId Event, LogLevel Level, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (Entries) Entries.Add((eventId, logLevel, formatter(state, exception)));
    }
}

internal sealed class InMemoryConfigStore(ConfigSnapshot snapshot) : IConfigStore
{
    public ValueTask<ConfigSnapshot> GetSnapshotAsync(CancellationToken ct = default) => ValueTask.FromResult(snapshot);
    public Task SaveServiceAsync(ApiService service, CancellationToken ct = default) => Task.CompletedTask;
    public Task SaveAuthProfileAsync(AuthProfile profile, CancellationToken ct = default) => Task.CompletedTask;
    public Task<IReadOnlyList<Recipe>> GetDraftsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Recipe>>([]);
    public Task<Recipe?> GetDraftAsync(string id, CancellationToken ct = default) => Task.FromResult<Recipe?>(null);
    public Task SaveDraftAsync(Recipe draft, CancellationToken ct = default) => Task.CompletedTask;
    public Task<bool> DeleteServiceAsync(string id, CancellationToken ct = default) => Task.FromResult(false);
    public Task<bool> DeleteDraftAsync(string id, CancellationToken ct = default) => Task.FromResult(false);
    public Task PublishAsync(Recipe recipe, ChangeInfo change, CancellationToken ct = default) => Task.CompletedTask;
    public Task<bool> UnpublishAsync(string id, ChangeInfo change, CancellationToken ct = default) => Task.FromResult(false);
    public Task<IReadOnlyList<string>> GetHistoryIdsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>([]);
    public Task<IReadOnlyList<RecipeHistoryEntry>> GetHistoryAsync(string id, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<RecipeHistoryEntry>>([]);
    public Task SaveBrowserOriginsAsync(IReadOnlyList<ApiAggregator.Engine.BrowserAccess.BrowserOrigin> origins, ApiAggregator.Engine.BrowserAccess.BrowserAccessHistoryEntry change, CancellationToken ct = default) => Task.CompletedTask;
    public Task<IReadOnlyList<ApiAggregator.Engine.BrowserAccess.BrowserAccessHistoryEntry>> GetBrowserAccessHistoryAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ApiAggregator.Engine.BrowserAccess.BrowserAccessHistoryEntry>>([]);
}

/// <summary>Fake downstream: answers by "host path" and records every request it sees.</summary>
internal sealed class FakeDownstream : HttpMessageHandler, IHttpClientFactory
{
    private readonly Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> _routes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TimeSpan> _delays = new(StringComparer.OrdinalIgnoreCase);
    public List<HttpRequestMessage> Requests { get; } = [];

    /// <summary>Makes the answer for this "host path" arrive late (to test timeouts). Cancelling the request ends the wait.</summary>
    public FakeDownstream Slow(string hostAndPath, TimeSpan delay)
    {
        _delays[hostAndPath] = delay;
        return this;
    }

    public FakeDownstream On(string hostAndPath, object body, HttpStatusCode status = HttpStatusCode.OK)
    {
        _routes[hostAndPath] = _ => new HttpResponseMessage(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        return this;
    }

    /// <summary>Starts an answer, then the connection breaks while it's being read (with answer bytes in the message, as .NET does).</summary>
    public FakeDownstream OnCutOff(string hostAndPath)
    {
        _routes[hostAndPath] = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new CutOffStream()) { Headers = { ContentType = new("application/json") } }
        };
        return this;
    }

    private sealed class CutOffStream : Stream
    {
        private bool _sentFirst;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_sentFirst) throw new IOException("Received an invalid chunk terminator: 'SECRET-PATIENT-NAME'.");
            _sentFirst = true;
            var start = "{\"name\":\"SECRET-PATIENT-NAME"u8;
            start.CopyTo(buffer.Span);
            return ValueTask.FromResult(start.Length);
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Answers with an exact body text (e.g. broken JSON).</summary>
    public FakeDownstream OnRaw(string hostAndPath, string text, string contentType)
    {
        _routes[hostAndPath] = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, contentType) };
        return this;
    }

    public int CallsTo(string hostAndPath) => Requests.Count(r => Key(r) == hostAndPath);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        lock (Requests) Requests.Add(request);
        if (_delays.TryGetValue(Key(request), out var delay)) await Task.Delay(delay, ct);
        return _routes.TryGetValue(Key(request), out var respond)
            ? respond(request)
            : new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static string Key(HttpRequestMessage r) => $"{r.RequestUri!.Host}{r.RequestUri.AbsolutePath}";

    public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
}

internal static class Json
{
    public static string Of(JsonNode? node) => node?.ToJsonString() ?? "null";
}
