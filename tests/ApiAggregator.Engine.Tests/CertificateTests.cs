using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ApiAggregator.Engine.Auth;
using ApiAggregator.Engine.Config;
using ApiAggregator.Engine.Execution;
using ApiAggregator.Engine.Model;
using ApiAggregator.Engine.Validation;
using ApiAggregator.TestSupport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ApiAggregator.Engine.Tests;

/// <summary>
/// Services with a certificate no trusted authority signed (ADR 0018), against a real https server on this machine
/// with a certificate made for the test. Through the engine's own HttpClient registration, so the real TLS check runs.
/// </summary>
public sealed class CertificateTests(SelfSignedServer server) : IClassFixture<SelfSignedServer>
{
    private static readonly string OtherFingerprint = new('A', 64);

    // ---------------------------------------------------------------- the rules

    [Theory]
    [InlineData("3f:a1")]
    [InlineData("not a fingerprint at all")]
    [InlineData("")]
    public void Anything_but_64_hex_digits_is_not_a_fingerprint(string text) => Assert.Null(CertificateRules.Normalize(text));

    [Fact]
    public void A_fingerprint_is_the_same_with_colons_spaces_lower_case_or_a_sha256_prefix()
    {
        var plain = new string('a', 62) + "0f";
        var expected = plain.ToUpperInvariant();

        Assert.Equal(expected, CertificateRules.Normalize(plain));
        Assert.Equal(expected, CertificateRules.Normalize(CertificateRules.Display(expected)));
        Assert.Equal(expected, CertificateRules.Normalize("sha256:" + CertificateRules.Display(expected).ToLowerInvariant()));
        Assert.Equal(expected, CertificateRules.Normalize("SHA256 " + CertificateRules.Display(expected).Replace(':', ' ')));
    }

    [Fact]
    public void Accepting_any_certificate_is_a_problem_outside_development_only()
    {
        var service = Https("dev", new ServiceCertificates { AcceptAny = true });

        Assert.Empty(CertificateRules.Problems(service, isDevelopment: true));
        Assert.Equal(
            "Accepting any certificate is allowed only in Development. Trust the service's own certificate instead (Read certificate), or ask for one signed by your organisation's authority.",
            Assert.Single(CertificateRules.Problems(service, isDevelopment: false)));
    }

    [Fact]
    public void Certificate_settings_on_a_plain_http_address_and_broken_fingerprints_are_explained()
    {
        var service = new ApiService
        {
            Id = "plain", BaseUrl = "http://plain.test", Certificates = new ServiceCertificates { Trusted = ["12:34"] }
        };

        var problems = CertificateRules.Problems(service, isDevelopment: true).ToList();

        Assert.Equal(2, problems.Count);
        Assert.Equal("Certificate settings only apply to https addresses. Use https in the base address, or remove them.", problems[0]);
        Assert.Equal("'12:34' isn't a SHA-256 fingerprint. Use Read certificate to get it: 64 characters, 0-9 and A-F (colons are fine).", problems[1]);
    }

    // ---------------------------------------------------------------- real connections

    [Fact]
    public async Task A_self_signed_certificate_is_refused_when_no_setting_trusts_it()
    {
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => Call(server.Url, certificates: null, isDevelopment: true));

        Assert.Equal(HttpRequestError.SecureConnectionError, error.HttpRequestError);
    }

    [Fact]
    public async Task The_exact_certificate_trusted_by_its_fingerprint_is_accepted_in_production()
    {
        var trusted = new ServiceCertificates { Trusted = [CertificateRules.Display(server.Fingerprint).ToLowerInvariant()] };

        var response = await Call(server.Url, trusted, isDevelopment: false);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Another_certificates_fingerprint_does_not_let_this_one_through()
    {
        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            Call(server.Url, new ServiceCertificates { Trusted = [OtherFingerprint] }, isDevelopment: false));

        Assert.Equal(HttpRequestError.SecureConnectionError, error.HttpRequestError);
    }

    [Fact]
    public async Task A_fingerprint_trusted_for_one_host_does_not_apply_to_another()
    {
        // The service names 127.0.0.1; the call goes to localhost (the same server, another host name).
        var forIp = new ServiceCertificates { Trusted = [server.Fingerprint] };
        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            Call(server.Url, forIp, isDevelopment: false, serviceBaseUrl: $"https://127.0.0.1:{server.Port}/"));

        Assert.Equal(HttpRequestError.SecureConnectionError, error.HttpRequestError);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task Accept_any_certificate_is_honoured_in_development_only(bool isDevelopment, bool accepted)
    {
        var call = Call(server.Url, new ServiceCertificates { AcceptAny = true }, isDevelopment);

        if (accepted) Assert.Equal(HttpStatusCode.OK, (await call).StatusCode);
        else Assert.Equal(HttpRequestError.SecureConnectionError, (await Assert.ThrowsAsync<HttpRequestException>(() => call)).HttpRequestError);
    }

    [Fact]
    public async Task A_step_to_a_service_with_an_untrusted_certificate_says_so_and_trusting_it_makes_the_endpoint_work()
    {
        var recipe = new Recipe
        {
            Id = "tls", Route = "tls", RequireAuthentication = false,
            Steps = [new StepDef { Alias = "ping", Endpoint = "tls/ping" }]
        };
        ConfigSnapshot Config(ServiceCertificates? certs, string version) =>
            new([Https("tls", certs, server.Url)], [], [recipe], version);
        using var provider = Engine(isDevelopment: false);
        var executor = provider.GetRequiredService<CompositionExecutor>();

        var refused = await executor.ExecuteAsync(recipe, new InboundRequest(), CallerContext.Anonymous("c-1"), ExecutionMode.AdminTest, default, Config(null, "v1"));
        Assert.Equal(502, refused.StatusCode);
        var step = Assert.Single(refused.Trace);
        Assert.Equal("Self-signed couldn't be reached securely.", step.Message);
        Assert.Contains("More › Certificate", step.Detail);

        // The executor takes the new settings with the new config version: no restart.
        var trusted = await executor.ExecuteAsync(recipe, new InboundRequest(), CallerContext.Anonymous("c-2"), ExecutionMode.AdminTest, default,
            Config(new ServiceCertificates { Trusted = [server.Fingerprint] }, "v2"));
        Assert.Equal(200, trusted.StatusCode);
    }

    // ---------------------------------------------------------------- helpers

    private static ApiService Https(string id, ServiceCertificates? certs, string? baseUrl = null) => new()
    {
        Id = id, Name = "Self-signed", BaseUrl = baseUrl ?? $"https://{id}.test/", Certificates = certs,
        Endpoints = [new ApiEndpoint { Id = "ping", Path = "/ping" }]
    };

    private static async Task<HttpResponseMessage> Call(string url, ServiceCertificates? certificates, bool isDevelopment, string? serviceBaseUrl = null)
    {
        using var provider = Engine(isDevelopment);
        provider.GetRequiredService<DownstreamCertificates>()
            .Use(new ConfigSnapshot([Https("tls", certificates, serviceBaseUrl ?? url)], [], [], "v1"));
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(EngineHttpClients.Downstream);
        return await client.GetAsync(url + "ping");
    }

    /// <summary>The engine as a host registers it, in Development or Production.</summary>
    private static ServiceProvider Engine(bool isDevelopment)
    {
        var services = new ServiceCollection().AddLogging(l => l.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton<IHostEnvironment>(new Environment(isDevelopment ? Environments.Development : Environments.Production));
        var configuration = new ConfigurationBuilder().Build();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddAggregatorEngine(configuration);
        return services.BuildServiceProvider();
    }

    private sealed class Environment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
