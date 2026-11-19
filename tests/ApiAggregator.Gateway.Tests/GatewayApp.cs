using System.Net;
using System.Net.Http.Headers;
using System.Text;
using ApiAggregator.Engine.Execution;
using ApiAggregator.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace ApiAggregator.Gateway.Tests;

/// <summary>
/// The real gateway (Program.cs) in memory, running as Production with its own config folder:
///   GET /bff/people/{id}   needs a token with "people.read"; calls people.test (stubbed, no sign-in)
///   GET /bff/locked/{id}   anonymous; its service signs in with an API key whose secret has no value
/// Downstream HTTP never leaves the process.
/// </summary>
public sealed class GatewayApp : WebApplicationFactory<Program>
{
    public const string PersonJson = """{ "id": "P-1", "name": "Pat" }""";

    private readonly string _configDir = Path.Combine(Path.GetTempPath(), "aggregator-gateway-tests-" + Guid.NewGuid().ToString("n"));

    public GatewayApp() => WriteConfig(_configDir);

    /// <summary>Extra configuration, e.g. a lower rate limit.</summary>
    public Dictionary<string, string> Settings { get; init; } = new();

    /// <summary>Services to swap in after Program.cs registered its own (e.g. a broken config store).</summary>
    public Action<IServiceCollection>? OverrideServices { get; init; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // UseSetting (not ConfigureAppConfiguration) so Program.cs sees the values before Build().
        builder.UseEnvironment("Production");
        builder.UseSetting("Aggregator:Config:Directory", _configDir);
        builder.UseSetting("Logging:LogLevel:Default", "Warning");
        foreach (var (key, value) in Settings) builder.UseSetting(key, value);

        builder.ConfigureTestServices(services =>
        {
            TestAuth.AcceptTestTokens(services);
            services.AddHttpClient(EngineHttpClients.Downstream).ConfigurePrimaryHttpMessageHandler(() => new StubDownstream());
            OverrideServices?.Invoke(services);
        });
    }

    public HttpClient ClientWithToken(string scope)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestAuth.Token(scope));
        return client;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { Directory.Delete(_configDir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private static void WriteConfig(string dir)
    {
        Directory.CreateDirectory(Path.Combine(dir, "recipes"));
        File.WriteAllText(Path.Combine(dir, "services.json"), """
            [
              { "id": "people", "baseUrl": "http://people.test/api", "endpoints": [
                  { "id": "getPerson", "method": "GET", "path": "/people/{personId}",
                    "parameters": [ { "name": "personId", "in": "path", "required": true, "type": "string" } ] } ] },
              { "id": "locked", "baseUrl": "http://locked.test/api", "authProfileId": "locked-key", "endpoints": [
                  { "id": "getThing", "method": "GET", "path": "/things/{thingId}",
                    "parameters": [ { "name": "thingId", "in": "path", "required": true, "type": "string" } ] } ] }
            ]
            """);
        File.WriteAllText(Path.Combine(dir, "auth-profiles.json"), """
            [ { "id": "locked-key", "steps": [ { "kind": "ApiKey", "settings": { "name": "X-Api-Key", "value": "secret:locked-api-key" } } ] } ]
            """);
        File.WriteAllText(Path.Combine(dir, "recipes", "people.json"), """
            {
              "id": "people", "name": "Person", "status": "published", "method": "GET", "route": "people/{id}",
              "requireAuthentication": true, "requiredScope": "people.read",
              "inputs": { "route.id": { "type": "string", "required": true, "pattern": "^[A-Za-z0-9-]{1,20}$" } },
              "steps": [ { "alias": "p", "endpoint": "people/getPerson", "bind": [ { "from": "request.route.id", "to": "path.personId" } ] } ],
              "response": { "id": "{{p.body.id}}", "name": "{{p.body.name}}" }
            }
            """);
        File.WriteAllText(Path.Combine(dir, "recipes", "locked.json"), """
            {
              "id": "locked", "name": "Locked thing", "status": "published", "method": "GET", "route": "locked/{id}",
              "requireAuthentication": false,
              "inputs": { "route.id": { "type": "string", "required": true, "pattern": "^[A-Za-z0-9-]{1,20}$" } },
              "steps": [ { "alias": "t", "endpoint": "locked/getThing", "bind": [ { "from": "request.route.id", "to": "path.thingId" } ] } ],
              "response": { "thing": "{{t.body}}" }
            }
            """);
    }

    /// <summary>Answers people.test/api/people/P-1; everything else is 404.</summary>
    private sealed class StubDownstream : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(request.RequestUri is { Host: "people.test", AbsolutePath: "/api/people/P-1" }
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(PersonJson, Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
