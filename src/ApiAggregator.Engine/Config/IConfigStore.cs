using ApiAggregator.Engine.BrowserAccess;
using ApiAggregator.Engine.Model;

namespace ApiAggregator.Engine.Config;

/// <summary>
/// Where services, auth profiles and recipes live. The file store is for development
/// and small installs; a PostgreSQL store implements the same interface (see docs/roadmap.md).
///
/// Drafts and published recipes are kept apart: editing a draft never changes what the
/// gateway serves until the draft is tested and published.
/// </summary>
public interface IConfigStore
{
    /// <summary>Services, auth profiles and PUBLISHED recipes. Cheap to call on every request.</summary>
    ValueTask<ConfigSnapshot> GetSnapshotAsync(CancellationToken ct = default);

    Task SaveServiceAsync(ApiService service, CancellationToken ct = default);

    /// <summary>Removes a service. False if there was none. The caller checks that no published recipe uses it.</summary>
    Task<bool> DeleteServiceAsync(string id, CancellationToken ct = default);

    Task SaveAuthProfileAsync(AuthProfile profile, CancellationToken ct = default);

    Task<IReadOnlyList<Recipe>> GetDraftsAsync(CancellationToken ct = default);
    Task<Recipe?> GetDraftAsync(string id, CancellationToken ct = default);
    Task SaveDraftAsync(Recipe draft, CancellationToken ct = default);

    /// <summary>Discards a draft. False if there was none. The published version is not touched.</summary>
    Task<bool> DeleteDraftAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Makes the recipe live and adds a history entry for its version (ADR 0012). Throws
    /// <see cref="HistoryConflictException"/> if that version is already in the history: versions are never reused.
    /// </summary>
    Task PublishAsync(Recipe recipe, ChangeInfo change, CancellationToken ct = default);

    /// <summary>
    /// Stops serving the recipe and adds an "unpublished" history entry. The history is kept, so any
    /// version can be rolled back to later. False if the recipe wasn't published.
    /// </summary>
    Task<bool> UnpublishAsync(string id, ChangeInfo change, CancellationToken ct = default);

    /// <summary>Ids of every recipe that has history, including ones that are no longer published (so they can be restored).</summary>
    Task<IReadOnlyList<string>> GetHistoryIdsAsync(CancellationToken ct = default);

    /// <summary>Newest first. Includes entries written before ADR 0012 (without who and when).</summary>
    Task<IReadOnlyList<RecipeHistoryEntry>> GetHistoryAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Replaces the origins added in the admin app (ADR 0015) and records the change. The history entry is written
    /// first and only once: throws <see cref="HistoryConflictException"/> if its number is already used.
    /// The config version changes, so the gateway rebuilds its CORS policy without a restart.
    /// </summary>
    Task SaveBrowserOriginsAsync(IReadOnlyList<BrowserOrigin> origins, BrowserAccessHistoryEntry change, CancellationToken ct = default);

    /// <summary>Every change to the admin-managed origins, newest first.</summary>
    Task<IReadOnlyList<BrowserAccessHistoryEntry>> GetBrowserAccessHistoryAsync(CancellationToken ct = default);

    /// <summary>Adds one author change to the activity log (ADR 0020). Append-only: entries are never changed or removed.</summary>
    Task AppendActivityAsync(ActivityEntry entry, CancellationToken ct = default);

    /// <summary>The newest <paramref name="max"/> entries, newest first; only of this kind and id when given.</summary>
    Task<IReadOnlyList<ActivityEntry>> GetActivityAsync(string? kind, string? id, int max, CancellationToken ct = default);
}

/// <summary>The version being published is already in the history (for example two people published at once).</summary>
public sealed class HistoryConflictException(string message) : Exception(message);
