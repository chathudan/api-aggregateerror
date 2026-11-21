namespace ApiAggregator.Engine.Logging.Live;

/// <summary>
/// One log line as the admin "Logs" page shows it. Only ids, codes and the already-safe message text:
/// never request or response values, header or query values, tokens or secrets.
/// </summary>
/// <param name="Seq">Increasing number given by the buffer. Callers ask for "everything after seq N".</param>
/// <param name="Level">Debug, Information, Warning, Error or Critical.</param>
/// <param name="Category">The logger name without the "ApiAggregator." prefix, e.g. "Engine.Execution.CompositionExecutor".</param>
/// <param name="Exception">The exception TYPE and message only (never a stack trace), cut to 500 characters.</param>
/// <param name="Page">Admin app only: the page in use, path only (no query), e.g. "/endpoints/customer-summary".</param>
/// <param name="Location">For an exception: where in our code it happened, as method and file:line only (no values).</param>
public sealed record LiveLogEntry(
    long Seq,
    DateTimeOffset Timestamp,
    string Level,
    string Category,
    int EventId,
    string? EventName,
    string Message,
    string? CorrelationId = null,
    string? RecipeId = null,
    string? Step = null,
    string? ServiceId = null,
    string? ErrorCode = null,
    string? Exception = null,
    string? Page = null,
    string? Location = null);
