using System.Text.Json.Nodes;

namespace ApiAggregator.Engine.Model;

/// <summary>A downstream API (Service A, B, C...). Registered once; owns many endpoints.</summary>
public sealed class ApiService
{
    public required string Id { get; init; }
    public string Name { get; init; } = "";

    /// <summary>Base URL, e.g. https://service-a.internal/api. Must be http(s).</summary>
    public required string BaseUrl { get; init; }

    /// <summary>Auth profile used by every endpoint of this service unless overridden.</summary>
    public string? AuthProfileId { get; init; }

    public int TimeoutMs { get; init; } = 5_000;

    /// <summary>Where the endpoints came from: "manual" or the OpenAPI spec URL.</summary>
    public string? OpenApiSource { get; init; }

    public List<ApiEndpoint> Endpoints { get; init; } = [];
}

public sealed class ApiEndpoint
{
    /// <summary>operationId, e.g. "getCustomer". Unique within the service.</summary>
    public required string Id { get; init; }
    public string? Summary { get; init; }
    public string Method { get; init; } = "GET";

    /// <summary>Path template relative to the service base URL, e.g. "/customers/{customerId}".</summary>
    public required string Path { get; init; }

    public List<ParamDef> Parameters { get; init; } = [];

    /// <summary>Overrides the service's auth profile for this endpoint only.</summary>
    public string? AuthProfileId { get; init; }

    public JsonNode? RequestSchema { get; init; }
    public JsonNode? ResponseSchema { get; init; }

    /// <summary>"manual" or "openapi".</summary>
    public string Source { get; init; } = "manual";
}

public sealed class ParamDef
{
    public required string Name { get; init; }

    /// <summary>path | query | header | cookie</summary>
    public string In { get; init; } = "query";
    public bool Required { get; init; }
    public string? Type { get; init; }
}

/// <summary>
/// How the gateway authenticates to a downstream service. Steps run in order, so any
/// combination works, e.g. [ApiKey, SessionLogin] or [Basic] or [OAuthClientCredentials].
/// </summary>
public sealed class AuthProfile
{
    public required string Id { get; init; }
    public string Name { get; init; } = "";
    public List<AuthStepDef> Steps { get; init; } = [];
}

public sealed class AuthStepDef
{
    /// <summary>Basic | ApiKey | StaticBearer | PassThroughUserToken | OAuthClientCredentials | SessionLogin</summary>
    public required string Kind { get; init; }

    /// <summary>
    /// Settings for the step. Values starting with "secret:" are resolved through
    /// ISecretResolver at run time and are never stored in plain text.
    /// </summary>
    public Dictionary<string, string> Settings { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}
