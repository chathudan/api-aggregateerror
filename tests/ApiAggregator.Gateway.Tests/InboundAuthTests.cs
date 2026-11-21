using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using ApiAggregator.TestSupport;

namespace ApiAggregator.Gateway.Tests;

/// <summary>Callers' tokens: who may call an aggregated endpoint.</summary>
public class InboundAuthTests(GatewayApp app) : IClassFixture<GatewayApp>
{
    [Fact]
    public async Task A_valid_token_with_the_scope_gets_the_aggregated_response()
    {
        var response = await app.ClientWithToken("people.read").GetAsync("/bff/people/P-1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Problems.AssertStandardHeaders(response);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal("P-1", body["id"]!.GetValue<string>());
        Assert.Equal("Pat", body["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task No_token_gives_401_unauthenticated_with_a_bearer_challenge()
    {
        var response = await app.CreateClient().GetAsync("/bff/people/P-1");

        await Problems.AssertProblem(response, 401, "unauthenticated");
        Assert.Equal("Bearer", response.Headers.WwwAuthenticate.Single().Scheme);
    }

    [Fact]
    public async Task A_token_signed_with_another_key_gives_401_unauthenticated()
    {
        var token = TestAuth.Token("people.read", signingKey: "someone-elses-signing-key-0123456789abcdef-xyz");
        var response = await Send(token);

        await Problems.AssertProblem(response, 401, "unauthenticated");
    }

    [Fact]
    public async Task An_expired_token_gives_401_unauthenticated()
    {
        var token = TestAuth.Token("people.read", expires: DateTime.UtcNow.AddMinutes(-5));
        var response = await Send(token);

        await Problems.AssertProblem(response, 401, "unauthenticated");
    }

    [Fact]
    public async Task A_malformed_token_gives_401_unauthenticated()
    {
        var response = await Send("not-a-jwt");

        await Problems.AssertProblem(response, 401, "unauthenticated");
    }

    [Fact]
    public async Task A_valid_token_without_the_scope_gives_403_insufficient_scope_naming_the_scope()
    {
        var response = await app.ClientWithToken("orders.read").GetAsync("/bff/people/P-1");

        var body = await Problems.AssertProblem(response, 403, "insufficient_scope");
        Assert.Equal("people.read", body["requiredScope"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_server_token_with_the_permission_in_roles_gets_the_response()
    {
        // Entra ID gives a server calling with client credentials its permissions in "roles", with no "scp".
        var response = await Send(TestAuth.Token("", subject: "partner-server", roles: ["people.read"]));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_server_token_with_other_roles_gives_403_insufficient_scope()
    {
        var response = await Send(TestAuth.Token("", subject: "partner-server", roles: ["orders.read", "people.write"]));

        var body = await Problems.AssertProblem(response, 403, "insufficient_scope");
        Assert.Equal("people.read", body["requiredScope"]!.GetValue<string>());
    }

    private Task<HttpResponseMessage> Send(string token)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client.GetAsync("/bff/people/P-1");
    }
}
