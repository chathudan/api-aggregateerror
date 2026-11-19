namespace ApiAggregator.Gateway.Tests;

/// <summary>Correlation id and no-store on every response (429 and 500 are checked in ErrorContractTests).</summary>
public class ResponseHeaderTests(GatewayApp app) : IClassFixture<GatewayApp>
{
    [Theory]
    [InlineData("/bff/people/P-1", "people.read", 200)]   // success
    [InlineData("/bff/people/P-1", null, 401)]            // gateway's own 401
    [InlineData("/bff/people/P-1", "orders.read", 403)]
    [InlineData("/bff/nothing/here", null, 404)]
    [InlineData("/bff/locked/T-1", null, 502)]
    [InlineData("/bff/people/not%20valid!", "people.read", 400)] // input fails its pattern
    [InlineData("/_admin/logs", null, 401)]               // challenge written by the authentication handler
    [InlineData("/bff/openapi.json", null, 200)]
    [InlineData("/health/live", null, 200)]
    public async Task Every_response_has_a_correlation_id_and_no_store(string path, string? scope, int status)
    {
        var client = scope is null ? app.CreateClient() : app.ClientWithToken(scope);

        var response = await client.GetAsync(path);

        Assert.Equal(status, (int)response.StatusCode);
        Problems.AssertStandardHeaders(response);
    }

    [Fact]
    public async Task A_safe_incoming_correlation_id_is_echoed_back()
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("X-Correlation-Id", "abc-123_XYZ");

        var response = await client.GetAsync("/bff/nothing/here");

        Assert.Equal("abc-123_XYZ", response.Headers.GetValues("X-Correlation-Id").Single());
    }

    [Theory]
    [InlineData("has spaces")]
    [InlineData("semi;colon")]
    [InlineData("a-very-long-id-0123456789-0123456789-0123456789-0123456789-0123456789")]
    public async Task An_unsafe_incoming_correlation_id_is_replaced(string incoming)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-Correlation-Id", incoming);

        var response = await client.GetAsync("/bff/nothing/here");

        var id = response.Headers.GetValues("X-Correlation-Id").Single();
        Assert.NotEqual(incoming, id);
        Assert.Matches("^[0-9a-f]{32}$", id);
    }
}
