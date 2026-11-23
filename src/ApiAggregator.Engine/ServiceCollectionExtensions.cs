using System.Net;
using ApiAggregator.Engine.Auth;
using ApiAggregator.Engine.Auth.Steps;
using ApiAggregator.Engine.Config;
using ApiAggregator.Engine.Execution;
using ApiAggregator.Engine.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ApiAggregator.Engine;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the engine. Configuration sections:
    ///   Aggregator:Config   -> FileConfigOptions
    ///   Aggregator:Engine   -> EngineOptions
    ///   Secrets:*           -> values for "secret:" references (development only)
    /// </summary>
    public static IServiceCollection AddAggregatorEngine(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<FileConfigOptions>(configuration.GetSection("Aggregator:Config"));
        services.Configure<EngineOptions>(configuration.GetSection("Aggregator:Engine"));

        services.TryAddSingleton<IConfigStore, FileConfigStore>();
        services.TryAddSingleton<ISecretResolver, ConfigurationSecretResolver>();
        services.TryAddSingleton<IResponseCache, MemoryResponseCache>();

        services.AddMemoryCache();
        services.AddSingleton<DownstreamCredentialCache>();

        services.AddSingleton<IAuthStep, BasicAuthStep>();
        services.AddSingleton<IAuthStep, ApiKeyStep>();
        services.AddSingleton<IAuthStep, StaticBearerStep>();
        services.AddSingleton<IAuthStep, PassThroughUserTokenStep>();
        services.AddSingleton<IAuthStep, OAuthClientCredentialsStep>();
        services.AddSingleton<IAuthStep, SessionLoginStep>();
        services.AddSingleton<DownstreamAuthenticator>();

        // "Accept any certificate" is honoured only in Development (ADR 0018). Without a host environment (a bare
        // container in a test) it counts as not Development.
        services.TryAddSingleton(sp => new DownstreamCertificates(
            sp.GetService<IHostEnvironment>()?.IsDevelopment() == true, sp.GetService<ILogger<DownstreamCertificates>>()));

        services.AddSingleton<CompositionExecutor>();
        services.AddSingleton<RecipeRouter>();

        // Security-relevant handler settings for every downstream call:
        //  - UseCookies = false: no shared cookie jar, so one user's session can never leak to another.
        //  - AllowAutoRedirect = false: credentials are never replayed to a redirect target.
        //  - PooledConnectionLifetime: picks up DNS changes (and certificate settings); connections are reused for speed.
        //  - SslOptions: the machine's trusted authorities, plus only what a service's certificate settings name (ADR 0018).
        foreach (var name in new[] { EngineHttpClients.Downstream, EngineHttpClients.Auth })
        {
            services.AddHttpClient(name)
                .ConfigurePrimaryHttpMessageHandler(sp => new SocketsHttpHandler
                {
                    UseCookies = false,
                    AllowAutoRedirect = false,
                    AutomaticDecompression = DecompressionMethods.All,
                    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                    ConnectTimeout = TimeSpan.FromSeconds(5),
                    SslOptions = { RemoteCertificateValidationCallback = sp.GetRequiredService<DownstreamCertificates>().Check }
                })
                .ConfigureHttpClient(c => c.Timeout = Timeout.InfiniteTimeSpan) // the engine applies per-step timeouts
                // The default HttpClient logging writes full URLs (including query values) at Information.
                // Query values can be personal data, so remove it: the engine logs ids, codes and timings itself.
                .RemoveAllLoggers();
        }

        return services;
    }
}
