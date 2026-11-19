using ApiAggregator.Engine.Model;

namespace ApiAggregator.Engine.Config;

/// <summary>What happened to a recipe at one point in its history.</summary>
public enum HistoryAction { Published, RolledBack, Unpublished }

/// <summary>Who made a change and when. <see cref="By"/> is the caller's subject id, never a name or email.</summary>
public sealed record ChangeInfo(string? By, DateTimeOffset At, int? RestoredFrom = null);

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

    /// <summary>For a roll back: the version whose content was published again.</summary>
    public int? RestoredFrom { get; init; }

    public Recipe? Recipe { get; init; }
}
