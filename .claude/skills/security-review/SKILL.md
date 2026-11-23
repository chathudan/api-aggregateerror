---
name: security-review
description: Review a change to the API Aggregator against its security invariants (inputs, escaping, headers, cookies, redirects, secrets, caching, logging, auth). Use before finishing any change to the engine, gateway, admin API, auth steps or recipes.
---

# Security review checklist

Go through each item against the diff. For every "no", fix it or explain why in the PR description.

## Inputs and requests
- [ ] Only declared inputs can reach a downstream call (`InputBinder`); no new path bypasses it
- [ ] New targets or bindings escape their values (`Uri.EscapeDataString`) and reject CR/LF
- [ ] The final URL still has to stay on the service host and base path (`RequestBuilder.BuildUri`)
- [ ] No new way to set `Authorization`, `Cookie`, `Host` or hop-by-hop headers from a recipe

## Auth and secrets
- [ ] Secrets are `secret:` references; nothing secret is logged, returned or put in an exception message
- [ ] The caller's token is forwarded only through `PassThroughUserTokenStep`
- [ ] Shared credential caching is per profile, never per user
- [ ] Downstream HttpClients still have `UseCookies = false`, `AllowAutoRedirect = false`
- [ ] Inbound: new endpoints have the right policy (`author`, `publisher`) or recipe scope; nothing is anonymous by accident

## Caching
- [ ] Off by default; per caller by default; keys hashed and versioned
- [ ] Only successful, complete responses are stored
- [ ] Any `perCaller: false` is justified as non-personal data

## Limits and failure
- [ ] Timeouts, response-size cap, forEach cap and step cap still apply
- [ ] Retries only for idempotent methods
- [ ] Errors shown to callers name the service and status, never downstream bodies or stack traces
- [ ] Nothing a downstream service does can become a 500: new exceptions on the call path are step failures (ADR 0017)
- [ ] Anything kept in memory for the life of the process is bounded (per config version, capped, or expiring), and
      nothing per request or per caller-chosen value (Host, headers, ids) grows without limit
- [ ] The overload limit (`Gateway:MaxConcurrentRequests`) still sits in front of new `/bff` work. For pipeline or
      engine changes, run `./scripts/bench.sh --profile smoke`

## Logging and data
- [ ] Logs contain ids, statuses and timings, never values or tokens
- [ ] Call details (ADR 0016) only in admin test runs (`IncludeRequestDetails`), never logged or stored, never with sign-in headers; summary lines keep query values masked
- [ ] Personal or health data handling changes are flagged for privacy review (`docs/architecture/03-auth-and-security.md`)

## Tests and docs
- [ ] A unit test covers the new behaviour, including a malicious input case
- [ ] `CLAUDE.md` invariants and the architecture docs are updated if behaviour changed
