using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using ApiAggregator.Engine.Expressions;

using ApiAggregator.Engine.Execution;

namespace ApiAggregator.Engine.Auth.Steps;

/// <summary>
/// Logs in with a username and password, then reuses the session until it expires.
/// Covers the common combinations:
///   - login form/JSON -> session cookie                         (cookies are captured automatically)
///   - login -> JWT in the response body -> Bearer header         (set tokenPath)
///   - login with Basic auth -> session cookie and/or token       (set loginUsesBasic = true)
///
/// Settings:
///   loginUrl (required), username (required), password (required, secret:)
///   format          json (default) | form
///   usernameField   default "username"
///   passwordField   default "password"
///   loginUsesBasic  true | false (default false)
///   tokenPath       optional path into the login response, e.g. "token" or "data.accessToken"
///   tokenHeader     optional, default Authorization (Bearer)
///   ttlSeconds      how long the session is reused, default 900
/// </summary>
public sealed class SessionLoginStep : IAuthStep
{
    public string Kind => "SessionLogin";
    public IReadOnlyList<string> RequiredSettings { get; } = ["loginUrl", "username", "password"];

    public async ValueTask ApplyAsync(AuthStepContext c, CancellationToken ct)
    {
        var session = await c.Cache.GetOrCreateAsync(c.CacheKey, token => LoginAsync(c, token), ct);

        foreach (var (name, value) in session.Cookies) RequestCookies.Add(c.Request, name, value);
        if (session.Headers.TryGetValue("token", out var bearer)) TokenHeader.Set(c.Request, c.Plain("tokenHeader"), bearer);
    }

    private static async Task<CachedCredential> LoginAsync(AuthStepContext c, CancellationToken ct)
    {
        var username = await c.SettingAsync("username", ct);
        var password = await c.SettingAsync("password", ct);

        using var login = new HttpRequestMessage(HttpMethod.Post, await c.SettingAsync("loginUrl", ct));
        if (string.Equals(c.Plain("loginUsesBasic"), "true", StringComparison.OrdinalIgnoreCase))
        {
            login.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}")));
        }
        else
        {
            var fields = new Dictionary<string, string>
            {
                [c.Plain("usernameField") ?? "username"] = username,
                [c.Plain("passwordField") ?? "password"] = password
            };
            login.Content = string.Equals(c.Plain("format"), "form", StringComparison.OrdinalIgnoreCase)
                ? new FormUrlEncodedContent(fields)
                : JsonContent.Create(fields);
        }

        var client = c.HttpClientFactory.CreateClient(EngineHttpClients.Auth);
        using var response = await client.SendAsync(login, ct);
        if (!response.IsSuccessStatusCode)
            throw new AuthFailedException($"Login for profile '{c.ProfileId}' failed with {(int)response.StatusCode}.");

        // Session cookies: keep only name=value; attributes (Path, HttpOnly...) don't apply server-to-server.
        var cookies = new List<KeyValuePair<string, string>>();
        if (response.Headers.TryGetValues("Set-Cookie", out var setCookies))
        {
            foreach (var raw in setCookies)
            {
                var pair = raw.Split(';', 2)[0];
                var eq = pair.IndexOf('=');
                if (eq > 0) cookies.Add(KeyValuePair.Create(pair[..eq].Trim(), pair[(eq + 1)..].Trim()));
            }
        }

        var headers = new Dictionary<string, string>();
        if (c.Plain("tokenPath") is { Length: > 0 } tokenPath)
        {
            var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
            var tokenValue = JsonText.Of(ValuePath.Parse($"body.{tokenPath}").Evaluate(new Scope(new Dictionary<string, JsonNode?> { ["body"] = body })));
            if (string.IsNullOrEmpty(tokenValue))
                throw new AuthFailedException($"Login for profile '{c.ProfileId}' returned no value at '{tokenPath}'.");
            headers["token"] = tokenValue;
        }

        if (cookies.Count == 0 && headers.Count == 0)
            throw new AuthFailedException($"Login for profile '{c.ProfileId}' returned no session cookie or token.");

        var ttl = int.TryParse(c.Plain("ttlSeconds"), out var t) ? t : 900;
        return new CachedCredential(headers, cookies, DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(ttl, 10, 86_400)));
    }
}
