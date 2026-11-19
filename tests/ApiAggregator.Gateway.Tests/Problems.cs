using System.Text.Json.Nodes;

namespace ApiAggregator.Gateway.Tests;

/// <summary>Shared checks for the gateway's response contract.</summary>
internal static class Problems
{
    /// <summary>Every response, success or error, carries these.</summary>
    public static void AssertStandardHeaders(HttpResponseMessage response)
    {
        Assert.True(response.Headers.TryGetValues("X-Correlation-Id", out var ids), "X-Correlation-Id is missing");
        Assert.False(string.IsNullOrWhiteSpace(Assert.Single(ids)));
        Assert.True(response.Headers.CacheControl?.NoStore, "Cache-Control: no-store is missing");
    }

    /// <summary>Checks the problem-details shape and code, and returns the body for more checks.</summary>
    public static async Task<JsonObject> AssertProblem(HttpResponseMessage response, int status, string code)
    {
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        AssertStandardHeaders(response);

        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        Assert.Equal(code, body["code"]?.GetValue<string>());
        Assert.Equal($"urn:api-aggregator:error:{code}", body["type"]?.GetValue<string>());
        Assert.Equal(status, body["status"]?.GetValue<int>());
        Assert.Equal(response.Headers.GetValues("X-Correlation-Id").Single(), body["correlationId"]?.GetValue<string>());
        return body;
    }
}
