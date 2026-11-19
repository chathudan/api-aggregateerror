using System.Collections.Concurrent;

namespace ApiAggregator.Admin.Hosting;

/// <summary>
/// The signed-in people's access tokens (OpenID Connect), in memory on the server, keyed by a random session id
/// that is in their cookie. Tokens never go into the cookie or the page, are never logged, and go when the
/// person signs out, when they expire, or when the app restarts (then the next Logs read falls back to the
/// configured service token, or says what to set).
/// </summary>
public sealed class UserTokenStore
{
    private readonly ConcurrentDictionary<string, (string Token, DateTimeOffset Expires)> _tokens = new();

    public void Put(string sessionId, string token, DateTimeOffset expires)
    {
        _tokens[sessionId] = (token, expires);
        // Small and bounded by the number of signed-in people; drop expired ones while here.
        foreach (var (key, value) in _tokens)
            if (value.Expires <= DateTimeOffset.UtcNow) _tokens.TryRemove(key, out _);
    }

    public string? Get(string? sessionId) =>
        sessionId is not null && _tokens.TryGetValue(sessionId, out var t) && t.Expires > DateTimeOffset.UtcNow.AddSeconds(30) ? t.Token : null;

    public void Remove(string sessionId) => _tokens.TryRemove(sessionId, out _);
}
