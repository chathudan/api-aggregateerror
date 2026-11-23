using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace MockServices;

/// <summary>
/// The mocks' own https certificate (https://localhost:5101), signed by itself like many test servers', so the
/// admin app's "More › Certificate" can be tried end to end (ADR 0018, docs/guides/adding-services.md).
/// Made once and kept in certs/mock-https.pfx (git-ignored), so its fingerprint stays the same across restarts and a
/// trusted fingerprint keeps working. Delete the file to get a new one. Local testing only: never a real certificate.
/// </summary>
public static class MockCertificate
{
    public static X509Certificate2 LoadOrCreate(string contentRoot)
    {
        var file = Path.Combine(contentRoot, "certs", "mock-https.pfx");
        if (!File.Exists(file))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file, Create());
        }
        var certificate = X509CertificateLoader.LoadPkcs12FromFile(file, null);
        // What a service owner would tell you: compare it with what Read certificate shows before trusting it.
        Console.WriteLine($"Mock https certificate (self-signed) SHA-256 fingerprint: {certificate.GetCertHashString(HashAlgorithmName.SHA256).Chunk(2).Select(p => new string(p)).Aggregate((a, b) => a + ":" + b)}");
        return certificate;
    }

    private static byte[] Create()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost, O=API Aggregator mock services (test only)", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddIpAddress(System.Net.IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false)); // server authentication
        using var made = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
        return made.Export(X509ContentType.Pfx);
    }
}
