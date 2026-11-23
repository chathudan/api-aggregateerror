using System.Collections.Frozen;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using ApiAggregator.Engine.Config;
using ApiAggregator.Engine.Logging;
using ApiAggregator.Engine.Validation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ApiAggregator.Engine.Execution;

/// <summary>
/// Decides whether to accept a service's TLS certificate (ADR 0018). A certificate the machine trusts, for the right
/// name, is always accepted. Beyond that only what a service's settings name:
/// <list type="bullet">
/// <item>an exact certificate, by its SHA-256 fingerprint (test servers that signed their own), in any environment;</item>
/// <item>any certificate from the host, in Development only.</item>
/// </list>
/// The downstream connections are shared per host, so the rules are per host name: a service can't trust a
/// certificate for one path of a host and not another. Rules follow the configuration: the engine calls
/// <see cref="Use"/> with the snapshot it runs on, and a change applies to new connections (pooled ones are replaced
/// within a few minutes).
/// </summary>
public sealed class DownstreamCertificates(bool isDevelopment, ILogger<DownstreamCertificates>? logger = null)
{
    private readonly ILogger _log = logger ?? NullLogger<DownstreamCertificates>.Instance;
    private readonly Lock _lock = new();
    private volatile Rules _rules = Rules.None;
    private readonly HashSet<string> _warned = new(StringComparer.Ordinal); // under _lock

    private sealed record Rules(string Version, FrozenDictionary<string, HostRule> ByHost)
    {
        public static readonly Rules None = new("", FrozenDictionary<string, HostRule>.Empty);
    }

    private sealed record HostRule(bool AcceptAny, FrozenSet<string> Fingerprints);

    /// <summary>Whether "accept any certificate" is honoured here (Development).</summary>
    public bool AcceptAnyAllowed => isDevelopment;

    /// <summary>Takes the rules from this configuration, once per version. Cheap to call on every request.</summary>
    public void Use(ConfigSnapshot config)
    {
        if (_rules.Version == config.Version) return;
        lock (_lock)
        {
            if (_rules.Version == config.Version) return;
            _rules = Build(config);
        }
    }

    /// <summary>The TLS callback for the downstream connections (SocketsHttpHandler.SslOptions).</summary>
    public bool Check(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        if (errors == SslPolicyErrors.None) return true;
        var host = sender is SslStream stream ? stream.TargetHostName : null;
        if (certificate is null || string.IsNullOrEmpty(host)) return false;

        if (_rules.ByHost.TryGetValue(Host(host), out var rule)
            && (rule.AcceptAny || rule.Fingerprints.Contains(CertificateRules.Fingerprint(certificate))))
            return true;

        _log.CertificateRefused(host, errors.ToString());
        return false;
    }

    private Rules Build(ConfigSnapshot config)
    {
        var byHost = new Dictionary<string, (bool AcceptAny, HashSet<string> Fingerprints)>(StringComparer.OrdinalIgnoreCase);
        foreach (var service in config.Services.OrderBy(s => s.Id, StringComparer.Ordinal))
        {
            if (service.Certificates is not { } certs) continue;
            // Only https has certificates. CertificateRules reports the other cases; here they're simply left out.
            if (!Uri.TryCreate(service.BaseUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) continue;

            var host = Host(uri.IdnHost);
            var rule = byHost.TryGetValue(host, out var existing) ? existing : (AcceptAny: false, Fingerprints: new HashSet<string>(StringComparer.Ordinal));
            foreach (var fingerprint in certs.Trusted.Take(CertificateRules.MaxTrusted).Select(CertificateRules.Normalize).OfType<string>())
                rule.Fingerprints.Add(fingerprint);
            if (certs.AcceptAny)
            {
                if (isDevelopment) rule.AcceptAny = true;
                // Once per service and host, not on every reload (the admin app's test calls make a version each).
                if (_warned.Count < 1000 && _warned.Add($"{service.Id}\n{host}"))
                {
                    if (isDevelopment) _log.AcceptAnyCertificateInUse(service.Id, host);
                    else _log.AcceptAnyCertificateIgnored(service.Id, host);
                }
            }
            byHost[host] = rule;
        }

        return new Rules(config.Version, byHost.ToFrozenDictionary(
            p => p.Key, p => new HostRule(p.Value.AcceptAny, p.Value.Fingerprints.ToFrozenSet(StringComparer.Ordinal)), StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>IPv6 addresses come with or without brackets depending on who's asking.</summary>
    private static string Host(string host) => host.Trim('[', ']');
}
