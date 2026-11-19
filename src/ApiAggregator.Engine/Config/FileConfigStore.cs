using System.Text.Json;
using System.Text.Json.Nodes;
using ApiAggregator.Engine.Logging;
using ApiAggregator.Engine.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ApiAggregator.Engine.Config;

public sealed class FileConfigOptions
{
    /// <summary>Folder that contains services.json, auth-profiles.json and recipes/.</summary>
    public string Directory { get; set; } = "config";

    /// <summary>How often to look for changed files. Edits go live within this time, no restart.</summary>
    public int ReloadCheckSeconds { get; set; } = 2;
}

/// <summary>
/// JSON-file configuration:
///   config/services.json          [ ApiService ]
///   config/auth-profiles.json     [ AuthProfile ]   (secrets are "secret:name" references only)
///   config/recipes/*.json         PUBLISHED recipes, one per file (what the gateway serves)
///   config/drafts/*.json          work in progress from the admin UI (never served)
///   config/history/               append-only history, for audit and rollback (ADR 0012):
///                                   <id>.v<n>.json              published or rolled back as version n
///                                   <id>.v<n>.unpublished.json  version n was taken down
/// </summary>
public sealed class FileConfigStore : IConfigStore
{
    private readonly string _root;
    private readonly TimeSpan _checkInterval;
    private readonly ILogger<FileConfigStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private ConfigSnapshot? _snapshot;
    private DateTime _lastCheckUtc;
    private string _lastStamp = "";

    public FileConfigStore(IOptions<FileConfigOptions> options, ILogger<FileConfigStore> logger)
    {
        _root = Path.GetFullPath(options.Value.Directory);
        _checkInterval = TimeSpan.FromSeconds(Math.Max(1, options.Value.ReloadCheckSeconds));
        _logger = logger;
        System.IO.Directory.CreateDirectory(RecipesDir);
    }

    private string ServicesFile => Path.Combine(_root, "services.json");
    private string ProfilesFile => Path.Combine(_root, "auth-profiles.json");
    private string RecipesDir => Path.Combine(_root, "recipes");
    private string DraftsDir => Path.Combine(_root, "drafts");
    private string HistoryDir => Path.Combine(_root, "history");

    public async ValueTask<ConfigSnapshot> GetSnapshotAsync(CancellationToken ct = default)
    {
        // Fast path: no I/O at all between checks.
        if (_snapshot is not null && DateTime.UtcNow - _lastCheckUtc < _checkInterval) return _snapshot;

        await _gate.WaitAsync(ct);
        try
        {
            if (_snapshot is not null && DateTime.UtcNow - _lastCheckUtc < _checkInterval) return _snapshot;
            _lastCheckUtc = DateTime.UtcNow;

            var stamp = ComputeStamp();
            if (_snapshot is not null && stamp == _lastStamp) return _snapshot;

            try
            {
                _snapshot = await LoadAsync(stamp, ct);
                _lastStamp = stamp;
                _logger.ConfigLoaded(_snapshot.Services.Count, _snapshot.Recipes.Count, _snapshot.Version);
            }
            catch (Exception ex) when (_snapshot is not null && ex is JsonException or IOException)
            {
                // A half-saved or broken file must never take the gateway down: keep serving the last good config.
                _logger.ConfigReloadFailed(ex);
            }
            return _snapshot!;
        }
        finally { _gate.Release(); }
    }

    public Task SaveServiceAsync(ApiService service, CancellationToken ct = default) =>
        UpsertListAsync(ServicesFile, service, s => s.Id, ct);

    public async Task<bool> DeleteServiceAsync(string id, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var list = await ReadListAsync<ApiService>(ServicesFile, ct);
            if (list.RemoveAll(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase)) == 0) return false;
            await WriteAtomicAsync(ServicesFile, JsonSerializer.Serialize(list.OrderBy(s => s.Id), AggregatorJson.Options), ct);
            _lastCheckUtc = DateTime.MinValue;
            return true;
        }
        finally { _gate.Release(); }
    }

    public Task SaveAuthProfileAsync(AuthProfile profile, CancellationToken ct = default) =>
        UpsertListAsync(ProfilesFile, profile, p => p.Id, ct);

    public async Task<IReadOnlyList<Recipe>> GetDraftsAsync(CancellationToken ct = default)
    {
        if (!System.IO.Directory.Exists(DraftsDir)) return [];
        var drafts = new List<Recipe>();
        foreach (var file in System.IO.Directory.EnumerateFiles(DraftsDir, "*.json").Order())
            if (await ReadRecipeAsync(file, ct) is { } r) drafts.Add(r);
        return drafts;
    }

    public async Task<Recipe?> GetDraftAsync(string id, CancellationToken ct = default) =>
        await ReadRecipeAsync(SafePath(DraftsDir, id), ct);

    public async Task SaveDraftAsync(Recipe draft, CancellationToken ct = default)
    {
        System.IO.Directory.CreateDirectory(DraftsDir);
        await WriteAtomicAsync(SafePath(DraftsDir, draft.Id), JsonSerializer.Serialize(draft, AggregatorJson.Options), ct);
    }

    public Task<bool> DeleteDraftAsync(string id, CancellationToken ct = default)
    {
        var file = SafePath(DraftsDir, id);
        if (!File.Exists(file)) return Task.FromResult(false);
        File.Delete(file);
        return Task.FromResult(true);
    }

    public async Task PublishAsync(Recipe recipe, ChangeInfo change, CancellationToken ct = default)
    {
        System.IO.Directory.CreateDirectory(HistoryDir);
        var entry = new RecipeHistoryEntry
        {
            Id = recipe.Id, Version = recipe.Version, At = change.At, By = change.By, RestoredFrom = change.RestoredFrom,
            Action = change.RestoredFrom is null ? HistoryAction.Published : HistoryAction.RolledBack, Recipe = recipe
        };
        // History first and write-once: if this version is already there, nothing goes live.
        await WriteOnceAsync(SafePath(HistoryDir, $"{recipe.Id}.v{recipe.Version}"), JsonSerializer.Serialize(entry, AggregatorJson.Options), ct);
        await WriteAtomicAsync(SafePath(RecipesDir, recipe.Id), JsonSerializer.Serialize(recipe, AggregatorJson.Options), ct);
        _lastCheckUtc = DateTime.MinValue; // pick the change up on the next call
    }

    public async Task<bool> UnpublishAsync(string id, ChangeInfo change, CancellationToken ct = default)
    {
        var live = SafePath(RecipesDir, id);
        if (await ReadRecipeAsync(live, ct) is not { } recipe) return false;

        System.IO.Directory.CreateDirectory(HistoryDir);
        var entry = new RecipeHistoryEntry { Id = id, Version = recipe.Version, Action = HistoryAction.Unpublished, At = change.At, By = change.By };
        await WriteOnceAsync(SafePath(HistoryDir, $"{id}.v{recipe.Version}.unpublished"), JsonSerializer.Serialize(entry, AggregatorJson.Options), ct);
        File.Delete(live); // the gateway's next config check sees one recipe fewer and stops serving it
        _lastCheckUtc = DateTime.MinValue;
        return true;
    }

    public async Task<IReadOnlyList<RecipeHistoryEntry>> GetHistoryAsync(string id, CancellationToken ct = default)
    {
        SafePath(HistoryDir, id); // the id becomes part of a file pattern: same rules as every other id (no wildcards, no "..")
        if (!System.IO.Directory.Exists(HistoryDir)) return [];
        var entries = new List<RecipeHistoryEntry>();
        foreach (var file in System.IO.Directory.EnumerateFiles(HistoryDir, $"{id}.v*.json"))
            if (ReadHistoryEntry(await File.ReadAllTextAsync(file, ct)) is { } entry && entry.Id == id) entries.Add(entry);

        // Newest first. For the same version, "unpublished" happened after it went live.
        return entries.OrderByDescending(e => e.Version).ThenByDescending(e => e.Action == HistoryAction.Unpublished).ToList();
    }

    public Task<IReadOnlyList<string>> GetHistoryIdsAsync(CancellationToken ct = default)
    {
        if (!System.IO.Directory.Exists(HistoryDir)) return Task.FromResult<IReadOnlyList<string>>([]);
        // File names are "<id>.v<n>[.unpublished].json" and ids have no dots, so the id is everything before ".v".
        IReadOnlyList<string> ids = System.IO.Directory.EnumerateFiles(HistoryDir, "*.v*.json")
            .Select(f => Path.GetFileName(f))
            .Select(name => name[..name.IndexOf(".v", StringComparison.Ordinal)])
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        return Task.FromResult(ids);
    }

    /// <summary>An ADR 0012 entry, or a file from before it: the bare recipe, without who and when.</summary>
    private static RecipeHistoryEntry? ReadHistoryEntry(string json)
    {
        if (JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) is not JsonObject node)
            return null;
        if (node.ContainsKey("action") || node.ContainsKey("recipe"))
            return node.Deserialize<RecipeHistoryEntry>(AggregatorJson.Options);

        var recipe = node.Deserialize<Recipe>(AggregatorJson.Options);
        return recipe is null ? null : new RecipeHistoryEntry { Id = recipe.Id, Version = recipe.Version, Recipe = recipe };
    }

    // ------------------------------------------------------------------ helpers

    private async Task<ConfigSnapshot> LoadAsync(string stamp, CancellationToken ct)
    {
        var services = await ReadListAsync<ApiService>(ServicesFile, ct);
        var profiles = await ReadListAsync<AuthProfile>(ProfilesFile, ct);
        var recipes = new List<Recipe>();
        foreach (var file in System.IO.Directory.EnumerateFiles(RecipesDir, "*.json").Order())
            if (await ReadRecipeAsync(file, ct) is { Status: RecipeStatus.Published } recipe) recipes.Add(recipe);
        return new ConfigSnapshot(services, profiles, recipes, stamp);
    }

    private string ComputeStamp()
    {
        var files = new[] { ServicesFile, ProfilesFile }.Concat(System.IO.Directory.EnumerateFiles(RecipesDir, "*.json"));
        long ticks = 0, count = 0;
        foreach (var f in files.Where(File.Exists))
        {
            ticks = Math.Max(ticks, File.GetLastWriteTimeUtc(f).Ticks);
            count++;
        }
        return $"{ticks:x}-{count}";
    }

    private static async Task<Recipe?> ReadRecipeAsync(string file, CancellationToken ct)
    {
        if (!File.Exists(file)) return null;
        await using var stream = File.OpenRead(file);
        return await JsonSerializer.DeserializeAsync<Recipe>(stream, AggregatorJson.Options, ct);
    }

    /// <summary>Ids become file names, so only safe characters are allowed (no path traversal).</summary>
    private static string SafePath(string dir, string id)
    {
        if (id.Length is 0 or > 80 || !id.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '.'))
            throw new ArgumentException($"'{id}' is not a valid id.");
        if (id.Contains("..")) throw new ArgumentException($"'{id}' is not a valid id.");
        return Path.Combine(dir, id + ".json");
    }

    private static async Task<List<T>> ReadListAsync<T>(string file, CancellationToken ct)
    {
        if (!File.Exists(file)) return [];
        await using var stream = File.OpenRead(file);
        return await JsonSerializer.DeserializeAsync<List<T>>(stream, AggregatorJson.Options, ct) ?? [];
    }

    private async Task UpsertListAsync<T>(string file, T item, Func<T, string> id, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var list = await ReadListAsync<T>(file, ct);
            list.RemoveAll(x => string.Equals(id(x), id(item), StringComparison.OrdinalIgnoreCase));
            list.Add(item);
            await WriteAtomicAsync(file, JsonSerializer.Serialize(list.OrderBy(id), AggregatorJson.Options), ct);
            _lastCheckUtc = DateTime.MinValue;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Like <see cref="WriteAtomicAsync"/>, but never replaces an existing file (history is append-only).</summary>
    private static async Task WriteOnceAsync(string path, string content, CancellationToken ct)
    {
        if (File.Exists(path)) throw new HistoryConflictException($"Version history already has {Path.GetFileName(path)}.");
        var temp = path + "." + Guid.NewGuid().ToString("n") + ".tmp";
        await File.WriteAllTextAsync(temp, content, ct);
        try { File.Move(temp, path, overwrite: false); }
        catch (IOException) when (File.Exists(path))
        {
            File.Delete(temp);
            throw new HistoryConflictException($"Version history already has {Path.GetFileName(path)}.");
        }
    }

    /// <summary>Write to a temp file then rename, so readers never see a half-written file.</summary>
    private static async Task WriteAtomicAsync(string path, string content, CancellationToken ct)
    {
        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, content, ct);
        File.Move(temp, path, overwrite: true);
    }
}
