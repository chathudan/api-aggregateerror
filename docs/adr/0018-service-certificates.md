# 0018 — Services with their own certificate: trust an exact one, or any in Development only

- Status: Accepted
- Date: 2026-11-24
- Deciders: Chathura Wijesinghe

## Context
Development and test services are often on `https://` with a certificate they signed themselves, or one for another
name (an IP address, a machine name). The gateway checks certificates the way a browser does, so every call to such
a service failed with 502 `unreachable`. Nothing in the configuration could change that.

The usual workarounds are dangerous:
- a global "skip certificate checks" switch (`DangerousAcceptAnyServerCertificateValidator`) that ends up in
  production;
- adding the test server's certificate to the machine's trusted authorities, which trusts it for every name and
  every program on that machine.

Either way, a server in between could read and change calls that carry credentials and personal data.

## Decision
- **Per service, in its settings** (`certificates` on the service):
  - `trusted`: SHA-256 fingerprints of exact certificates to accept, at most 10. This is allowed in **every**
    environment. Only that one certificate is accepted, so it is as strict as a normal check: a different certificate
    still fails, even one a trusted authority signed for another name.
  - `acceptAny`: accept any certificate from the host. **Development only.** The admin app refuses to save it
    elsewhere. If it reaches another environment in the files, the gateway ignores it, logs a warning once and checks
    as usual. `aggregator validate` prints a note.
- **Always the machine's check first.** A certificate the machine trusts, for the right name, is accepted as before.
  The settings only add to that; they never turn the check off.
- **Per host name.** Connections are pooled per host, so a rule applies to the base address's host: any service on
  that host shares it. It covers service calls, sign-in calls (login and token addresses) on the same host, and the
  admin app's spec downloads. It doesn't cover a sign-in address on another host.
- **No restart.** `DownstreamCertificates` takes the rules from the config snapshot each run uses (once per
  version). New connections use them straight away. Pooled connections are replaced within 5 minutes
  (`PooledConnectionLifetime`), so a removed certificate stops working by then.
- **Read, compare, trust.** The admin app's **More › Certificate** reads the certificate the saved service's
  address presents (`GET /admin/services/{id}/certificate`, author role). It does a TLS handshake only: nothing is
  sent, and only a saved service's own address can be read. It shows the subject, issuer, dates, fingerprint and why
  the machine doesn't trust it. The person compares the fingerprint with the one the owner gives them before choosing
  **Trust this certificate**.
- **A plain message.** A failed TLS check is 502 `unreachable` for apps. The step message is "… couldn't be reached
  securely.", and the detail (Try it, logs) says how to fix it.

## Consequences
- A new security invariant in `CLAUDE.md`: certificates are always checked; the only exceptions are a service's
  exact fingerprints, or "accept any" in Development only. No global switch.
- When a test server's certificate is renewed, its fingerprint changes. The new one must be trusted before the old
  one is removed, or calls fail in between. The guide says so.
- Trusting on first sight relies on the person comparing fingerprints. The UI and the guide ask them to compare
  with the owner's before trusting.
- Logs name the service id and host when a certificate is refused or "accept any" is used. These come from the
  configuration, not from requests.

## Alternatives considered
- **A global setting to skip checks in Development:** rejected. It is too easy to carry into other environments, and
  it covers every service, not the one that needs it.
- **A CA certificate per service** (trust a private authority): deferred. It suits organisations with an internal
  authority. Today that is better done on the machine, which already works. It can be added beside `trusted` later.
- **Pin by public key (SPKI) instead of certificate:** rejected for now. It survives renewals that keep the key, but
  people can't easily compare it with what browsers and `openssl` show. The certificate fingerprint is what owners
  can give.
