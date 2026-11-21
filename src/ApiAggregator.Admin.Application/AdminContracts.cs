using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ApiAggregator.Engine.BrowserAccess;
using ApiAggregator.Engine.Config;
using ApiAggregator.Engine.Execution;
using ApiAggregator.Engine.Validation;

namespace ApiAggregator.Admin.Application;

// What the use cases take and return. The /admin/* endpoints serialise these as they are (camelCase), so property
// names and order are the API contract: change them only with care, and add rather than rename.

/// <summary>What the host decides for the service.</summary>
public sealed class AdminOptions
{
    /// <summary>Publishing needs a passing test of exactly the draft (Admin:RequireTestBeforePublish).</summary>
    public bool RequireTestBeforePublish { get; init; } = true;

    /// <summary>Development only: secret values typed in the UI may be saved (secrets.local.json, git-ignored).</summary>
    public bool CanSaveSecrets { get; init; }

    /// <summary>The config folder, to tell whether it's the sample data (it carries SAMPLE-DATA.md).</summary>
    public string ConfigDirectory { get; init; } = "config";

    /// <summary>The browser-access limits operations set (Gateway:Cors), the same values the gateway reads.</summary>
    public BrowserAccessSettings BrowserAccess { get; init; } = new();

    /// <summary>Development allows plain http:// origins (ADR 0015).</summary>
    public bool IsDevelopment { get; init; }
}

/// <summary>Saves a secret value (Development only). The host provides it; the service never writes files itself.</summary>
public interface ISecretWriter
{
    Task SaveAsync(string name, string value, CancellationToken ct);
}

public sealed record SecretValue(string? Value);
public sealed record SecretSaved(string Name, string Reference);
public sealed record SecretExists(string Name, bool Exists);

public sealed record RecipeRef(string Id, string Name);
public sealed record ServiceUsage(string Id, List<RecipeRef> Published, List<RecipeRef> Drafts);
public sealed record ServiceDeleted(string Id, List<RecipeRef> DraftsAffected);

public sealed record AuthKindInfo(string Kind, IReadOnlyList<string> RequiredSettings);

/// <summary>What this person may do on this installation. Pages use it to hide buttons; the service checks anyway.</summary>
public sealed record Capabilities(bool SampleData, bool CanPublish, bool RequireTestBeforePublish, bool CanSaveSecrets, IReadOnlyList<AuthKindInfo> AuthKinds);

public enum SignInState { NotChecked, SignedIn, SettingsOk, Failed }

/// <summary>The last sign-in check of one auth profile. Holds a plain-English message, never a secret.</summary>
public sealed record SignInStatus(string ProfileId, SignInState State, string Message, DateTimeOffset? CheckedAt);

public sealed record SignInCheck(bool Ok, string Message);

public sealed record RecipeSummary(string Id, string Name, string Method, string Route, int? PublishedVersion, bool HasDraft, bool Deleted);

public sealed record DraftDiscarded(string Id, int? LiveVersion);

public sealed record TestRequest(Dictionary<string, string?>? Route, Dictionary<string, string?>? Query, Dictionary<string, string?>? Headers)
{
    public InboundRequest ToInbound() => new()
    {
        Route = Route ?? new Dictionary<string, string?>(),
        Query = Query ?? new Dictionary<string, string?>(),
        Headers = Headers ?? new Dictionary<string, string?>()
    };
}

/// <summary>
/// The result of Try it. When the draft has problems it isn't run: only <see cref="Passed"/> (false),
/// <see cref="Issues"/> and <see cref="Warnings"/> are set.
/// </summary>
public sealed record TestOutcome(
    bool Passed,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? StatusCode,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? DurationMs,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Partial,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<StepTrace>? Trace,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonNode? Body,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CorrelationId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<ValidationIssue>? Issues,
    IReadOnlyList<ValidationIssue> Warnings);

public sealed record PublishResult(string Id, int Version, string Url, IReadOnlyList<ValidationIssue> Warnings);

/// <summary>
/// One row of an endpoint's history. <see cref="By"/> is the person's id; <see cref="ByName"/> their display name
/// when the sign-in gave one. Both, and <see cref="At"/>, are empty for versions published before they were recorded.
/// </summary>
public sealed record HistoryRow(int Version, HistoryAction Action, DateTimeOffset? At, string? By, string? ByName, int? RestoredFrom, string? Name, string? Route, bool IsLive);

public sealed record RollbackResult(string Id, int Version, int RestoredFrom);

public sealed record EndpointDeleted(string Id, int? UnpublishedVersion, bool DraftDiscarded);

// ---------------------------------------------------------------- browser access (CORS, ADR 0015)

/// <summary>
/// The Browser access page: which websites may call /bff/* from a browser, and what they may send. Credentials are
/// never allowed (<see cref="AllowsCredentials"/> is always false; it's there so the page and scripts can say so).
/// </summary>
public sealed record BrowserAccessView(
    IReadOnlyList<AllowedOrigin> Origins,
    IReadOnlyList<SkippedOrigin> Skipped,
    IReadOnlyList<string> Patterns,
    IReadOnlyList<string> Methods,
    IReadOnlyList<string> Headers,
    IReadOnlyList<string> ExposedHeaders,
    int PreflightMaxAgeSeconds,
    bool AllowsCredentials,
    int MaxAdminEntries,
    bool CanChange);

public sealed record AddOriginRequest(string? Origin, string? Note);

/// <summary>Edit an origin added here: <c>Origin</c> as it is on the list now, <c>NewOrigin</c> and <c>Note</c> as they should be.</summary>
public sealed record ChangeOriginRequest(string? Origin, string? NewOrigin, string? Note);
public sealed record OriginTestRequest(string? Origin);
public sealed record OriginRemoved(string Origin);
