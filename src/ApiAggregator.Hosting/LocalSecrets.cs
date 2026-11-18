using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace ApiAggregator.Hosting;

/// <summary>
/// DEVELOPMENT ONLY: secrets typed into the admin UI are kept in config/secrets.local.json
/// (git-ignored), never in services.json or auth-profiles.json. Both the gateway and the admin API
/// read the file as configuration ("Secrets:name"), so a saved password works at once.
/// In every other environment this does nothing: use Azure Key Vault.
/// </summary>
public static class LocalSecrets
{
    public const string FileName = "secrets.local.json";

    public static string PathFor(IConfiguration configuration, string contentRoot) =>
        Path.GetFullPath(Path.Combine(contentRoot, configuration["Aggregator:Config:Directory"] ?? "config", FileName));

    public static WebApplicationBuilder AddLocalSecretsFile(this WebApplicationBuilder builder)
    {
        if (builder.Environment.IsDevelopment())
            builder.Configuration.AddJsonFile(PathFor(builder.Configuration, builder.Environment.ContentRootPath), optional: true, reloadOnChange: true);
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
