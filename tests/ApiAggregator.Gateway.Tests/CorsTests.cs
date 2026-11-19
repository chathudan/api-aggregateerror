using System.Net;
using System.Text.Json.Nodes;
using ApiAggregator.Engine.Config;
using ApiAggregator.Engine.Logging.Live;
using ApiAggregator.Engine.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ApiAggregator.Gateway.Tests;

/// <summary>A gateway with one operations origin, a pattern for admin entries, and a recipe that always times out.</summary>
public sealed class CorsGateway : IDisposable
{
    public const string Allowed = "https://app.example.test";
    public const string Pattern = "https://*.example.test";

    public GatewayApp App { get; }

    public CorsGateway() : this(new Dictionary<string, string>()) { }

    internal CorsGateway(Dictionary<string, string> extra, Action<IServiceCollection>? overrides = null)
    {
        var settings = new Dictionary<string, string>
        {
            ["Gateway:Cors:AllowedOrigins:0"] = Allowed,
            ["Gateway:Cors:AllowedOriginPatterns:0"] = Pattern,
            ["Aggregator:Config:ReloadCheckSeconds"] = "1"
        };
        foreach (var (k, v) in extra) settings[k] = v;
        App = new GatewayApp { Settings = settings, OverrideServices = overrides };
        AddSlowRecipe(App.ConfigDir);
    }

    /// <summary>GET /bff/slow/{id}: its only step never answers within the 100 ms step timeout, so the answer is 504.</summary>
    private static void AddSlowRecipe(string dir)
    {
        var services = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "services.json")))!.AsArray();
        services.Add(JsonNode.Parse("""
            { "id": "slow", "baseUrl": "http://slow.test/api", "endpoints": [
                { "id": "getSlow", "method": "GET", "path": "/slow/{slowId}",
                  "parameters": [ { "name": "slowId", "in": "path", "required": true, "type": "string" } ] } ] }
            """));
        File.WriteAllText(Path.Combine(dir, "services.json"), services.ToJsonString());
        File.WriteAllText(Path.Combine(dir, "recipes", "slow.json"), """
            {
              "id": "slow", "name": "Slow", "status": "published", "method": "GET", "route": "slow/{id}", "requireAuthentication": false,
              "inputs": { "route.id": { "type": "string", "required": true, "pattern": "^[A-Za-z0-9-]{1,20}$" } },
              "steps": [ { "alias": "s", "endpoint": "slow/getSlow", "timeoutMs": 100, "bind": [ { "from": "request.route.id", "to": "path.slowId" } ] } ],
              "response": { "slow": "{{s.body}}" }
            }
            """);
    }

    public void Dispose() => App.Dispose();
}

/// <summary>Browser access to /bff/* (ADR 0015): preflights, actual requests, errors, live changes, logging.</summary>
public sealed class CorsTests(CorsGateway gateway) : IClassFixture<CorsGateway>
{
    private const string Evil = "https://evil.test";

    private GatewayApp App => gateway.App;

    // ------------------------------------------------------------------ preflight

    [Fact]
    public async Task A_preflight_from_an_allowed_origin_is_answered_with_what_it_may_send()
    {
        var response = await App.CreateClient().SendAsync(Preflight("/bff/people/P-1", CorsGateway.Allowed, "GET", "authorization,x-correlation-id"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(CorsGateway.Allowed, Single(response, "Access-Control-Allow-Origin"));
        Assert.Contains("GET", Single(response, "Access-Control-Allow-Methods"));
        var headers = Single(response, "Access-Control-Allow-Headers");
        Assert.Contains("authorization", headers, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("x-correlation-id", headers, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("600", Single(response, "Access-Control-Max-Age"));
        AssertNoCredentials(response);
    }

    [Fact]
    public async Task A_preflight_from_another_origin_gets_no_permission()
    {
        var response = await App.CreateClient().SendAsync(Preflight("/bff/people/P-1", Evil, "GET", "authorization"));

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.False(response.Headers.Contains("Access-Control-Allow-Methods"));
        AssertNoCredentials(response);
    }

    [Fact]
    public async Task Preflights_need_no_token_and_never_reach_the_rate_limiter()
    {
        using var limited = new CorsGateway(new() { ["Gateway:RequestsPerMinutePerCaller"] = "1" });
        var client = limited.App.CreateClient();

        for (var i = 0; i < 3; i++)
        {
            var preflight = await client.SendAsync(Preflight("/bff/people/P-1", CorsGateway.Allowed, "GET", "authorization"));
            Assert.Equal(HttpStatusCode.NoContent, preflight.StatusCode);
        }

        // The one request allowed this minute is still there.
        var response = await client.GetAsync("/bff/nothing/here");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ------------------------------------------------------------------ actual requests

    [Fact]
    public async Task An_allowed_origin_may_read_the_answer_and_the_exposed_headers()
    {
        var client = App.ClientWithToken("people.read");

        var response = await client.SendAsync(Get("/bff/people/P-1", CorsGateway.Allowed));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(CorsGateway.Allowed, Single(response, "Access-Control-Allow-Origin"));
        var exposed = Single(response, "Access-Control-Expose-Headers");
        foreach (var header in new[] { "X-Correlation-Id", "X-Aggregator-Partial", "X-Aggregator-Cache", "Retry-After" })
            Assert.Contains(header, exposed, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(response.Headers.Vary, v => v.Equals("Origin", StringComparison.OrdinalIgnoreCase));
        AssertNoCredentials(response);
    }

    [Fact]
    public async Task Another_origin_gets_the_answer_without_permission_to_read_it()
    {
        var response = await App.ClientWithToken("people.read").SendAsync(Get("/bff/people/P-1", Evil));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode); // CORS isn't access control: the token is
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.Contains(response.Headers.Vary, v => v.Equals("Origin", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("/bff/openapi.json")]
    [InlineData("/health/live")]
    public async Task Only_the_aggregated_endpoints_answer_cross_origin(string path)
    {
        var response = await App.CreateClient().SendAsync(Get(path, CorsGateway.Allowed));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    // ------------------------------------------------------------------ errors keep the headers, so pages can read them

    [Theory]
    [InlineData("/bff/people/not%20valid!", "people.read", 400)]
    [InlineData("/bff/people/P-1", null, 401)]
    [InlineData("/bff/people/P-1", "orders.read", 403)]
    [InlineData("/bff/nothing/here", null, 404)]
    [InlineData("/bff/locked/T-1", null, 502)]
    [InlineData("/bff/slow/S-1", null, 504)]
    public async Task Error_responses_keep_the_cors_headers(string path, string? scope, int status)
    {
        var client = scope is null ? App.CreateClient() : App.ClientWithToken(scope);

        var response = await client.SendAsync(Get(path, CorsGateway.Allowed));

        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal(CorsGateway.Allowed, Single(response, "Access-Control-Allow-Origin"));
        Assert.Contains("X-Correlation-Id", Single(response, "Access-Control-Expose-Headers"), StringComparison.OrdinalIgnoreCase);
        AssertNoCredentials(response);
    }

    [Fact]
    public async Task Too_many_requests_keeps_the_cors_headers()
    {
        using var limited = new CorsGateway(new() { ["Gateway:RequestsPerMinutePerCaller"] = "1" });
        var client = limited.App.CreateClient();
        await client.SendAsync(Get("/bff/nothing/here", CorsGateway.Allowed));

        var response = await client.SendAsync(Get("/bff/nothing/here", CorsGateway.Allowed));

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(CorsGateway.Allowed, Single(response, "Access-Control-Allow-Origin"));
        AssertNoCredentials(response);
    }

    [Fact]
    public async Task An_unexpected_exception_keeps_the_cors_headers()
    {
        using var broken = new CorsGateway(new(), s => s.Replace(ServiceDescriptor.Singleton<IConfigStore, BrokenStore>()));

        var response = await broken.App.CreateClient().SendAsync(Get("/bff/people/P-1", CorsGateway.Allowed));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(CorsGateway.Allowed, Single(response, "Access-Control-Allow-Origin"));
        AssertNoCredentials(response);
    }

    // ------------------------------------------------------------------ changes apply without a restart

    [Fact]
    public async Task A_header_input_of_a_newly_published_recipe_is_allowed_without_a_restart()
    {
        var client = App.CreateClient();
        Assert.DoesNotContain("x-tenant", await AllowedHeaders(client), StringComparison.OrdinalIgnoreCase);

        File.WriteAllText(Path.Combine(App.ConfigDir, "recipes", "tenant.json"), """
            {
              "id": "tenant", "name": "Tenant", "status": "published", "method": "GET", "route": "tenant/{id}", "requireAuthentication": false,
              "inputs": { "route.id": { "type": "string", "required": true, "pattern": "^[A-Za-z0-9-]{1,20}$" },
                          "header.X-Tenant": { "type": "string", "pattern": "^[a-z]{1,10}$" } },
              "steps": [ { "alias": "p", "endpoint": "people/getPerson", "bind": [ { "from": "request.route.id", "to": "path.personId" } ] } ],
              "response": { "id": "{{p.body.id}}" }
            }
            """);

        await Eventually(async () => (await AllowedHeaders(client)).Contains("x-tenant", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task An_origin_added_and_removed_in_the_admin_app_applies_without_a_restart()
    {
        const string added = "https://portal.example.test";
        using var fresh = new CorsGateway();
        var client = fresh.App.CreateClient();
        Assert.False((await client.SendAsync(Get("/bff/nothing/here", added))).Headers.Contains("Access-Control-Allow-Origin"));

        WriteCors(fresh.App.ConfigDir, added);
        await Eventually(async () => (await client.SendAsync(Get("/bff/nothing/here", added))).Headers.Contains("Access-Control-Allow-Origin"));

        WriteCors(fresh.App.ConfigDir);
        await Eventually(async () => !(await client.SendAsync(Get("/bff/nothing/here", added))).Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task An_admin_entry_outside_the_operations_pattern_is_not_used()
    {
        using var fresh = new CorsGateway();
        WriteCors(fresh.App.ConfigDir, "https://portal.other.test", "https://portal.example.test");
        var client = fresh.App.CreateClient();

        await Eventually(async () => (await client.SendAsync(Get("/bff/nothing/here", "https://portal.example.test"))).Headers.Contains("Access-Control-Allow-Origin"));
        Assert.False((await client.SendAsync(Get("/bff/nothing/here", "https://portal.other.test"))).Headers.Contains("Access-Control-Allow-Origin"));
    }

    // ------------------------------------------------------------------ settings and logging

    [Theory]
    [InlineData("Gateway:Cors:AllowedOrigins:0", "*")]
    [InlineData("Gateway:Cors:AllowedOrigins:0", "https://app.example.test/")]
    [InlineData("Gateway:Cors:AllowedOrigins:0", "http://app.example.test")] // plain http outside Development
    [InlineData("Gateway:Cors:AllowedOriginPatterns:0", "https://*.com")]
    public void Invalid_cors_settings_stop_the_gateway_with_a_plain_message(string key, string value)
    {
        using var bad = new CorsGateway(new() { [key] = value });

        var error = Assert.Throws<InvalidOperationException>(() => bad.App.CreateClient());

        Assert.Contains("Gateway:Cors:", error.Message);
    }

    [Fact]
    public async Task A_rejected_origin_is_logged_once_with_the_origin_only()
    {
        using var fresh = new CorsGateway();
        var client = fresh.App.CreateClient();

        await client.SendAsync(Get("/bff/people/P-SECRET-42?name=Pat", "https://rejected.test"));
        await client.SendAsync(Get("/bff/people/P-SECRET-42?name=Pat", "https://rejected.test"));

        var lines = fresh.App.Services.GetRequiredService<LiveLogBuffer>().Read(0, 1000).Where(e => e.EventId == 2001).ToList();
        var line = Assert.Single(lines);
        Assert.Contains("https://rejected.test", line.Message);
        Assert.DoesNotContain("P-SECRET-42", line.Message);
        Assert.DoesNotContain("Pat", line.Message);
    }

    // ------------------------------------------------------------------ helpers

    private static HttpRequestMessage Preflight(string path, string origin, string method, string headers)
    {
        var request = new HttpRequestMessage(HttpMethod.Options, path);
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", method);
        request.Headers.Add("Access-Control-Request-Headers", headers);
        return request;
    }

    private static HttpRequestMessage Get(string path, string origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Origin", origin);
        return request;
    }

    private static async Task<string> AllowedHeaders(HttpClient client)
    {
        var response = await client.SendAsync(Preflight("/bff/people/P-1", CorsGateway.Allowed, "GET", "x-tenant"));
        return response.Headers.TryGetValues("Access-Control-Allow-Headers", out var values) ? string.Join(",", values) : "";
    }

    private static string Single(HttpResponseMessage response, string header) =>
        string.Join(",", response.Headers.TryGetValues(header, out var values) ? values : throw new Xunit.Sdk.XunitException($"{header} is missing"));

    /// <summary>Credentials are never allowed cross-origin (ADR 0015).</summary>
    internal static void AssertNoCredentials(HttpResponseMessage response) =>
        Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"), "Access-Control-Allow-Credentials must never be sent");

    private static void WriteCors(string dir, params string[] origins) =>
        File.WriteAllText(Path.Combine(dir, "cors.json"),
            new JsonObject { ["origins"] = new JsonArray(origins.Select(o => (JsonNode)new JsonObject { ["origin"] = o }).ToArray()) }.ToJsonString());

    /// <summary>The gateway checks its config every second: wait up to five.</summary>
    private static async Task Eventually(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 50; i++)
        {
            if (await condition()) return;
            await Task.Delay(100);
        }
        Assert.Fail("The change wasn't picked up within five seconds.");
    }

    /// <summary>Every call fails, as a broken database would.</summary>
    private sealed class BrokenStore : IConfigStore
    {
        private static InvalidOperationException Boom() => new("store unavailable");
        public ValueTask<ConfigSnapshot> GetSnapshotAsync(CancellationToken ct = default) => throw Boom();
        public Task SaveServiceAsync(ApiService service, CancellationToken ct = default) => throw Boom();
        public Task SaveAuthProfileAsync(AuthProfile profile, CancellationToken ct = default) => throw Boom();
        public Task<IReadOnlyList<Recipe>> GetDraftsAsync(CancellationToken ct = default) => throw Boom();
        public Task<Recipe?> GetDraftAsync(string id, CancellationToken ct = default) => throw Boom();
        public Task SaveDraftAsync(Recipe draft, CancellationToken ct = default) => throw Boom();
        public Task<bool> DeleteServiceAsync(string id, CancellationToken ct = default) => throw Boom();
        public Task<bool> DeleteDraftAsync(string id, CancellationToken ct = default) => throw Boom();
        public Task PublishAsync(Recipe recipe, ChangeInfo change, CancellationToken ct = default) => throw Boom();
        public Task<bool> UnpublishAsync(string id, ChangeInfo change, CancellationToken ct = default) => throw Boom();
        public Task<IReadOnlyList<string>> GetHistoryIdsAsync(CancellationToken ct = default) => throw Boom();
        public Task<IReadOnlyList<RecipeHistoryEntry>> GetHistoryAsync(string id, CancellationToken ct = default) => throw Boom();
        public Task SaveBrowserOriginsAsync(IReadOnlyList<ApiAggregator.Engine.BrowserAccess.BrowserOrigin> origins, ApiAggregator.Engine.BrowserAccess.BrowserAccessHistoryEntry change, CancellationToken ct = default) => throw Boom();
        public Task<IReadOnlyList<ApiAggregator.Engine.BrowserAccess.BrowserAccessHistoryEntry>> GetBrowserAccessHistoryAsync(CancellationToken ct = default) => throw Boom();
    }
}
