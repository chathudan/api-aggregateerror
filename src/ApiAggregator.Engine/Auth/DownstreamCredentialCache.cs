using System.Collections.Concurrent;
using ApiAggregator.Engine.Logging;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace ApiAggregator.Engine.Auth;

/// <summary>A downstream credential (token, session cookies) and when it stops being valid.</summary>
public sealed record CachedCredential(IReadOnlyDictionary<string, string> Headers, IReadOnlyList<KeyValuePair<string, string>> Cookies, DateTimeOffset ExpiresAt);

/// <summary>
/// Caches downstream credentials in memory with "single flight": if 200 requests arrive at once
/// with an expired session, only ONE login call is made and the other 199 wait for it.
/// Credentials never leave the process and are never written to logs or disk.
/// </summary>
public sealed class DownstreamCredentialCache(IMemoryCache cache, ILogger<DownstreamCredentialCache> logger)
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    public async ValueTask<CachedCredential> GetOrCreateAsync(string key, Func<CancellationToken, Task<CachedCredential>> create, CancellationToken ct)
    {
        if (cache.TryGetValue(key, out CachedCredential? hit) && hit!.ExpiresAt > DateTimeOffset.UtcNow) return hit;

        var gate = _locks.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (cache.TryGetValue(key, out hit) && hit!.ExpiresAt > DateTimeOffset.UtcNow) return hit;

            var created = await create(ct);
            cache.Set(key, created, created.ExpiresAt);
            logger.CredentialObtained(key, created.ExpiresAt); // the key is auth:<profile>:<step>, never a secret
            return created;
        }
        finally { gate.Release(); }
    }

    /// <summary>Forget credentials for a profile, e.g. after the service answered 401.</summary>
    public void InvalidateProfile(string profileId, int stepCount, string reason)
    {
        for (var i = 0; i < stepCount; i++) cache.Remove($"auth:{profileId}:{i}");
        logger.CredentialInvalidated(profileId, reason);
    }
}
