using ApiAggregator.Engine.Model;

namespace ApiAggregator.Engine.Config;

/// <summary>What happened to a recipe at one point in its history.</summary>
public enum HistoryAction { Published, RolledBack, Unpublished }

/// <summary>
/// Who made a change and when. <see cref="By"/> is the person's subject id (never an email);
/// <see cref="ByName"/> is their display name when their sign-in gives one (ADR 0014). <see cref="Via"/> and
/// <see cref="Client"/> say how (ADR 0020); <see cref="Drafted"/> who last changed the draft that went live, and how.
/// </summary>
public sealed record ChangeInfo(string? By, DateTimeOffset At, int? RestoredFrom = null, string? ByName = null,
    ChangeChannel Via = ChangeChannel.Page, string? Client = null, ChangeSource? Drafted = null);

/// <summary>
/// One entry in a recipe's history (ADR 0012). History only ever grows: entries are written once and
/// never changed, and version numbers are never reused.
///   Published / RolledBack  the recipe that went live as <see cref="Version"/>
///   Unpublished             the live version <see cref="Version"/> was taken down; <see cref="Recipe"/> is null
/// Entries written before ADR 0012 have no <see cref="At"/> or <see cref="By"/>.
/// </summary>
public sealed class RecipeHistoryEntry
{
    public required string Id { get; init; }
    public required int Version { get; init; }
    public HistoryAction Action { get; init; } = HistoryAction.Published;
    public DateTimeOffset? At { get; init; }
    public string? By { get; init; }

    /// <summary>The display name of <see cref="By"/>, when the sign-in gave one. Empty in entries written before ADR 0014.</summary>
    public string? ByName { get; init; }

    /// <summary>For a roll back: the version whose content was published again.</summary>
    public int? RestoredFrom { get; init; }

    /// <summary>How the change was made (ADR 0020). Null in entries written before it.</summary>
    public ChangeChannel? Via { get; init; }
    public string? Client { get; init; }

    /// <summary>Who last changed the draft that went live, and how, e.g. via MCP (ADR 0020). Null when unknown.</summary>
    public ChangeSource? Drafted { get; init; }

    public Recipe? Recipe { get; init; }
}
