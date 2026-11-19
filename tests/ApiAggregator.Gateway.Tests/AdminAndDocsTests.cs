using System.Net;
using System.Text.Json.Nodes;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Readers;

namespace ApiAggregator.Gateway.Tests;

/// <summary>The gateway's non-recipe endpoints: live logs, the OpenAPI document, the docs page, startup checks.</summary>
public class AdminAndDocsTests(GatewayApp app) : IClassFixture<GatewayApp>
{
    // ---------------------------------------------------------------- /_admin/logs

    [Fact]
    public async Task Live_logs_need_a_token()
    {
        var response = await app.CreateClient().GetAsync("/_admin/logs");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Live_logs_refuse_a_token_without_an_aggregator_role()
    {
        var response = await app.ClientWithToken("people.read").GetAsync("/_admin/logs");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Problems.AssertStandardHeaders(response);
    }

    [Theory]
    [InlineData("aggregator.author")]
    [InlineData("aggregator.publisher")]
    public async Task Live_logs_are_readable_with_an_aggregator_role(string scope)
    {
        var response = await app.ClientWithToken(scope).GetAsync("/_admin/logs");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Problems.AssertStandardHeaders(response);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal("Gateway", body["source"]!.GetValue<string>());
        Assert.NotNull(body["entries"]);
    }

    // ---------------------------------------------------------------- /bff/openapi.json and /bff/docs

    [Fact]
    public async Task The_openapi_document_is_valid_openapi_3_and_lists_the_published_endpoints()
    {
        var response = await app.CreateClient().GetAsync("/bff/openapi.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var document = new OpenApiStringReader().Read(await response.Content.ReadAsStringAsync(), out var diagnostic);

        Assert.Empty(diagnostic.Errors);
        Assert.Equal(OpenApiSpecVersion.OpenApi3_0, diagnostic.SpecificationVersion);
        Assert.Contains("/bff/people/{id}", document.Paths.Keys);
        Assert.Contains("/bff/locked/{id}", document.Paths.Keys);
    }

    [Fact]
    public async Task The_openapi_document_hides_internal_details_outside_development()
    {
        var json = await app.CreateClient().GetStringAsync("/bff/openapi.json");

        Assert.DoesNotContain("people.test", json);   // downstream addresses
        Assert.DoesNotContain("locked-key", json);    // auth profile ids
        Assert.DoesNotContain("getPerson", json);     // downstream endpoint ids
    }

    [Fact]
    public async Task The_docs_page_is_off_outside_development()
    {
        var response = await app.CreateClient().GetAsync("/bff/docs");

        await Problems.AssertProblem(response, 404, "not_found");
    }

    [Fact]
    public async Task The_dev_token_issuer_is_off_outside_development()
    {
        var response = await app.CreateClient().GetAsync("/dev/token");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---------------------------------------------------------------- startup

    [Fact]
    public void A_dev_signing_key_outside_development_stops_the_gateway_from_starting()
    {
        using var misconfigured = new GatewayApp { Settings = { ["Auth:DevSigningKey"] = "dev-only-signing-key-change-me-0123456789abcdef" } };

        var error = Assert.ThrowsAny<Exception>(() => misconfigured.CreateClient());
        Assert.Contains("Auth:DevSigningKey must not be set outside Development", error.ToString());
    }
}
