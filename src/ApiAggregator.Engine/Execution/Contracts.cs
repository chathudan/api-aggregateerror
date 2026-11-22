using System.Text.Json.Nodes;

namespace ApiAggregator.Engine.Execution;

/// <summary>The parts of the caller's HTTP request the engine is allowed to see. Raw values; validation happens in InputBinder.</summary>
public sealed class InboundRequest
{
    public IReadOnlyDictionary<string, string?> Route { get; init; } = new Dictionary<string, string?>();
    public IReadOnlyDictionary<string, string?> Query { get; init; } = new Dictionary<string, string?>();
    public IReadOnlyDictionary<string, string?> Headers { get; init; } = new Dictionary<string, string?>();
}

public sealed class ExecutionMode
{
    /// <summary>Use the response and step caches (when enabled on the recipe). Admin "Try it" turns this off.</summary>
    public bool UseCache { get; init; } = true;

    /// <summary>Include method and URL of each downstream call in the trace. Admin test runs only; never on the public gateway.</summary>
    public bool IncludeRequestDetails { get; init; }

    public static readonly ExecutionMode Gateway = new();
    public static readonly ExecutionMode AdminTest = new() { UseCache = false, IncludeRequestDetails = true };
}

public enum StepOutcome { Succeeded, Failed, Skipped }

/// <summary>
/// What happened in one step. Message is the caller-facing text. Detail (the internal reason) and Details (each call's
/// request and response) are only filled when ExecutionMode.IncludeRequestDetails is on (admin "Try it", ADR 0016):
/// never for gateway callers, never logged or stored. Request is the call with query values masked.
/// </summary>
public sealed record StepTrace(
    string Alias,
    string? DisplayName,
    StepOutcome Outcome,
    int? HttpStatus,
    long DurationMs,
    int Calls,
    bool FromCache,
    string? Message,
    string? Request = null,
    string? ErrorCode = null,
    string? ServiceId = null,
    bool Retryable = false,
    string? Detail = null,
    IReadOnlyList<CallDetail>? Details = null);

/// <summary>
/// One downstream call as admin "Try it" shows it (ADR 0016): the full address, the headers, cookies and body the
/// recipe sent, and the service's answer. Sign-in headers are added after this is taken, so keys, tokens and session
/// cookies never appear; Set-Cookie is never kept. Bodies over <see cref="CallDetail.MaxBodyChars"/> are cut.
/// </summary>
public sealed record CallDetail(string Request, JsonObject? RequestHeaders, JsonNode? RequestBody, int? Status, JsonNode? Response, bool Truncated)
{
    public const int MaxBodyChars = 64 * 1024;

    /// <summary>For-each steps keep the first ones only.</summary>
    public const int MaxCallsShown = 20;

    /// <summary>A body as it's shown: itself, or its first 64 K characters as text when it's bigger.</summary>
    public static JsonNode? Cap(JsonNode? body, ref bool truncated)
    {
        if (body is null) return null;
        var text = body.ToJsonString();
        if (text.Length <= MaxBodyChars) return body;
        truncated = true;
        return JsonValue.Create(text[..MaxBodyChars] + "…");
    }
}

public sealed class CompositionResult
{
    public required int StatusCode { get; init; }
    public JsonNode? Body { get; init; }
    public bool Partial { get; init; }
    public bool FromCache { get; init; }

    /// <summary>Stable code of the first root cause (or "invalid_input"). Null when the call worked.</summary>
    public string? ErrorCode { get; init; }

    /// <summary>True when at least one root cause is worth retrying later.</summary>
    public bool Retryable { get; init; }

    public long DurationMs { get; init; }
    public IReadOnlyList<StepTrace> Trace { get; init; } = [];
}

public sealed class EngineOptions
{
    /// <summary>Largest downstream response accepted per call.</summary>
    public int MaxResponseBytes { get; set; } = 5 * 1024 * 1024;

    /// <summary>Upper bound for forEach fan-out, so one request can't trigger thousands of calls.</summary>
    public int MaxForEachItems { get; set; } = 100;

    /// <summary>Memory budget for the response and step caches.</summary>
    public int CacheSizeMb { get; set; } = 256;
}

/// <summary>Names of the HttpClients the engine uses. The gateway adds resilience policies to them.</summary>
public static class EngineHttpClients
{
    public const string Downstream = "aggregator-downstream";
    public const string Auth = "aggregator-auth";
}
