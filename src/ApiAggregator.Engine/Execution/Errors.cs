using System.Text.Json.Nodes;

namespace ApiAggregator.Engine.Execution;

/// <summary>
/// Stable, machine-readable reasons a downstream call can fail. These strings are part of the
/// public error contract: web and mobile apps switch on them, so never rename one.
/// </summary>
public static class DownstreamErrorCode
{
    /// <summary>The gateway couldn't sign in (login failed, secret missing, caller token missing).</summary>
    public const string AuthFailed = "auth_failed";
    /// <summary>The service answered 401 (after one automatic sign-in retry).</summary>
    public const string Unauthorized = "unauthorized";
    public const string Forbidden = "forbidden";
    public const string NotFound = "not_found";
    /// <summary>Any other 4xx (400, 409, 422...).</summary>
    public const string BadRequest = "bad_request";
    public const string RateLimited = "rate_limited";
    public const string ServerError = "server_error";
    public const string Timeout = "timeout";
    public const string Unreachable = "unreachable";
    public const string InvalidResponse = "invalid_response";
    /// <summary>A value needed for the call was missing or invalid.</summary>
    public const string MissingValue = "missing_value";
    /// <summary>Not run because an upstream step failed.</summary>
    public const string DependencyFailed = "dependency_failed";
    /// <summary>Records from several steps couldn't be merged into one list (e.g. too many records). Not retryable.</summary>
    public const string CombineFailed = "combine_failed";

    /// <summary>Maps a non-success HTTP status from a downstream service to a code.</summary>
    public static string ForStatus(int status) => status switch
    {
        401 => Unauthorized,
        403 => Forbidden,
        404 => NotFound,
        429 => RateLimited,
        >= 500 => ServerError,
        >= 400 => BadRequest,
        _ => InvalidResponse // e.g. a redirect: redirects are never followed, so there is no usable answer
    };

    /// <summary>Is it worth the caller trying again later? Transient problems yes, mistakes and rejections no.</summary>
    public static bool Retryable(string? code) =>
        code is RateLimited or ServerError or Timeout or Unreachable;

    /// <summary>True for the codes that mean "the gateway isn't allowed in", which get a more specific title.</summary>
    public static bool IsAuthProblem(string? code) =>
        code is AuthFailed or Unauthorized or Forbidden;
}

/// <summary>Builds the JSON error bodies (RFC 7807 problem details) used by the engine and the gateway.</summary>
public static class ErrorBody
{
    public const string InvalidInput = "invalid_input";

    /// <summary>
    /// One problem-details object. "code" is the stable machine-readable reason; "type" repeats it as a URN.
    /// Null parts are left out.
    /// </summary>
    public static JsonObject Problem(string code, string title, int status, string correlationId,
        string? detail = null, bool? retryable = null, JsonArray? errors = null)
    {
        var body = new JsonObject
        {
            ["type"] = $"urn:api-aggregator:error:{code}",
            ["title"] = title,
            ["status"] = status,
            ["code"] = code
        };
        if (detail is not null) body["detail"] = detail;
        body["correlationId"] = correlationId;
        if (retryable is not null) body["retryable"] = retryable;
        if (errors is not null) body["errors"] = errors;
        return body;
    }
}
