using System.Text.Json.Nodes;

namespace ApiAggregator.Engine.Model;

/// <summary>
/// A recipe defines ONE aggregated endpoint, e.g. GET /bff/customer/{id}/overview.
/// The gateway hosts many recipes; each is versioned and published independently.
/// </summary>
public sealed class Recipe
{
    /// <summary>Stable slug, e.g. "customer-overview". Also the file name in config/recipes.</summary>
    public required string Id { get; init; }

    public string Name { get; init; } = "";
    public int Version { get; init; } = 1;
    public RecipeStatus Status { get; init; } = RecipeStatus.Draft;

    public string Method { get; init; } = "GET";

    /// <summary>Route relative to /bff, e.g. "customer/{id}/overview".</summary>
    public required string Route { get; init; }

    /// <summary>When true (default) the caller must present a valid JWT.</summary>
    public bool RequireAuthentication { get; init; } = true;

    /// <summary>Optional OAuth scope the caller's token must contain.</summary>
    public string? RequiredScope { get; init; }

    /// <summary>
    /// Declared caller inputs. Key = "route.id", "query.status" or "header.X-Tenant".
    /// Anything not declared here is ignored and never forwarded downstream.
    /// </summary>
    public Dictionary<string, InputDef> Inputs { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public List<StepDef> Steps { get; init; } = [];

    /// <summary>Response template, e.g. { "customer": "{{a.body}}" }.</summary>
    public JsonNode? Response { get; init; }

    /// <summary>Whole-endpoint response cache. Off by default.</summary>
    public CacheSettings Cache { get; init; } = new();

    /// <summary>Hard limit for the whole composition.</summary>
    public int TimeoutMs { get; init; } = 10_000;
}

public enum RecipeStatus { Draft, Published, Retired }

public sealed class InputDef
{
    /// <summary>string | int | number | bool | date</summary>
    public string Type { get; init; } = "string";
    public bool Required { get; init; }
    public string? Pattern { get; init; }
    public string[]? Enum { get; init; }
    public double? Min { get; init; }
    public double? Max { get; init; }
    public int MaxLength { get; init; } = 200;
    public string? Default { get; init; }
}

public sealed class StepDef
{
    /// <summary>Short name used in expressions, e.g. "a" or "customer". Letters, digits, underscore.</summary>
    public required string Alias { get; init; }

    /// <summary>Friendly name shown in the admin UI, e.g. "Get customer".</summary>
    public string? DisplayName { get; init; }

    /// <summary>Endpoint reference "serviceId/endpointId", e.g. "service-a/getCustomer".</summary>
    public required string Endpoint { get; init; }

    public List<BindingDef> Bind { get; init; } = [];

    /// <summary>If a required step fails the whole call fails (502). Optional failures give a partial response.</summary>
    public bool Required { get; init; } = true;

    /// <summary>Optional condition, e.g. "{{a.body.isPremium}} == true".</summary>
    public string? When { get; init; }

    /// <summary>Optional array expression; the step runs once per item, e.g. "{{b.body.items[0:5].id}}".</summary>
    public string? ForEach { get; init; }
    public int MaxConcurrency { get; init; } = 4;

    public int? TimeoutMs { get; init; }

    /// <summary>Per-step cache of the downstream response. Off by default.</summary>
    public CacheSettings Cache { get; init; } = new();
}

/// <summary>Moves one value from a source to a target on the downstream request.</summary>
public sealed class BindingDef
{
    /// <summary>Source path, e.g. "request.route.id", "a.body.id", "b.headers.ETag", "item". May end with a pipe transform: "a.body.ids | join(',')".</summary>
    public string? From { get; init; }

    /// <summary>JSON template (alternative to From), used mainly for request bodies.</summary>
    public JsonNode? Template { get; init; }

    /// <summary>Target: "path.customerId", "query.status", "header.X-Customer-Id", "cookie.name", "body" or "body.some.field".</summary>
    public required string To { get; init; }

    /// <summary>Used when the source resolves to null.</summary>
    public string? Default { get; init; }
}

public sealed class CacheSettings
{
    public bool Enabled { get; init; }
    public int TtlSeconds { get; init; } = 30;

    /// <summary>
    /// Per-caller by default (safe). Set false ONLY for data that is identical for every caller
    /// (reference data, public catalogues). Never set false for patient or customer data.
    /// </summary>
    public bool PerCaller { get; init; } = true;
}
