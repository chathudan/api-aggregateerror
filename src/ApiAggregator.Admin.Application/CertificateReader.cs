using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

namespace ApiAggregator.Admin.Application;

/// <summary>
/// Reads the certificate a service presents, without sending it anything (ADR 0018): a TLS handshake, then the
/// connection is closed. The certificate is accepted only for the handshake to finish, so it can be shown; nothing is
/// trusted by reading it.
/// </summary>
internal static class CertificateReader
{
    public static async Task<(X509Certificate2 Certificate, SslPolicyErrors Errors)> ReadAsync(Uri address, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(address.IdnHost, address.Port, timeout.Token);
        var errors = SslPolicyErrors.None;
        await using var tls = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = address.IdnHost,
            RemoteCertificateValidationCallback = (_, _, _, e) => { errors = e; return true; }
        }, timeout.Token);

        var remote = tls.RemoteCertificate ?? throw new IOException("The service sent no certificate.");
        return (X509CertificateLoader.LoadCertificate(remote.GetRawCertData()), errors);
    }

    /// <summary>Why the machine doesn't trust it, in plain English; null when it does.</summary>
    public static string? Problem(X509Certificate2 certificate, SslPolicyErrors errors, string host, DateTimeOffset now)
    {
        if (errors == SslPolicyErrors.None) return null;
        var reasons = new List<string>();
        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch))
            reasons.Add($"It is issued for another name than {host}.");
        if (now > certificate.NotAfter) reasons.Add($"It expired on {certificate.NotAfter:yyyy-MM-dd}.");
        else if (now < certificate.NotBefore) reasons.Add($"It isn't valid until {certificate.NotBefore:yyyy-MM-dd}.");
        else if (errors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors))
            reasons.Add("No authority this machine trusts signed it (a test server's own certificate looks like this).");
        if (reasons.Count == 0) reasons.Add("This machine doesn't trust it.");
        return string.Join(" ", reasons);
    }
}
