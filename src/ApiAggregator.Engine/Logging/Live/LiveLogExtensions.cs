using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ApiAggregator.Engine.Logging.Live;

public static class LiveLogExtensions
{
    /// <summary>
    /// Keeps the most recent log lines in memory for the admin Logs page.
    /// Call it AFTER the console provider is set up (ClearProviders would remove it).
    /// Settings come from the "LiveLogs" section; when Enabled is false nothing is registered.
    /// </summary>
    public static ILoggingBuilder AddLiveLogs(this ILoggingBuilder builder, IConfiguration section)
    {
        var options = new LiveLogOptions();
        section.Bind(options);
        if (!options.Enabled) return builder;

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(new LiveLogBuffer(options.Capacity));
        builder.Services.AddSingleton<ILoggerProvider, LiveLogProvider>();
        return builder;
    }
}
