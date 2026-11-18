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
    Task SaveAuthProfileAsync(AuthProfile profile, CancellationToken ct = default);

    Task<IReadOnlyList<Recipe>> GetDraftsAsync(CancellationToken ct = default);
    Task<Recipe?> GetDraftAsync(string id, CancellationToken ct = default);
    Task SaveDraftAsync(Recipe draft, CancellationToken ct = default);

    /// <summary>Makes the recipe live and keeps a copy of the version under history/ for audit and rollback.</summary>
    Task PublishAsync(Recipe recipe, CancellationToken ct = default);

    Task<IReadOnlyList<Recipe>> GetHistoryAsync(string id, CancellationToken ct = default);
}
