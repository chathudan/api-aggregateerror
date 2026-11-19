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

    /// <summary>
    /// Named lists built by merging records from several steps by key (see <see cref="CombineDef"/>).
    /// Each one is computed after all steps finish and is used in the response as {{combine.&lt;name&gt;}}.
    /// </summary>
    public Dictionary<string, CombineDef> Combine { get; init; } = new(StringComparer.OrdinalIgnoreCase);

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

    /// <summary>
    /// Only used with ForEach. When true (the default), repeated items (same JSON text) are removed before
    /// the calls are made, so each unique value is called once. This avoids duplicate downstream calls
    /// (cost, rate limits, load). The step output has one entry per UNIQUE item, in first-seen order.
    /// Set false only when the same call must really be repeated.
    /// </summary>
    public bool Distinct { get; init; } = true;

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

/// <summary>
/// Merges records from several steps into one list, matching records by a key (e.g. the same user in
/// the CRM and in billing). Computed after all steps finish; never used by steps.
/// </summary>
public sealed class CombineDef
{
    /// <summary>The lists to merge, in priority order: the first source wins a conflict (with OnConflict "priority").</summary>
    public List<CombineSource> Sources { get; init; } = [];

    /// <summary>Applied to key values before comparing them. Allowed: "trim", "lower".</summary>
    public List<string> Normalize { get; init; } = ["trim"];

    /// <summary>What happens when two sources have different values for the same field: "priority" or "newest:&lt;field&gt;", e.g. "newest:updatedAt".</summary>
    public string OnConflict { get; init; } = "priority";

    /// <summary>Field name -> source name. That source's value always wins for that field.</summary>
    public Dictionary<string, string> Owners { get; init; } = new(StringComparer.Ordinal);

    /// <summary>"neverOverwrite" (a null never replaces a value) or "overwrite".</summary>
    public string Nulls { get; init; } = "neverOverwrite";

    /// <summary>What to do with records that have no key: "keep" (as their own record) or "drop".</summary>
    public string WithoutKey { get; init; } = "keep";

    /// <summary>Adds "_sources": ["crm","billing"] to each merged record, so callers can see where it came from.</summary>
    public bool TrackSources { get; init; }

    /// <summary>Field to sort by; start with "-" for descending. Default: first-seen order.</summary>
    public string? SortBy { get; init; }

    /// <summary>Cap on input records across all sources. Protects gateway memory.</summary>
    public int MaxRecords { get; init; } = 5000;

    /// <summary>If the list can't be produced (e.g. over MaxRecords): true = the call fails (502); false = partial answer with "_errors".</summary>
    public bool Required { get; init; } = true;
}

/// <summary>One list that feeds a combine.</summary>
public sealed class CombineSource
{
    /// <summary>Path (no braces) to an array, or to a single object (treated as a list of one), e.g. "crm.body.contacts".</summary>
    public required string From { get; init; }

    /// <summary>Field that identifies a record: "userId", a dotted path ("customer.id"), or several separated by commas ("tenantId,id").</summary>
    public required string Key { get; init; }

    /// <summary>Label used in Owners and "_sources". Defaults to the step name at the start of From.</summary>
    public string? Name { get; init; }

    /// <summary>
    /// Use when the services do NOT share an id. A "mapping" list (returned by some step) says which id in
    /// this source corresponds to which shared id. Without it, Key is compared directly.
    /// </summary>
    public CombineVia? Via { get; init; }
}

/// <summary>
/// A mapping list that links one service's ids to a shared id, e.g. rows like { "crmId": "7", "billingId": "B-9" }.
/// A source's Key is looked up in the Match field; the Use field then gives the id that is compared with the other lists.
/// </summary>
public sealed class CombineVia
{
    /// <summary>Path (no braces) to the mapping rows, e.g. "links.body.items". A single object counts as one row.</summary>
    public required string From { get; init; }

    /// <summary>Field in a mapping row whose value equals this source's key. Several fields (comma-separated) for a composite key.</summary>
    public required string Match { get; init; }

    /// <summary>Field in the same row that holds the shared id to compare on. Same number of fields as Match.</summary>
    public required string Use { get; init; }
}
