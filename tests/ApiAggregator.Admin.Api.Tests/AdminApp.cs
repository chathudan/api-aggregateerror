using System.Net;
using System.Net.Http.Headers;
using System.Text;
using ApiAggregator.Engine.Execution;
using ApiAggregator.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace ApiAggregator.Admin.Api.Tests;

/// <summary>
/// The real admin API (Program.cs) in memory, running as Production with an empty config folder of its own
/// and two stubbed services: people.test (a CRM list) and billing.test (a billing list).
/// </summary>
public sealed class AdminApp : WebApplicationFactory<Program>
{
    public const string CrmJson = """{ "items": [ { "id": "U-1", "name": "Ann", "email": "ann@example.test" }, { "id": "U-2", "name": "Bo" } ] }""";
    public const string BillingJson = """{ "items": [ { "id": "U-1", "balance": 10 }, { "id": "U-2", "balance": 0, "name": "Bo B." } ] }""";

    public string ConfigDir { get; } = Path.Combine(Path.GetTempPath(), "aggregator-admin-tests-" + Guid.NewGuid().ToString("n"));

    /// <summary>Extra configuration, e.g. a dev signing key.</summary>
    public Dictionary<string, string> Settings { get; init; } = new();

    public AdminApp()
    {
        Directory.CreateDirectory(Path.Combine(ConfigDir, "recipes"));
        Directory.CreateDirectory(Path.Combine(ConfigDir, "drafts"));
        File.WriteAllText(Path.Combine(ConfigDir, "auth-profiles.json"), "[]");
        File.WriteAllText(Path.Combine(ConfigDir, "services.json"), """
            [
              { "id": "people", "baseUrl": "http://people.test/api", "endpoints": [ { "id": "listPeople", "method": "GET", "path": "/people" } ] },
              { "id": "billing", "baseUrl": "http://billing.test/api", "endpoints": [ { "id": "listAccounts", "method": "GET", "path": "/accounts" } ] }
            ]
            """);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // UseSetting (not ConfigureAppConfiguration) so Program.cs sees the values before Build().
        builder.UseEnvironment("Production");
        builder.UseSetting("Aggregator:Config:Directory", ConfigDir);
        builder.UseSetting("Logging:LogLevel:Default", "Warning");
        foreach (var (key, value) in Settings) builder.UseSetting(key, value);

        builder.ConfigureTestServices(services =>
        {
            TestAuth.AcceptTestTokens(services);
            services.AddHttpClient(EngineHttpClients.Downstream).ConfigurePrimaryHttpMessageHandler(() => new StubDownstream());
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
        try { Directory.Delete(ConfigDir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private sealed class StubDownstream : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var json = request.RequestUri switch
            {
                { Host: "people.test", AbsolutePath: "/api/people" } => CrmJson,
                { Host: "billing.test", AbsolutePath: "/api/accounts" } => BillingJson,
                _ => null
            };
            return Task.FromResult(json is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }
}
