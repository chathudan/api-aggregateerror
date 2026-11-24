using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace ApiAggregator.Docs.Tests;

/// <summary>
/// An AI agent builds the "unique contacts and their notes" endpoint (the sample contacts-unique) from nothing, using
/// only the MCP tools, against the real mocks and admin app in sample mode (ADR 0021): import the CRM's spec text, set
/// up its sign-in with a secret reference, write the endpoint, check it until it has no problems, save it as a draft,
/// and try it. A person would then publish it.
/// </summary>
public sealed class McpEndToEndTests(DocsStack stack) : IClassFixture<DocsStack>
{
    [Fact]
    public async Task An_agent_builds_unique_contacts_from_nothing_using_only_the_tools()
    {
        await using var agent = await stack.AgentAsync();

        // 1. The CRM's spec, as the agent would have read it (from a file or the owner): text, not an address.
        using var http = new HttpClient();
        var spec = await http.GetStringAsync(stack.AsRunning("http://localhost:5100/f/openapi.json"));

        // 2. Its sign-in, with a reference to the secret a person has already set (the mocks' Basic password).
        await Ok(agent, "save_sign_in", new
        {
            id = "crm-agent-signin",
            steps = new[] { new { kind = "Basic", settings = new Dictionary<string, string> { ["username"] = "svc-f", ["password"] = "secret:service-f-password" } } }
        });

        // 3. The service and all its endpoints from the spec.
        var imported = await Ok(agent, "import_openapi_service", new
        {
            service_id = "crm-agent", name = "CRM (added by an agent)", base_url = stack.AsRunning("http://localhost:5100/f"),
            sign_in_id = "crm-agent-signin", spec_text = spec
        });
        foreach (var operation in new[] { "listContacts", "listOrders", "getContactNotes" }) Assert.Contains(operation, imported);

        // 4. The endpoint, written from the real fields: first without the notes step, to ask what it can loop over.
        var recipe = Recipe(withNotes: false);
        var lists = await Ok(agent, "list_source_fields", new { recipe, before_step = (string?)null });
        Assert.Contains("orders.body.orders[*].contactId", lists);

        // 5. Checked until it has no problems.
        var broken = JsonDocument.Parse(await Ok(agent, "validate_recipe", new { recipe = Recipe(withNotes: true, endpoint: "crm-agent/noSuchEndpoint") })).RootElement;
        Assert.False(broken.GetProperty("valid").GetBoolean());
        var valid = JsonDocument.Parse(await Ok(agent, "validate_recipe", new { recipe = Recipe(withNotes: true) })).RootElement;
        Assert.True(valid.GetProperty("valid").GetBoolean(), valid.ToString());
        Assert.Empty(valid.GetProperty("issues").EnumerateArray());

        // 6. Saved as a draft (never live), then tried: in sample mode the answer's values are shown.
        await Ok(agent, "save_endpoint_draft", new { recipe = Recipe(withNotes: true) });
        var tried = JsonDocument.Parse(await Ok(agent, "try_endpoint_draft", new { id = "contacts-by-agent" })).RootElement;
        Assert.True(tried.GetProperty("passed").GetBoolean(), tried.ToString());
        Assert.True(tried.GetProperty("body").GetProperty("uniqueContacts").GetInt32() > 0);

        // Not live: the gateway doesn't know it.
        var (status, _, _) = await stack.CallGatewayAsync("GET", "/bff/agent/contacts/unique", "people.read", []);
        Assert.Equal(404, status);
    }

    /// <summary>The same endpoint as the sample contacts-unique, on its own address, over the service the agent added.</summary>
    private static object Recipe(bool withNotes, string endpoint = "crm-agent/getContactNotes")
    {
        var steps = new List<object>
        {
            new { alias = "crm", displayName = "Contacts from CRM", endpoint = "crm-agent/listContacts" },
            new { alias = "orders", displayName = "Orders from CRM", endpoint = "crm-agent/listOrders" }
        };
        if (withNotes)
            steps.Add(new
            {
                alias = "notes", displayName = "Notes for each contact with an order", endpoint,
                forEach = "{{orders.body.orders[*].contactId}}", maxConcurrency = 3,
                bind = new[] { new { from = "item", to = "path.contactId" } }
            });
        return new
        {
            id = "contacts-by-agent", name = "Unique contacts and their notes (by an agent)", route = "agent/contacts/unique",
            requireAuthentication = true, requiredScope = "people.read", steps,
            response = new Dictionary<string, string>
            {
                ["uniqueContacts"] = "{{crm.body.contacts | distinct('id') | count}}",
                ["contacts"] = "{{crm.body.contacts | distinct('id')}}",
                ["notes"] = withNotes ? "{{notes[*].body}}" : "{{orders.body.orders}}"
            }
        };
    }

    private static async Task<string> Ok(McpClient agent, string tool, object arguments)
    {
        var args = JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(arguments));
        var result = await agent.CallToolAsync(tool, args);
        var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(t => t.Text));
        Assert.True(result.IsError != true, $"{tool} was refused: {text}");
        return text;
    }
}
