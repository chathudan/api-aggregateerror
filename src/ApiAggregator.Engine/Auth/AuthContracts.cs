using System.Text.Json.Nodes;
using ApiAggregator.Engine.Model;
using Microsoft.Extensions.Configuration;

namespace ApiAggregator.Engine.Auth;

/// <summary>Who is calling the gateway. Built from the validated inbound JWT.</summary>
public sealed class CallerContext
{
    public string? Subject { get; init; }

    /// <summary>The caller's raw access token. Only PassThroughUserToken may forward it.</summary>
    public string? BearerToken { get; init; }

    public JsonObject Claims { get; init; } = new();
    public string CorrelationId { get; init; } = Guid.NewGuid().ToString("n");

    public static CallerContext Anonymous(string? correlationId = null) =>
        new() { CorrelationId = correlationId ?? Guid.NewGuid().ToString("n") };
}

/// <summary>Resolves "secret:name" references. Production: Azure Key Vault. Development: configuration.</summary>
public interface ISecretResolver
{
    ValueTask<string> GetAsync(string name, CancellationToken ct);
}

/// <summary>Reads Secrets:{name} from configuration (user-secrets, environment variables, Key Vault provider).</summary>
public sealed class ConfigurationSecretResolver(IConfiguration configuration) : ISecretResolver
{
    public ValueTask<string> GetAsync(string name, CancellationToken ct)
    {
        var value = configuration[$"Secrets:{name}"];
        return string.IsNullOrEmpty(value)
            ? throw new AuthFailedException($"The secret '{name}' has no value. Add it to Key Vault (or Secrets:{name} in configuration), or type it in the admin UI under Services > Sign-in.")
            : ValueTask.FromResult(value);
    }
}

/// <summary>One kind of downstream authentication. Add a new kind = add one class (see .claude/skills/add-auth-step).</summary>
public interface IAuthStep
{
    /// <summary>The value used in AuthStepDef.Kind, e.g. "Basic".</summary>
    string Kind { get; }

    /// <summary>Required settings, checked when a profile is saved.</summary>
    IReadOnlyList<string> RequiredSettings { get; }

    ValueTask ApplyAsync(AuthStepContext context, CancellationToken ct);
}

public sealed class AuthFailedException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Everything an auth step needs, with helpers that keep secrets out of logs and config.</summary>
public sealed class AuthStepContext
{
    public required HttpRequestMessage Request { get; init; }
    public required AuthStepDef Definition { get; init; }
    public required string ProfileId { get; init; }
    public required int StepIndex { get; init; }
    public required CallerContext Caller { get; init; }
    public required ISecretResolver Secrets { get; init; }
    public required DownstreamCredentialCache Cache { get; init; }
    public required IHttpClientFactory HttpClientFactory { get; init; }

    /// <summary>Cache key for anything this step caches (tokens, session cookies).</summary>
    public string CacheKey => $"auth:{ProfileId}:{StepIndex}";

    public async ValueTask<string> SettingAsync(string name, CancellationToken ct) =>
        await OptionalSettingAsync(name, ct)
        ?? throw new AuthFailedException($"Auth profile '{ProfileId}' step {StepIndex + 1} ({Definition.Kind}) is missing setting '{name}'.");

    public async ValueTask<string?> OptionalSettingAsync(string name, CancellationToken ct)
    {
        if (!Definition.Settings.TryGetValue(name, out var raw) || string.IsNullOrEmpty(raw)) return null;
        return raw.StartsWith("secret:", StringComparison.OrdinalIgnoreCase)
            ? await Secrets.GetAsync(raw["secret:".Length..], ct)
            : raw;
    }

    public string? Plain(string name) => Definition.Settings.GetValueOrDefault(name);
}
