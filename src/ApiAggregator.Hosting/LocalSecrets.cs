using System.Text.Json;
using System.Text.Json.Nodes;
using ApiAggregator.Engine.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ApiAggregator.Hosting;

/// <summary>
/// DEVELOPMENT ONLY: secrets typed into the admin UI are kept in config/secrets.local.json
/// (git-ignored), never in services.json or auth-profiles.json. The gateway and the admin API read
/// the file directly whenever a secret is needed (not through configuration reload, which lags by up
/// to a second), so a value saved in the UI works on the very next call, e.g. "Check sign-in".
/// In every other environment this does nothing: use Azure Key Vault.
/// </summary>
public static class LocalSecrets
{
    public const string FileName = "secrets.local.json";

    public static string PathFor(IConfiguration configuration, string contentRoot) =>
        Path.GetFullPath(Path.Combine(contentRoot, configuration["Aggregator:Config:Directory"] ?? "config", FileName));

    /// <summary>Call before AddAggregatorEngine: the engine only adds its default resolver if none is registered.</summary>
    public static WebApplicationBuilder AddLocalSecretsFile(this WebApplicationBuilder builder)
    {
        if (builder.Environment.IsDevelopment())
        {
            var file = PathFor(builder.Configuration, builder.Environment.ContentRootPath);
            builder.Services.AddSingleton<ISecretResolver>(_ => new LocalSecretsResolver(builder.Configuration, file));
        }
        return builder;
    }

    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task SaveAsync(string file, string name, string value, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var root = File.Exists(file) ? JsonNode.Parse(await File.ReadAllTextAsync(file, ct)) as JsonObject ?? new() : new JsonObject();
            if (root["Secrets"] is not JsonObject secrets) root["Secrets"] = secrets = new JsonObject();
            secrets[name] = value;

            var temp = file + ".tmp";
            await File.WriteAllTextAsync(temp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), ct);
            File.Move(temp, file, overwrite: true);
        }
        finally { Gate.Release(); }
    }
}

/// <summary>
/// Development resolver: configuration first (user-secrets, environment variables, appsettings),
/// then config/secrets.local.json, re-read only when the file has changed.
/// </summary>
public sealed class LocalSecretsResolver(IConfiguration configuration, string file) : ISecretResolver
{
    private readonly object _lock = new();
    private DateTime _loadedWriteTime;
    private Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public ValueTask<string> GetAsync(string name, CancellationToken ct)
    {
        var fromConfig = configuration[$"Secrets:{name}"];
        if (!string.IsNullOrEmpty(fromConfig)) return ValueTask.FromResult(fromConfig);

        if (FromFile(name) is { Length: > 0 } fromFile) return ValueTask.FromResult(fromFile);

        throw new AuthFailedException(
            $"The secret '{name}' has no value yet. Open Services > Sign-in for this service in the admin UI and type it again, " +
            $"or add it as Secrets:{name} in user-secrets or an environment variable.");
    }

    private string? FromFile(string name)
    {
        if (!File.Exists(file)) return null;
        var writeTime = File.GetLastWriteTimeUtc(file);
        lock (_lock)
        {
            if (writeTime != _loadedWriteTime)
            {
                try
                {
                    var secrets = (JsonNode.Parse(File.ReadAllText(file)) as JsonObject)?["Secrets"] as JsonObject;
                    _values = secrets?.ToDictionary(p => p.Key, p => p.Value?.ToString() ?? "", StringComparer.Ordinal) ?? new(StringComparer.Ordinal);
                    _loadedWriteTime = writeTime;
                }
                catch (Exception ex) when (ex is IOException or JsonException) { /* half-written file: keep the last good values */ }
            }
            return _values.GetValueOrDefault(name);
        }
    }
}
