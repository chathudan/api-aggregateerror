using System.Text.Json;
using System.Text.Json.Serialization;
using ApiAggregator.Engine.BrowserAccess;
using ApiAggregator.Engine.Model;

namespace ApiAggregator.Engine.Config;

/// <summary>An immutable view of all configuration at one moment. Swapped atomically on reload.</summary>
public sealed class ConfigSnapshot
{
    private readonly Dictionary<string, ApiService> _services;
    private readonly Dictionary<string, AuthProfile> _profiles;
    private readonly Dictionary<string, Recipe> _recipes;

    public ConfigSnapshot(IEnumerable<ApiService> services, IEnumerable<AuthProfile> profiles, IEnumerable<Recipe> recipes, string version,
        IEnumerable<BrowserOrigin>? browserOrigins = null)
    {
        BrowserOrigins = browserOrigins?.ToList() ?? [];
        _services = services.ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);
        _profiles = profiles.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
        _recipes = recipes.ToDictionary(r => r.Id, StringComparer.OrdinalIgnoreCase);
        Version = version;
    }

    /// <summary>Changes whenever any config file changes. Used in cache keys so edits never serve stale shapes.</summary>
    public string Version { get; }

    public IReadOnlyCollection<ApiService> Services => _services.Values;
    public IReadOnlyCollection<AuthProfile> AuthProfiles => _profiles.Values;
    public IReadOnlyCollection<Recipe> Recipes => _recipes.Values;

    /// <summary>Origins added in the admin app for browser access to /bff/* (config/cors.json, ADR 0015), in the order added.</summary>
    public IReadOnlyList<BrowserOrigin> BrowserOrigins { get; }

    public ApiService? GetService(string id) => _services.GetValueOrDefault(id);
    public AuthProfile? GetAuthProfile(string? id) => id is null ? null : _profiles.GetValueOrDefault(id);
    public Recipe? GetRecipe(string id) => _recipes.GetValueOrDefault(id);

    /// <summary>Resolves "serviceId/endpointId".</summary>
    public ApiEndpoint? FindEndpoint(string reference, out ApiService? service)
    {
        service = null;
        var slash = reference.IndexOf('/');
        if (slash <= 0) return null;
        service = GetService(reference[..slash]);
        var endpointId = reference[(slash + 1)..];
        return service?.Endpoints.FirstOrDefault(e => string.Equals(e.Id, endpointId, StringComparison.OrdinalIgnoreCase));
    }
}

public static class AggregatorJson
{
    /// <summary>One set of JSON options for config files and API payloads.</summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>The same, on one line (append-only logs such as the activity log, ADR 0020).</summary>
    public static readonly JsonSerializerOptions Compact = new(Options) { WriteIndented = false };
}
