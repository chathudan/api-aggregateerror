using System.Text.Json;
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
///   config/history/               every published version, for audit and rollback
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
                _logger.LogInformation("Configuration loaded: {Services} services, {Recipes} recipes", _snapshot.Services.Count, _snapshot.Recipes.Count);
            }
            catch (Exception ex) when (_snapshot is not null && ex is JsonException or IOException)
            {
                // A half-saved or broken file must never take the gateway down: keep serving the last good config.
                _logger.LogError(ex, "Configuration reload failed; still serving the previous version");
            }
            return _snapshot!;
        }
        finally { _gate.Release(); }
    }

    public Task SaveServiceAsync(ApiService service, CancellationToken ct = default) =>
        UpsertListAsync(ServicesFile, service, s => s.Id, ct);

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

    public async Task PublishAsync(Recipe recipe, CancellationToken ct = default)
    {
        System.IO.Directory.CreateDirectory(HistoryDir);
        var json = JsonSerializer.Serialize(recipe, AggregatorJson.Options);
        await WriteAtomicAsync(SafePath(HistoryDir, $"{recipe.Id}.v{recipe.Version}"), json, ct);
        await WriteAtomicAsync(SafePath(RecipesDir, recipe.Id), json, ct);
        _lastCheckUtc = DateTime.MinValue; // pick the change up on the next call
    }

    public async Task<IReadOnlyList<Recipe>> GetHistoryAsync(string id, CancellationToken ct = default)
    {
        if (!System.IO.Directory.Exists(HistoryDir)) return [];
        var versions = new List<Recipe>();
        foreach (var file in System.IO.Directory.EnumerateFiles(HistoryDir, $"{id}.v*.json"))
            if (await ReadRecipeAsync(file, ct) is { } r && r.Id == id) versions.Add(r);
        return versions.OrderByDescending(r => r.Version).ToList();
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

    /// <summary>Write to a temp file then rename, so readers never see a half-written file.</summary>
    private static async Task WriteAtomicAsync(string path, string content, CancellationToken ct)
    {
        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, content, ct);
        File.Move(temp, path, overwrite: true);
    }
}
