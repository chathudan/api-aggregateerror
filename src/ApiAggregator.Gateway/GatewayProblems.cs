using System.Text.Json.Nodes;

namespace ApiAggregator.Gateway;

/// <summary>Writes error bodies (RFC 7807 problem details) with the right content type.</summary>
internal static class GatewayProblems
{
    public const string ContentType = "application/problem+json";

    /// <summary>For endpoint handlers. The status comes from the body unless given.</summary>
    public static IResult Result(JsonNode? body, int? status = null) =>
        Results.Json(body, contentType: ContentType, statusCode: status ?? body?["status"]?.GetValue<int>());

    /// <summary>For middleware (e.g. the rate limiter), which can't return an IResult.</summary>
    public static Task WriteAsync(HttpContext http, JsonNode body, CancellationToken ct)
    {
        http.Response.ContentType = ContentType;
        return http.Response.WriteAsync(body.ToJsonString(), ct);
    }
}
