using Microsoft.Extensions.Logging;

namespace ApiAggregator.Engine.Logging.Live;

/// <summary>
/// A logger provider that copies log lines into the <see cref="LiveLogBuffer"/> for the admin Logs page.
/// It adds nothing beyond the formatted message and a few ids (correlation, recipe, step, service, error code).
/// </summary>
public sealed class LiveLogProvider(LiveLogBuffer buffer, LiveLogOptions options) : ILoggerProvider, ISupportExternalScope
{
    private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();

    public ILogger CreateLogger(string categoryName) => new LiveLogger(categoryName, buffer, options, () => _scopes);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

    public void Dispose() { }

    private sealed class LiveLogger(string category, LiveLogBuffer buffer, LiveLogOptions options, Func<IExternalScopeProvider> scopes) : ILogger
    {
        private const int MaxExceptionLength = 500;
        private readonly string _shortCategory = category.StartsWith("ApiAggregator.", StringComparison.Ordinal) ? category["ApiAggregator.".Length..] : category;
        private readonly bool _ours = category.StartsWith("ApiAggregator", StringComparison.Ordinal);

        // Scopes are not stored: the page shows the log line, not the scope.
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => scopes().Push(state);

        public bool IsEnabled(LogLevel logLevel) =>
            logLevel != LogLevel.None && logLevel >= (_ours ? options.MinimumLevel : options.OtherCategoriesMinimumLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            // The ids we show come from the line's own fields and from the surrounding scopes (e.g. the request's correlation id).
            var ids = new Ids();
            if (state is IEnumerable<KeyValuePair<string, object?>> fields) ids.Collect(fields);
            scopes().ForEachScope(static (scope, ids) =>
            {
                if (scope is IEnumerable<KeyValuePair<string, object?>> scopeFields) ids.Collect(scopeFields);
            }, ids);

            buffer.Add(new LiveLogEntry(
                Seq: 0, // the buffer assigns it
                Timestamp: DateTimeOffset.Now,
                Level: logLevel switch { LogLevel.Trace or LogLevel.Debug => "Debug", LogLevel.Information => "Information", LogLevel.Warning => "Warning", LogLevel.Error => "Error", _ => "Critical" },
                Category: _shortCategory,
                EventId: eventId.Id,
                EventName: eventId.Name,
                Message: formatter(state, exception),
                CorrelationId: ids.CorrelationId,
                RecipeId: ids.RecipeId,
                Step: ids.Step,
                ServiceId: ids.ServiceId,
                ErrorCode: ids.ErrorCode,
                Exception: Describe(exception, _ours)));
        }

        /// <summary>
        /// Never a stack trace. For our own categories: type and message (our messages are written to hold
        /// no values). For framework categories: the type only, because framework messages can contain URLs
        /// with query values or other request data.
        /// </summary>
        private static string? Describe(Exception? exception, bool ours)
        {
            if (exception is null) return null;
            var text = ours ? $"{exception.GetType().Name}: {exception.Message}" : exception.GetType().Name;
            return text.Length <= MaxExceptionLength ? text : text[..MaxExceptionLength];
        }
    }

    /// <summary>Picks the few known id fields out of structured state and scopes. The first value found wins (the line's own beats the scope's).</summary>
    private sealed class Ids
    {
        public string? CorrelationId, RecipeId, Step, ServiceId, ErrorCode;

        public void Collect(IEnumerable<KeyValuePair<string, object?>> fields)
        {
            foreach (var (key, value) in fields)
            {
                var text = value?.ToString();
                if (string.IsNullOrEmpty(text)) continue;
                if (key.Equals("CorrelationId", StringComparison.OrdinalIgnoreCase)) CorrelationId ??= text;
                else if (key.Equals("RecipeId", StringComparison.OrdinalIgnoreCase)) RecipeId ??= text;
                else if (key.Equals("Step", StringComparison.OrdinalIgnoreCase)) Step ??= text;
                else if (key.Equals("ServiceId", StringComparison.OrdinalIgnoreCase)) ServiceId ??= text;
                else if (key.Equals("ErrorCode", StringComparison.OrdinalIgnoreCase)) ErrorCode ??= text;
            }
        }
    }
}
