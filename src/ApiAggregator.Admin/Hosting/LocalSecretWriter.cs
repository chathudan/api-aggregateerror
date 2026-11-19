using ApiAggregator.Admin.Application;
using ApiAggregator.Hosting;

namespace ApiAggregator.Admin.Hosting;

/// <summary>
/// Development only: saves a secret typed in the UI to secrets.local.json in the config folder (git-ignored).
/// AdminService refuses to call it outside Development (AdminOptions.CanSaveSecrets).
/// </summary>
internal sealed class LocalSecretWriter(IConfiguration configuration, IWebHostEnvironment env) : ISecretWriter
{
    public Task SaveAsync(string name, string value, CancellationToken ct) =>
        env.IsDevelopment()
            ? LocalSecrets.SaveAsync(LocalSecrets.PathFor(configuration, env.ContentRootPath), name, value, ct)
            : throw new InvalidOperationException("Secrets can only be saved to a file in Development.");
}
