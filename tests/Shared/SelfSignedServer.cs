using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ApiAggregator.Engine.Validation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ApiAggregator.TestSupport;

/// <summary>An https server on localhost with a certificate it signed itself, made fresh for the test run.</summary>
public sealed class SelfSignedServer : IAsyncLifetime
{
    private WebApplication? _app;

    public int Port { get; private set; }
    public string Url => $"https://localhost:{Port}/";
    public string Fingerprint { get; private set; } = "";

    public async Task InitializeAsync()
    {
        var certificate = Create();
        Fingerprint = CertificateRules.Fingerprint(certificate);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0, l => l.UseHttps(certificate)));
        _app = builder.Build();
        _app.MapGet("/ping", () => Results.Json(new { ok = true }));
        await _app.StartAsync();
        Port = new Uri(_app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First()).Port;
    }

    public async Task DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
    }

    private static X509Certificate2 Create()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false)); // server authentication
        using var made = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        // Through PFX, so the private key is usable by the TLS stack on every platform (macOS needs it).
        return X509CertificateLoader.LoadPkcs12(made.Export(X509ContentType.Pfx), null);
    }
}
