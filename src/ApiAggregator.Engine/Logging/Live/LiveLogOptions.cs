using Microsoft.Extensions.Logging;

namespace ApiAggregator.Engine.Logging.Live;

/// <summary>Settings for the in-memory live log, read from the "LiveLogs" configuration section.</summary>
public sealed class LiveLogOptions
{
    /// <summary>Turn the live log off completely (the endpoint then answers 404).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How many recent lines to keep in memory. Older lines are dropped.</summary>
    public int Capacity { get; set; } = 2000;

    /// <summary>Lowest level kept for our own code (categories starting with "ApiAggregator").</summary>
    public LogLevel MinimumLevel { get; set; } = LogLevel.Information;

    /// <summary>Lowest level kept for everything else (framework), so unhandled errors show but chatter doesn't.</summary>
    public LogLevel OtherCategoriesMinimumLevel { get; set; } = LogLevel.Warning;
}
