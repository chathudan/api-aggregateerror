using Microsoft.Extensions.Logging;

namespace ApiAggregator.Engine.Logging;

/// <summary>
/// Every log line the engine writes, in one place. Source-generated, so they are fast and structured
/// (each {Placeholder} becomes a searchable field).
/// Rule: log ids, codes, statuses and timings only. Never request or response values, header or
/// query values, tokens or secret values.
/// Event ids: 1000s composition, 1100s steps, 1200s downstream sign-in, 1300s cache, 1400s config.
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

    [LoggerMessage(EventId = 1100, Level = LogLevel.Debug,
        Message = "Step {Step} succeeded: service {ServiceId}, endpoint {EndpointId}, HTTP {HttpStatus}, {DurationMs} ms, {Calls} call(s), fromCache {FromCache}")]
    public static partial void StepSucceeded(this ILogger logger, string step, string serviceId, string endpointId, int? httpStatus, long durationMs, int calls, bool fromCache);

    [LoggerMessage(EventId = 1101, Level = LogLevel.Warning,
        Message = "Step {Step} failed: service {ServiceId}, endpoint {EndpointId}, {ErrorCode}, HTTP {HttpStatus}, {DurationMs} ms. {Detail}")]
    public static partial void StepFailed(this ILogger logger, string step, string serviceId, string endpointId, string errorCode, int? httpStatus, long durationMs, string? detail);

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
}
