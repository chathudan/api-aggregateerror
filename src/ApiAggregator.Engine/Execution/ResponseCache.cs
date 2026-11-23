using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace ApiAggregator.Engine.Execution;

/// <summary>
/// Cache for composed responses and individual step responses, switched on per recipe / per step
/// with "cache": { "enabled": true, "ttlSeconds": 30 }.
/// The in-memory version is bounded by EngineOptions.CacheSizeMb. A Redis-backed version can
/// implement the same interface when several gateway pods must share one cache.
/// </summary>
public interface IResponseCache
{
    bool TryGet(string key, out byte[] value);
    void Set(string key, byte[] value, TimeSpan ttl);
}

public sealed class MemoryResponseCache : IResponseCache, IDisposable
{
    private readonly MemoryCache _cache;
    private readonly Timer _sweep;

    public MemoryResponseCache(IOptions<EngineOptions> options)
    {
        _cache = new MemoryCache(new MemoryCacheOptions
        {
            SizeLimit = (long)options.Value.CacheSizeMb * 1024 * 1024,
            ExpirationScanFrequency = TimeSpan.FromSeconds(10)
        });
        // MemoryCache drops expired entries only when it is used. When traffic stops they would stay in memory, and they
        // can hold personal data: sweep them out every 30 seconds whatever the traffic.
        _sweep = new Timer(_ => _cache.Compact(0), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    public bool TryGet(string key, out byte[] value)
    {
        if (_cache.TryGetValue(key, out byte[]? hit) && hit is not null) { value = hit; return true; }
        value = [];
        return false;
    }

    public void Set(string key, byte[] value, TimeSpan ttl) =>
        _cache.Set(key, value, new MemoryCacheEntryOptions { Size = value.Length + key.Length, AbsoluteExpirationRelativeToNow = ttl });

    public void Dispose()
    {
        _sweep.Dispose();
        _cache.Dispose();
    }
}

/// <summary>
/// Cache keys are hashed, so no customer or patient identifier is ever visible in a key,
/// and they include the config version, so a recipe edit can never serve an old shape.
/// </summary>
public static class CacheKeys
{
    public static string Response(string recipeId, int version, string configVersion, string? caller, string request) =>
        $"r:{recipeId}:{version}:{configVersion}:{Hash($"{caller}\n{request}")}";

    public static string Step(string serviceId, string endpointId, string? caller, string fingerprint) =>
        $"s:{serviceId}:{endpointId}:{Hash($"{caller}\n{fingerprint}")}";

    /// <summary>"*" = shared by everyone (only for non-personal data). Otherwise the caller's subject.</summary>
    public static string? CallerPart(bool perCaller, string? subject) => perCaller ? subject ?? "anonymous" : "*";

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)), 0, 16);
}
