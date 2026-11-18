# API Aggregator

Combine calls to several backend APIs into **one endpoint** for your web and mobile apps, without
writing code for each one.

```
GET /bff/customer/C-10042/overview
   ├─ Service A  GET /customers/C-10042                    (Basic auth)
   ├─ Service B  GET /orders   X-Customer-Id: <A.id>        (session login)
   └─ Service C  GET /recommendations?segment=<A.segment>   (OAuth → JWT)
                 X-Tier: <B.tier>
```

- **Many aggregated endpoints**, each built in a guided admin UI by QA or technicians, then tested,
  published and rolled back without a redeploy.
- **Pass values between calls** into the path, query, header, cookie or body. Steps run in parallel
  automatically when they don't depend on each other.
- **Any downstream auth combination:** Basic, session login (cookie and/or JWT), OAuth client
  credentials, API key, or the caller's own token.
- **OpenAPI import** for services that have a spec.
- **Caching on/off** per endpoint and per step. Entries are kept per caller, so nobody sees another
  person's data.
- **Secure by default:** declared inputs only, escaped values, no shared cookie jar, no redirects,
  secrets by reference, rate limiting, `no-store` responses.

## Quick start (macOS / Linux, .NET 10 SDK)

```bash
dotnet --version              # 10.x (or build with -p:AggregatorTargetFramework=net9.0)
dotnet restore && dotnet build
dotnet test                   # 52 engine tests

./scripts/dev-run.sh          # mocks :5100, gateway :5200, admin API :5210, admin UI :5300
./scripts/try-gateway.sh      # in another terminal: dev token + calls to the sample endpoints
open http://localhost:5300    # the wizard
```

Or with Docker: `docker compose up --build`.

**QA and CI without the gateway:**

```bash
export Secrets__service-a-password=pass-a Secrets__service-b-password=pass-b Secrets__service-c-client-secret=secret-c
dotnet run --project tools/ApiAggregator.Cli -- validate
dotnet run --project tools/ApiAggregator.Cli -- run customer-overview --route id=C-10042 --query status=open --repeat 2
```

## Repository map

| Path | What |
|---|---|
| `src/ApiAggregator.Engine` | The heart: model, expressions, planner, validator, executor, auth, cache. **No NuGet dependencies** |
| `src/ApiAggregator.Gateway` | Serves published endpoints under `/bff/*` |
| `src/ApiAggregator.Admin.Api` | Services, auth profiles, OpenAPI import, drafts, test, publish, rollback |
| `src/ApiAggregator.Admin.UI` | Blazor wizard for QA and technicians |
| `src/ApiAggregator.Hosting` | Shared inbound JWT setup |
| `tools/ApiAggregator.Cli` | `aggregator validate` and `aggregator run` |
| `samples/MockServices` | Services A/B/C with Basic, session and OAuth/JWT auth |
| `config/` | `services.json`, `auth-profiles.json`, `recipes/` (published), `drafts/`, `history/` |
| `tests/` | xUnit tests |
| `docs/` | [Architecture](docs/architecture/01-overview.md), [ADRs](docs/adr/README.md), [roadmap](docs/roadmap.md) |
| `CLAUDE.md`, `.claude/skills/` | Guidance for Claude Code working in this repo |

## Status

Development-ready. Before production, work through the "Next" list in [docs/roadmap.md](docs/roadmap.md):
PostgreSQL config store, Key Vault, OIDC for the admin UI, telemetry, and a privacy and security
review for health or personal data.
