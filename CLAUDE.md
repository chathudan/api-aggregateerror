# CLAUDE.md — API Aggregator

Guidance for Claude Code (and humans) working in this repository. Read this first, then
`docs/architecture/01-overview.md`.

## What this is

A .NET 10 gateway that serves **many aggregated endpoints** (`/bff/*`). Each endpoint is a
**recipe**: steps that call downstream service endpoints, pass values between them, and build one
response. QA and technicians author recipes in a Blazor wizard. Downstream auth is configurable per
service (Basic, session login, OAuth/JWT, API key, pass-through) in any combination.

## Principles (in priority order)

1. **Secure by default.** Never weaken an invariant listed below to make a feature easier.
2. **Simple and readable.** Plain C#, small classes, early returns, comments that say *why*. Avoid
   clever abstractions, reflection, dynamic code and new dependencies.
3. **Fast.** Parallel by default, no per-request I/O beyond the downstream calls, cache parsed things.
4. **Plain-English messages.** Validation and error text is read by non-developers: say what's wrong
   and how to fix it, with no stack traces and no jargon.

## Security invariants (do not break)

- Only **declared inputs** reach downstream services (`InputBinder`).
- Every bound value is **escaped for its target**. `.`/`..` path values are rejected, and the final
  URL must stay on the service host and base path (`RequestBuilder`).
- **No CR/LF** in header or cookie values. `Authorization`, `Cookie`, `Host` and hop-by-hop headers
  can't be bound (`RecipeValidator.BlockedHeaders`).
- Downstream HttpClients use `UseCookies = false` and `AllowAutoRedirect = false` (`ServiceCollectionExtensions`).
- **Secrets only as `secret:<name>` references.** Never log, return or cache secret values per user.
  The admin API may *store* a value only in Development (`secrets.local.json` in the config folder, git-ignored) and never returns one.
- **Caching is off by default and per caller by default.** Keys are hashed and include the recipe
  and config version. Never cache partial or non-2xx responses.
- **No scripting in recipes.** The path language and the transform list are fixed (ADR 0002).
- **No fuzzy matching.** `combine` merges records by exact key only; never by similar names, emails or
  dates of birth (ADR 0011). Identity resolution belongs in the system that owns it.
- The caller's token is forwarded only by `PassThroughUserTokenStep`.
- Logs never contain request or response values; log ids, statuses and timings.
- Responses carry `Cache-Control: no-store`.
- **History is append-only.** Entries are written once and never changed; version numbers are never
  reused. Publish, roll back and delete endpoint need `aggregator.publisher` (ADR 0012).
- `Auth:DevSigningKey` is Development-only (startup fails otherwise).

If a change touches any of these, run the `security-review` skill and add or adjust a unit test.

## Layout

| Path | Notes |
|---|---|
| `src/ApiAggregator.Engine` | **No NuGet packages**, only the ASP.NET Core shared framework. Keep it that way |
| `…/Model` | `Recipe`, `StepDef`, `BindingDef`, `ApiService`, `ApiEndpoint`, `AuthProfile` (init-only) |
| `…/Expressions` | `ValuePath` (path language), `Transforms`, `Template` (`{{ }}`), `Scope` |
| `…/Planning` | `ExecutionPlanner`: infers dependencies, groups steps into parallel levels |
| `…/Validation` | `RecipeValidator`: all plain-English checks |
| `…/Execution` | `InputBinder`, `RequestBuilder`, `CompositionExecutor`, `Conditions`, `ResponseCache` |
| `…/Auth` | `IAuthStep` + `Steps/*`, `DownstreamAuthenticator`, `DownstreamCredentialCache` |
| `…/Config` | `IConfigStore`, `FileConfigStore` (published / drafts / history), `RecipeHistoryEntry` (ADR 0012), `ConfigSnapshot` |
| `…/Authoring` | `SourceCatalog` (wizard dropdown options, schema inference), `AutoBinder` (pre-filled values) |
| `src/ApiAggregator.Gateway` | `Program.cs` (one handler for `/bff/{**path}`), `ApiDocsPage` (Swagger UI at `/bff/docs`); the spec comes from `Engine/Authoring/OpenApiDocumentBuilder` |
| `src/ApiAggregator.Admin.Api` | `Program.cs` (all admin endpoints), `OpenApiImporter` |
| `src/ApiAggregator.Admin.UI` | Pages: `Home` (dashboard), `Endpoints`, `EndpointEditor` (wizard: stepper, problems next to fields, run-order lanes, sticky footer), `Services`, `Logs`, `Help`. Shared: `PageShell` (every page: one primary action), `StatusPill` (icon + word, never colour alone), `ConfirmBox`, `ErrorBanner`, `Toasts`/`ToastService`, `FieldIssues`, `StepFlow`, `EmptyState`, `Loading`, `SignInEditor`. Form models in `Services/` (`RecipeForm`, `SignInForm` + `SignInKinds`, `ServiceForms`) |
| `src/ApiAggregator.Hosting` | `InboundAuth` (JWT setup, scopes, caller context), `LocalSecrets` (Development-only secrets file) |
| `tools/ApiAggregator.Cli` | `aggregator validate | run` |
| `samples/MockServices` | Service A (Basic `svc-a/pass-a`), B (session `svc-b/pass-b`), C (OAuth `client-c/secret-c`), D (API key `X-Api-Key: key-d`, spec at `/d/openapi.json`, not in the sample config: use it to try **Add service**); B also has `POST /b/orders/search` (body); I fixed token (`token-i`), J the caller's token, K API key `key-k` + login `svc-k/pass-k`; E Identity, F CRM, G Billing, H Links: people data for **Combine lists** (`PeopleSamples.cs`, explained in `samples/README.md`). All on :5100 |
| `samples/config/` | Sample services, sign-ins and recipes (used by `dev-run.sh`, the guides and the tests; has `SAMPLE-DATA.md`). `customer-summary` shows values passed into path, body, query and header; customer `C-30003` has no loyalty record (a partial answer) |
| `docs/` | `README.md` (index by audience), `guides/` (getting started, services, endpoints, combine lists, testing, app developers, troubleshooting, operations, security, glossary, FAQ), `architecture/` and `adr/` (the design record), `images/` (screenshots) |
| `tests/ApiAggregator.Docs.Tests` | Runs every `<!-- check: ... -->` example in `docs/` and compares the output (`scripts/check-docs.sh`) |
| `config/` | Your own configuration (real mode: `dev-run.sh --real`). Starts empty |

## Commands

```bash
dotnet build                                                   # whole solution
dotnet test                                                    # engine, gateway, admin API and UI tests (must stay green)
./scripts/install-playwright.sh                                # once: browsers for the UI tests (skipped without them)
./scripts/screenshots.sh                                       # refresh docs/images (dev ports must be free)
./scripts/check-docs.sh                                        # run every checked example in docs/ (also part of dotnet test)
dotnet run --project tools/ApiAggregator.Cli -- validate --config samples/config   # check every recipe and auth profile (also run for config/)
dotnet run --project tools/ApiAggregator.Cli -- run customer-overview --route id=C-10042 --config samples/config   # needs mocks + Secrets__* env
./scripts/dev-run.sh                                           # mocks :5100, gateway :5200, admin API :5210, UI :5300 (--real for config/)
./scripts/try-gateway.sh                                       # call the sample endpoints with a dev token
```

Target framework: set once in `Directory.Build.props` (`AggregatorTargetFramework`, default `net10.0`).
Package versions: `Directory.Packages.props` (central management). Only Gateway, Admin.Api,
Hosting and the tests use packages (the UI tests use Microsoft.Playwright, ADR 0013). Keep runtime packages on the 10.x line while targeting `net10.0`.

## How to make common changes

| Task | Skill / place |
|---|---|
| New downstream auth kind | `.claude/skills/add-auth-step` (and add a row to `SignInKinds` in the UI) |
| New value transform | `.claude/skills/add-transform` (rarely; see ADR 0002) |
| Write or fix a recipe | `.claude/skills/author-recipe` |
| Record a decision | `.claude/skills/write-adr` |
| Change anything security-relevant | `.claude/skills/security-review` |
| New validation rule | `RecipeValidator`: plain-English message, plus a test in `PlanningAndValidationTests` |
| New admin endpoint | `Admin.Api/Program.cs` in the right section; `author` or `publisher` policy; document it in `docs/architecture/04-admin-and-openapi.md` |
| New config store (PostgreSQL) | Implement `IConfigStore`; register it before `AddAggregatorEngine` (it uses `TryAdd`) |

## Conventions

- File-scoped namespaces, nullable enabled, `var` for obvious types, `sealed` by default.
- Engine model types are **init-only**. The UI uses its own mutable form models (`RecipeForm`).
- JSON uses `AggregatorJson.Options` (web defaults, camelCase enums, comments allowed).
- Tests: xUnit, one scenario per test, names that read as sentences, builders in `TestData`.
- Docs: update the matching file in `docs/architecture/` and `docs/guides/` in the same change. Write a new ADR for any
  decision that changes an invariant or adds a dependency.
- Guides: every response shown is real. Mark it with a `check:` comment so `check-docs.sh` keeps it true; never
  invent output. Technician sections are mirrored in the UI's `Help` page: change both.
- This is a healthcare-adjacent project: never invent clinical or regulatory requirements. Flag
  them for review by qualified people (see `docs/architecture/03-auth-and-security.md`).
