namespace ApiAggregator.Engine.Config;

/// <summary>How a change was made (ADR 0020): a page of the admin app, the /admin/* API (scripts), or MCP (an AI agent).</summary>
public enum ChangeChannel { Page, Api, Mcp }

/// <summary>
/// Who made a change and how. <see cref="Client"/> is the name an MCP client gives itself (e.g. "Claude Code"): a
/// label, never used to decide anything.
/// </summary>
public sealed record ChangeSource(string? By, string? ByName, ChangeChannel Via = ChangeChannel.Page, string? Client = null)
{
    /// <summary>"Ann Author", "Ann Author via the admin API", "Ann Author via MCP (Claude Code)".</summary>
    public string Describe()
    {
        var who = ByName ?? By ?? "someone";
        return Via switch
        {
            ChangeChannel.Api => $"{who} via the admin API",
            ChangeChannel.Mcp => $"{who} via MCP ({Client ?? "unknown client"})",
            _ => who
        };
    }
}

/// <summary>
/// One author change (ADR 0020): a service, sign-in or draft saved, imported, discarded or deleted, from any channel.
/// Appended once and never changed. Never the content and never values: what changed is in the files and, once
/// published, in the history.
/// </summary>
public sealed record ActivityEntry(DateTimeOffset At, string? By, string? ByName, ChangeChannel Via, string? Client, string Action, string Kind, string Id)
{
    public ChangeSource Source => new(By, ByName, Via, Client);

    // Kinds
    public const string Endpoint = "endpoint";
    public const string Service = "service";
    public const string SignIn = "sign-in";
    public const string Secret = "secret";
}
