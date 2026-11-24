using ApiAggregator.Engine.Config;

namespace ApiAggregator.Admin.Application;

/// <summary>Activity log entries (ADR 0020) as people read them.</summary>
public static class ActivityText
{
    /// <summary>"Draft saved by Ann Author via MCP (Claude Code), 3 min ago".</summary>
    public static string Line(ActivityEntry entry, DateTimeOffset now) =>
        $"{char.ToUpperInvariant(entry.Action[0])}{entry.Action[1..]} by {entry.Source.Describe()}, {Ago(entry.At, now)}";

    public static string Ago(DateTimeOffset at, DateTimeOffset now)
    {
        var span = now - at;
        if (span < TimeSpan.FromMinutes(1)) return "just now";
        if (span < TimeSpan.FromHours(1)) return $"{(int)span.TotalMinutes} min ago";
        if (span < TimeSpan.FromDays(1)) return $"{(int)span.TotalHours} h ago";
        return at.ToUniversalTime().ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture);
    }
}
