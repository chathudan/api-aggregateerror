using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ApiAggregator.Engine.Model;

namespace ApiAggregator.Engine.Validation;

/// <summary>
/// The rules for a service's certificate settings (ADR 0018): plain-English problems, and fingerprints in one form.
/// Shared by the admin app (refuses to save), the CLI (validate) and the engine (ignores what breaks them).
/// </summary>
public static class CertificateRules
{
    /// <summary>More than a handful means something else is wrong (a certificate per server should be replaced by a CA).</summary>
    public const int MaxTrusted = 10;

    /// <summary>"sha256:3f:a1:…" or "3FA1…" -> 64 upper-case hex digits; null when it isn't a SHA-256 fingerprint.</summary>
    public static string? Normalize(string? fingerprint)
    {
        if (string.IsNullOrWhiteSpace(fingerprint)) return null;
        var text = fingerprint.Trim();
        if (text.StartsWith("sha256", StringComparison.OrdinalIgnoreCase)) text = text[6..]; // "s" isn't a hex digit, so no fingerprint starts so
        var hex = new string(text.Where(c => c is not (':' or ' ' or '-')).ToArray());
        return hex.Length == 64 && hex.All(Uri.IsHexDigit) ? hex.ToUpperInvariant() : null;
    }

    /// <summary>The certificate's SHA-256 fingerprint, normalized (64 upper-case hex digits).</summary>
    public static string Fingerprint(X509Certificate certificate) => certificate.GetCertHashString(HashAlgorithmName.SHA256);

    /// <summary>"3F:A1:…", the way browsers and openssl show it, for people to compare.</summary>
    public static string Display(string normalized) => string.Join(':', normalized.Chunk(2).Select(pair => new string(pair)));

    /// <summary>What's wrong with a service's certificate settings, in plain English. Empty when they're fine (or absent).</summary>
    public static IEnumerable<string> Problems(ApiService service, bool isDevelopment)
    {
        if (service.Certificates is not { } certs) yield break;
        var https = Uri.TryCreate(service.BaseUrl, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps;
        if (!https && (certs.AcceptAny || certs.Trusted.Count > 0))
            yield return "Certificate settings only apply to https addresses. Use https in the base address, or remove them.";
        if (certs.AcceptAny && !isDevelopment)
            yield return "Accepting any certificate is allowed only in Development. Trust the service's own certificate instead (Read certificate), or ask for one signed by your organisation's authority.";
        if (certs.Trusted.Count > MaxTrusted)
            yield return $"Trust at most {MaxTrusted} certificates per service. For more, ask for certificates signed by one authority and add that authority to the machine.";
        foreach (var bad in certs.Trusted.Where(f => Normalize(f) is null))
            yield return $"'{Shorten(bad)}' isn't a SHA-256 fingerprint. Use Read certificate to get it: 64 characters, 0-9 and A-F (colons are fine).";
    }

    private static string Shorten(string text) => text.Length <= 40 ? text : text[..40] + "…";
}
