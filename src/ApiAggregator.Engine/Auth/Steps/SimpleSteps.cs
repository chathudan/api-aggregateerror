using System.Net.Http.Headers;
using System.Text;

namespace ApiAggregator.Engine.Auth.Steps;

/// <summary>Authorization: Basic base64(username:password). Settings: username, password.</summary>
public sealed class BasicAuthStep : IAuthStep
{
    public string Kind => "Basic";
    public IReadOnlyList<string> RequiredSettings { get; } = ["username", "password"];

    public async ValueTask ApplyAsync(AuthStepContext c, CancellationToken ct)
    {
        var user = await c.SettingAsync("username", ct);
        var pass = await c.SettingAsync("password", ct);
        c.Request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{pass}")));
    }
}

/// <summary>A static API key in a header (default) or query string. Settings: name, value, in (header|query).</summary>
public sealed class ApiKeyStep : IAuthStep
{
    public string Kind => "ApiKey";
    public IReadOnlyList<string> RequiredSettings { get; } = ["name", "value"];

    public async ValueTask ApplyAsync(AuthStepContext c, CancellationToken ct)
    {
        var name = await c.SettingAsync("name", ct);
        var value = await c.SettingAsync("value", ct);
        if (string.Equals(c.Plain("in"), "query", StringComparison.OrdinalIgnoreCase))
        {
            var uri = c.Request.RequestUri!;
            var sep = string.IsNullOrEmpty(uri.Query) ? "?" : "&";
            c.Request.RequestUri = new Uri($"{uri}{sep}{Uri.EscapeDataString(name)}={Uri.EscapeDataString(value)}");
        }
        else
        {
            c.Request.Headers.Remove(name);
            c.Request.Headers.TryAddWithoutValidation(name, value);
        }
    }
}

/// <summary>A long-lived bearer token (or JWT) issued to the gateway. Settings: token, header (optional).</summary>
public sealed class StaticBearerStep : IAuthStep
{
    public string Kind => "StaticBearer";
    public IReadOnlyList<string> RequiredSettings { get; } = ["token"];

    public async ValueTask ApplyAsync(AuthStepContext c, CancellationToken ct) =>
        TokenHeader.Set(c.Request, c.Plain("header"), await c.SettingAsync("token", ct));
}

/// <summary>
/// Forwards the caller's own access token. Use only when the downstream service trusts the same
/// identity provider and audience; otherwise prefer OAuthClientCredentials (token exchange is on the roadmap).
/// </summary>
public sealed class PassThroughUserTokenStep : IAuthStep
{
    public string Kind => "PassThroughUserToken";
    public IReadOnlyList<string> RequiredSettings { get; } = [];

    public ValueTask ApplyAsync(AuthStepContext c, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(c.Caller.BearerToken))
            throw new AuthFailedException("This service needs the caller's token, but the request has none.");
        TokenHeader.Set(c.Request, c.Plain("header"), c.Caller.BearerToken);
        return ValueTask.CompletedTask;
    }
}

internal static class TokenHeader
{
    /// <summary>Authorization: Bearer x by default, or any custom header name (raw value).</summary>
    public static void Set(HttpRequestMessage request, string? header, string token)
    {
        if (string.IsNullOrEmpty(header) || header.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return;
        }
        request.Headers.Remove(header);
        request.Headers.TryAddWithoutValidation(header, token);
    }
}
