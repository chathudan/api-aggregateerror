using System.Text.Json.Nodes;

using ApiAggregator.Engine.Execution;

namespace ApiAggregator.Engine.Auth.Steps;

/// <summary>
/// OAuth 2.0 client credentials: gets a token (usually a JWT) from the token endpoint and
/// caches it until shortly before it expires.
/// Settings: tokenUrl, clientId, clientSecret, scope (optional), audience (optional), header (optional).
/// </summary>
public sealed class OAuthClientCredentialsStep : IAuthStep
{
    public string Kind => "OAuthClientCredentials";
    public IReadOnlyList<string> RequiredSettings { get; } = ["tokenUrl", "clientId", "clientSecret"];

    public async ValueTask ApplyAsync(AuthStepContext c, CancellationToken ct)
    {
        var credential = await c.Cache.GetOrCreateAsync(c.CacheKey, async token =>
        {
            var form = new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = await c.SettingAsync("clientId", token),
                ["client_secret"] = await c.SettingAsync("clientSecret", token)
            };
            if (await c.OptionalSettingAsync("scope", token) is { } scope) form["scope"] = scope;
            if (await c.OptionalSettingAsync("audience", token) is { } audience) form["audience"] = audience;

            var client = c.HttpClientFactory.CreateClient(EngineHttpClients.Auth);
            using var response = await client.PostAsync(await c.SettingAsync("tokenUrl", token), new FormUrlEncodedContent(form), token);
            if (!response.IsSuccessStatusCode)
                throw new AuthFailedException($"Token endpoint for profile '{c.ProfileId}' answered {(int)response.StatusCode}.");

            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(token)) as JsonObject;
            var accessToken = json?["access_token"]?.GetValue<string>()
                ?? throw new AuthFailedException($"Token endpoint for profile '{c.ProfileId}' returned no access_token.");
            var expiresIn = json["expires_in"]?.GetValue<int>() ?? 300;

            return new CachedCredential(
                new Dictionary<string, string> { ["token"] = accessToken }, [],
                DateTimeOffset.UtcNow.AddSeconds(Math.Max(10, expiresIn - 60))); // refresh 60 s early
        }, ct);

        TokenHeader.Set(c.Request, c.Plain("header"), credential.Headers["token"]);
    }
}
