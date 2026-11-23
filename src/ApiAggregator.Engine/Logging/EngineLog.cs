using Microsoft.Extensions.Logging;

namespace ApiAggregator.Engine.Logging;

/// <summary>
/// Every log line the engine writes, in one place. Source-generated, so they are fast and structured
/// (each {Placeholder} becomes a searchable field).
/// Rule: log ids, codes, statuses and timings only. Never request or response values, header or
/// query values, tokens or secret values.
/// Event ids: 1000s composition, 1100s steps, 1200s downstream sign-in, 1300s cache, 1400s config, 1500s combine.
/// </summary>
internal static partial class EngineLog
{
    // ---- composition (one recipe run)

    [LoggerMessage(EventId = 1000, Level = LogLevel.Debug,
        Message = "Composition {RecipeId} v{Version} started for {Subject}")]
    public static partial void CompositionStarted(this ILogger logger, string recipeId, int version, string subject);

    [LoggerMessage(EventId = 1001, Level = LogLevel.Information,
        Message = "Composition {RecipeId} v{Version} completed with {Status} in {DurationMs} ms; steps {Steps}; fromCache {FromCache}; partial {Partial}")]
    public static partial void CompositionCompleted(this ILogger logger, string recipeId, int version, int status, long durationMs, string steps, bool fromCache, bool partial);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Warning,
        Message = "Composition {RecipeId} v{Version} failed with {Status} ({ErrorCode}) in {DurationMs} ms; root causes {RootCauses}")]
    public static partial void CompositionFailed(this ILogger logger, string recipeId, int version, int status, string? errorCode, string rootCauses, long durationMs);

    [LoggerMessage(EventId = 1003, Level = LogLevel.Information,
        Message = "Composition {RecipeId} rejected the request: {ErrorCount} input problem(s)")]
    public static partial void InputRejected(this ILogger logger, string recipeId, int errorCount);

    // ---- steps

    // Information, so the Logs page shows which service endpoint each step called, its HTTP status and time.
    // {Call} is the endpoint's method and path TEMPLATE ("GET /contacts/{contactId}"), never the filled-in address.
    [LoggerMessage(EventId = 1100, Level = LogLevel.Information,
        Message = "Step {Step} succeeded: service {ServiceId}, endpoint {EndpointId} ({Call}), HTTP {HttpStatus}, {DurationMs} ms, {Calls} call(s), fromCache {FromCache}")]
    public static partial void StepSucceeded(this ILogger logger, string step, string serviceId, string endpointId, string call, int? httpStatus, long durationMs, int calls, bool fromCache);

    [LoggerMessage(EventId = 1101, Level = LogLevel.Warning,
        Message = "Step {Step} failed: service {ServiceId}, endpoint {EndpointId} ({Call}), {ErrorCode}, HTTP {HttpStatus}, {DurationMs} ms. {Detail}")]
    public static partial void StepFailed(this ILogger logger, string step, string serviceId, string endpointId, string call, string errorCode, int? httpStatus, long durationMs, string? detail);

    [LoggerMessage(EventId = 1102, Level = LogLevel.Debug,
        Message = "Step {Step} skipped: {Reason}")]
    public static partial void StepSkipped(this ILogger logger, string step, string reason);

    // ---- downstream sign-in

    [LoggerMessage(EventId = 1200, Level = LogLevel.Warning,
        Message = "Sign-in {ProfileId} failed for service {ServiceId}: {Reason}")]
    public static partial void DownstreamAuthFailed(this ILogger logger, string profileId, string serviceId, string reason);

    [LoggerMessage(EventId = 1201, Level = LogLevel.Information,
        Message = "Obtained a downstream credential {CacheKey}, valid until {ExpiresAt}")]
    public static partial void CredentialObtained(this ILogger logger, string cacheKey, DateTimeOffset expiresAt);

    [LoggerMessage(EventId = 1202, Level = LogLevel.Information,
        Message = "Dropped the cached credentials of sign-in {ProfileId}: {Reason}")]
    public static partial void CredentialInvalidated(this ILogger logger, string profileId, string reason);

    // ---- cache

    [LoggerMessage(EventId = 1300, Level = LogLevel.Debug,
        Message = "{Level} cache hit for {RecipeOrStep}")]
    public static partial void ResponseCacheHit(this ILogger logger, string level, string recipeOrStep);

    // ---- configuration

    [LoggerMessage(EventId = 1400, Level = LogLevel.Information,
        Message = "Configuration loaded: {Services} services, {Recipes} recipes, version {Version}")]
    public static partial void ConfigLoaded(this ILogger logger, int services, int recipes, string version);

    [LoggerMessage(EventId = 1401, Level = LogLevel.Error,
        Message = "Configuration reload failed; still serving the previous version")]
    public static partial void ConfigReloadFailed(this ILogger logger, Exception exception);

    // ---- service certificates (ADR 0018). Service ids and host names come from the configuration, not from requests.

    [LoggerMessage(EventId = 1410, Level = LogLevel.Warning,
        Message = "Service {ServiceId} is set to accept any certificate from {Host}. That is honoured only in Development, so its certificate is checked as usual")]
    public static partial void AcceptAnyCertificateIgnored(this ILogger logger, string serviceId, string host);

    [LoggerMessage(EventId = 1411, Level = LogLevel.Warning,
        Message = "Service {ServiceId} accepts any certificate from {Host} (Development only). Calls to it can be intercepted")]
    public static partial void AcceptAnyCertificateInUse(this ILogger logger, string serviceId, string host);

    [LoggerMessage(EventId = 1412, Level = LogLevel.Warning,
        Message = "The certificate of {Host} isn't trusted ({Errors}) and no service trusts it by fingerprint")]
    public static partial void CertificateRefused(this ILogger logger, string host, string errors);

    // ---- combine (merging records from several steps). Counts only: record values can be personal data.

    [LoggerMessage(EventId = 1500, Level = LogLevel.Information,
        Message = "Combine {Name} completed: inputs {Inputs}; matched {Matched}; unmapped {Unmapped}; ambiguous {Ambiguous}; without key {WithoutKey}; ignored {Ignored}; output {Output}; {DurationMs} ms")]
    public static partial void CombineCompleted(this ILogger logger, string name, string inputs, int matched, int unmapped, int ambiguous, int withoutKey, int ignored, int output, long durationMs);

    [LoggerMessage(EventId = 1501, Level = LogLevel.Warning,
        Message = "Combine {Name} failed: {Reason}")]
    public static partial void CombineFailed(this ILogger logger, string name, string reason);
}
