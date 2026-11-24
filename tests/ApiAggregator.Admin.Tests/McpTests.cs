using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ApiAggregator.Admin.Application;
using ApiAggregator.Admin.Mcp;
using ApiAggregator.Engine.Auth;
using ApiAggregator.Engine.Config;
using ApiAggregator.Engine.Logging.Live;
using ApiAggregator.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace ApiAggregator.Admin.Tests;

/// <summary>
/// The MCP server for AI agents (ADR 0021), called the way an agent calls it: an MCP client over HTTP with a bearer
/// token. Drafts only; no values from real services; no secrets; no publishing.
/// </summary>
public sealed class McpTests(AdminApp app) : IClassFixture<AdminApp>
{
    private const string AgentScopes = "aggregator.author aggregator.mcp";

    private async Task<McpClient> Agent(string scope = AgentScopes, AdminApp? on = null)
    {
        var http = (on ?? app).CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestAuth.Token(scope, subject: "agent-user"));
        var transport = new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri(http.BaseAddress!, "mcp") }, http);
        return await McpClient.CreateAsync(transport, new McpClientOptions { ClientInfo = new Implementation { Name = "Test Agent", Version = "1.0" } });
    }

    private static async Task<CallToolResult> Call(McpClient agent, string tool, object? arguments = null)
    {
        var args = arguments is null
            ? null
            : JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(arguments));
        return await agent.CallToolAsync(tool, args);
    }

    private static string Text(CallToolResult result) => string.Concat(result.Content.OfType<TextContentBlock>().Select(t => t.Text));

    private static object PeopleEndpoint(string id, string name = "Everyone") => new
    {
        id, name, route = $"agents/{id}", requireAuthentication = false,
        steps = new[] { new { alias = "crm", endpoint = "people/listPeople" } },
        response = new { people = "{{crm.body.items}}" }
    };

    private static HttpRequestMessage Initialize(string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent("""
                {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"raw","version":"1"}}}
                """, Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    // ---------------------------------------------------------------- who may connect

    [Fact]
    public async Task Without_a_token_mcp_answers_401_and_without_the_mcp_scope_403()
    {
        var client = app.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Initialize(null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Initialize(TestAuth.Token("aggregator.author aggregator.publisher")))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Initialize(TestAuth.Token(AgentScopes)))).StatusCode);
    }

    [Fact]
    public async Task A_request_larger_than_the_mcp_limit_is_refused()
    {
        var request = Initialize(TestAuth.Token(AgentScopes));
        request.Content = new StringContent("{\"padding\":\"" + new string('x', (int)McpHosting.MaxRequestBytes) + "\"}", Encoding.UTF8, "application/json");

        var response = await app.CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task Too_many_requests_from_one_person_get_429()
    {
        using var limited = new AdminApp { Settings = { ["Mcp:RequestsPerMinute"] = "2" } };
        var client = limited.CreateClient();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++) statuses.Add((await client.SendAsync(Initialize(TestAuth.Token(AgentScopes)))).StatusCode);

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests], statuses);
    }

    // ---------------------------------------------------------------- what an agent can and can't do

    [Fact]
    public async Task There_is_no_tool_to_publish_roll_back_delete_discard_or_touch_secrets_or_browser_access()
    {
        await using var agent = await Agent();

        var tools = (await agent.ListToolsAsync()).Select(t => t.Name).ToList();

        Assert.Contains("save_endpoint_draft", tools);
        Assert.Contains("try_endpoint_draft", tools);
        foreach (var forbidden in new[] { "publish", "rollback", "roll_back", "delete", "discard", "secret", "cors", "origin", "unpublish" })
            Assert.DoesNotContain(tools, t => t.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task An_agent_saves_a_draft_that_is_not_live_and_the_activity_log_says_it_came_via_mcp()
    {
        await using var agent = await Agent();

        var result = await Call(agent, "save_endpoint_draft", new { recipe = PeopleEndpoint("agent-everyone") });

        Assert.True(result.IsError != true, Text(result));
        var store = app.Services.GetRequiredService<IConfigStore>();
        Assert.NotNull(await store.GetDraftAsync("agent-everyone"));
        Assert.Null((await store.GetSnapshotAsync()).GetRecipe("agent-everyone"));
        var entry = Assert.Single(await store.GetActivityAsync(ActivityEntry.Endpoint, "agent-everyone", 5));
        Assert.Equal("agent-user via MCP (Test Agent)", entry.Source.Describe());
    }

    [Fact]
    public async Task A_token_with_the_mcp_scope_but_no_author_role_can_not_save()
    {
        await using var agent = await Agent("aggregator.mcp");

        var result = await Call(agent, "save_endpoint_draft", new { recipe = PeopleEndpoint("not-an-author") });

        Assert.True(result.IsError);
        Assert.Contains("You need the author or publisher role to do this.", Text(result));
    }

    [Fact]
    public async Task A_password_or_key_in_a_sign_in_is_refused_and_a_missing_secret_is_left_to_a_person()
    {
        await using var agent = await Agent();

        var raw = await Call(agent, "save_sign_in", new
        {
            id = "agent-billing",
            steps = new[] { new { kind = "ApiKey", settings = new Dictionary<string, string> { ["name"] = "X-Api-Key", ["value"] = "sk_live_51HxYz9Q2w8E4r6T" } } }
        });
        Assert.True(raw.IsError);
        Assert.Contains("Ask a person to set secret 'agent-billing-value' in Services", Text(raw));
        Assert.DoesNotContain("sk_live_51HxYz9Q2w8E4r6T", JsonSerializer.Serialize(await app.Services.GetRequiredService<IConfigStore>().GetSnapshotAsync()));

        var reference = await Call(agent, "save_sign_in", new
        {
            id = "agent-billing",
            steps = new[] { new { kind = "ApiKey", settings = new Dictionary<string, string> { ["name"] = "X-Api-Key", ["value"] = "secret:agent-billing-key" } } }
        });
        Assert.True(reference.IsError != true, Text(reference));
        Assert.Contains("Ask a person to set secret 'agent-billing-key' in Services.", Text(reference));
    }

    [Fact]
    public async Task Outside_the_sample_data_a_try_shows_field_names_and_types_but_no_values_and_does_not_count_for_publishing()
    {
        await using var agent = await Agent();
        await Call(agent, "save_endpoint_draft", new { recipe = PeopleEndpoint("agent-tried") });

        var tried = await Call(agent, "try_endpoint_draft", new { id = "agent-tried" });

        var text = Text(tried);
        Assert.True(tried.IsError != true, text);
        var outcome = JsonDocument.Parse(text).RootElement;
        Assert.True(outcome.GetProperty("passed").GetBoolean());
        Assert.DoesNotContain("ann@example.test", text); // a value the stubbed CRM answers with
        Assert.DoesNotContain("Ann", text);
        Assert.Contains("\"email\"", text);              // ...but its field names and types are there
        Assert.Contains("Values are hidden", outcome.GetProperty("valuesHidden").GetString());
        Assert.False(outcome.TryGetProperty("body", out _));

        // A person still has to test it before it can be published.
        var publisher = new AdminActor("pub", "Pat", IsAuthor: true, IsPublisher: true);
        var publish = await app.Services.GetRequiredService<AdminService>().PublishAsync(publisher, "agent-tried");
        Assert.Equal("Run a successful test of this exact draft before publishing.", publish.Failure?.Message);
    }

    [Fact]
    public async Task A_spec_is_given_as_text_and_an_address_only_on_a_service_host()
    {
        await using var agent = await Agent();
        var spec = """
            { "openapi": "3.0.1", "info": { "title": "Agent billing", "version": "1" },
              "paths": { "/invoices": { "get": { "operationId": "listInvoices", "responses": { "200": { "description": "OK" } } } } } }
            """;

        var fromText = await Call(agent, "import_openapi_service", new { service_id = "agent-spec", spec_text = spec, name = "Agent billing", base_url = "http://billing.test/api" });
        Assert.True(fromText.IsError != true, Text(fromText));
        Assert.Contains("listInvoices", Text(fromText));

        var elsewhere = await Call(agent, "import_openapi_service", new { service_id = "agent-spec", spec_url = "http://169.254.169.254/latest/meta-data" });
        Assert.True(elsewhere.IsError);
        Assert.Contains("Over MCP, give the spec as text. A spec address is allowed only on the host of a service a person has set up.", Text(elsewhere));

        // Not even after adding a service on that address itself.
        await Call(agent, "add_service", new { id = "agent-metadata", name = "Metadata", base_url = "http://169.254.169.254/latest" });
        var viaOwnService = await Call(agent, "import_openapi_service", new { service_id = "agent-metadata", spec_url = "http://169.254.169.254/latest/meta-data" });
        Assert.True(viaOwnService.IsError);
        Assert.Contains("a service a person has set up", Text(viaOwnService));

        // On a service's own host the address is allowed (this one doesn't answer, so reading it fails for that reason).
        var onServiceHost = await Call(agent, "import_openapi_service", new { service_id = "agent-spec", spec_url = "http://billing.test/openapi.json" });
        Assert.DoesNotContain("Over MCP", Text(onServiceHost));
    }

    [Fact]
    public async Task Endpoint_json_with_problems_comes_back_with_where_and_what_to_fix()
    {
        await using var agent = await Agent();

        var checkedJson = await Call(agent, "validate_recipe", new { recipe = new { id = "agent-broken", route = "agents/{id}", steps = new[] { new { alias = "x", endpoint = "people/nope" } } } });

        var result = JsonDocument.Parse(Text(checkedJson)).RootElement;
        Assert.False(result.GetProperty("valid").GetBoolean());
        Assert.Contains(result.GetProperty("issues").EnumerateArray(), i => i.GetProperty("where").GetString() == "inputs");
        Assert.Null(await app.Services.GetRequiredService<IConfigStore>().GetDraftAsync("agent-broken"));
    }

    [Fact]
    public async Task Tool_calls_are_logged_by_name_without_their_arguments_or_results()
    {
        using var logged = new AdminApp { Settings = { ["Logging:LogLevel:Default"] = "Information" } };
        await using var agent = await Agent(on: logged);

        await Call(agent, "save_endpoint_draft", new { recipe = PeopleEndpoint("agent-logged", name: "MARKER-ARGUMENT-NAME") });
        await Call(agent, "try_endpoint_draft", new { id = "agent-logged" });

        var lines = logged.Services.GetRequiredService<LiveLogBuffer>().Read(0).Select(e => JsonSerializer.Serialize(e)).ToList();
        Assert.Contains(lines, l => l.Contains("MCP tool save_endpoint_draft by agent-user via Test Agent: ok"));
        Assert.DoesNotContain(lines, l => l.Contains("MARKER-ARGUMENT-NAME") || l.Contains("ann@example.test"));
    }
}
