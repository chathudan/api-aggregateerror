# Roadmap

## Done (this repo)

- Engine: recipes, path language, transforms, conditions, forEach, inferred parallel plan, validator
  with plain-English messages, executor with partial/502 semantics, a single retry on 401
- Downstream auth: Basic, ApiKey, StaticBearer, PassThroughUserToken, OAuthClientCredentials,
  SessionLogin (cookie and/or JWT, optionally with a Basic login), in any combination; single-flight credential cache
- Caching on/off per recipe and per step, per caller by default, hashed keys, bounded memory
- Gateway: dynamic `/bff/*` routing, JWT and scopes, rate limiting, resilience (idempotent retries),
  security headers, health checks, generated OpenAPI for aggregated endpoints
- Admin app (ADR 0014): one process for the pages and `/admin/*`, both over `AdminService` (all rules and role
  checks); services, auth profiles (plain-text secrets rejected), OpenAPI import (preview and select),
  schema inference, drafts, validate, test, publish gate, history, rollback
- Admin sign-in: development sign-in (name + role), OpenID Connect for production (code flow, PKCE, app roles);
  `/admin/*` bearer-only; history records the person (id and name)
- Admin UI (Blazor): Add service (from OpenAPI or by hand) with sign-in cards and Check sign-in; add endpoints by hand with a sample response; five-step wizard with an endpoint picker, pre-filled ("suggested") values, one-click response fields and live validation
- Live logs page in the admin UI (gateway and admin app, filter by correlation id)
- Duplicate handling: `distinct`, de-duplicated forEach calls, `combine` by exact key (ADR 0011)
- Guides by audience (`docs/README.md`, `docs/guides/`) with screenshots; every example is checked against the mocks by `scripts/check-docs.sh` and `dotnet test`
- Admin UI tests in a real browser (Playwright, ADR 0013)
- CLI (`aggregator validate | run`), mock Services A/B/C/D, 60 unit tests, Dockerfile, docker-compose

## Next: needed before production

| Item | Why |
|---|---|
| PostgreSQL `IConfigStore` (+ `LISTEN/NOTIFY` reload) | Several gateway pods; durable drafts and a durable publish gate |
| Key Vault `ISecretResolver` (or the Key Vault configuration provider) with managed identity | Production secrets |
| Register the admin app with your identity provider and assign the author and publisher app roles ([operations](guides/operations.md#deploy-to-azure-container-apps)) | The admin app won't start in production without it |
| OpenTelemetry traces and metrics (span per step, cache hit ratio, sign-in count) | Operability |
| Audit log sink with retention (tamper-evident) | Compliance |
| Privacy and security review (see [03](architecture/03-auth-and-security.md)), pen test | Health and personal data |
| Release-build load test against real services | Sizing |

## Later

| Item | Notes |
|---|---|
| Redis `IResponseCache` | Shared cache across pods |
| Token exchange (RFC 8693) auth step | User-delegated calls without forwarding the user's token |
| mTLS client certificates per service | Some internal services require them |
| Scheduled OpenAPI re-import with a diff and a "needs review" flag on affected recipes | Contract drift |
| JSON Schema for recipe files (editor autocomplete) | Developer comfort |
| Per-recipe rate limits and quotas | Protect expensive endpoints |
| Request-scoped response cache invalidation (`DELETE /admin/cache/{recipe}`) | Data fixes |
| Recipe templates and cloning in the UI | Faster authoring |
| "Combine lists" card in the wizard with Try it counts | Phase 4 of duplicate handling; the engine already supports `combine` |
| FHIR-aware helpers (e.g. Bundle flattening) as transforms | Only if several health recipes need them; see ADR 0002 |
