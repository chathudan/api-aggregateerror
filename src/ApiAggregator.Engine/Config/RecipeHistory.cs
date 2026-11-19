using ApiAggregator.Engine.Model;

namespace ApiAggregator.Engine.Config;

/// <summary>What happened to a recipe at one point in its history.</summary>
public enum HistoryAction { Published, RolledBack, Unpublished }

/// <summary>
/// Who made a change and when. <see cref="By"/> is the person's subject id (never an email);
/// <see cref="ByName"/> is their display name when their sign-in gives one (ADR 0014).
/// </summary>
public sealed record ChangeInfo(string? By, DateTimeOffset At, int? RestoredFrom = null, string? ByName = null);

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

    public Recipe? Recipe { get; init; }
}
