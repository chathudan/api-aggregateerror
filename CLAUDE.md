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
  The admin app may *store* a value only in Development (`secrets.local.json` in the config folder, git-ignored) and never returns one.
- **Caching is off by default and per caller by default.** Keys are hashed and include the recipe
  and config version. Never cache partial or non-2xx responses.
- **No scripting in recipes.** The path language and the transform list are fixed (ADR 0002).
- **No fuzzy matching.** `combine` merges records by exact key only; never by similar names, emails or
  dates of birth (ADR 0011). Identity resolution belongs in the system that owns it.
- The caller's token is forwarded only by `PassThroughUserTokenStep`.
- **Admin sign-in has two schemes that never mix** (ADR 0014). Pages: the `aggregator.admin` cookie (dev sign-in only
  in Development, OIDC otherwise; startup fails outside Development without OIDC), POST forms with an antiforgery
  token. `/admin/*`: bearer tokens only, so the cookie gets 401. Roles are enforced in `AdminService`, not by hiding
  buttons. A signed-in person's access token stays in server memory, never in the cookie or the page.
- Logs never contain request or response values; log ids, statuses and timings.
- Responses carry `Cache-Control: no-store`.
- **CORS only on `/bff/{**path}`, never with credentials** (ADR 0015). Origins are exact (`OriginRules`: no `*`,
  `null`, paths or plain http outside Development/localhost); admin entries must fit `Gateway:Cors:AllowedOriginPatterns`
  and the gateway re-checks them. Changing them needs `aggregator.admin` (a separate role). The admin app has no CORS.
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
| `…/Config` | `IConfigStore`, `FileConfigStore` (published / drafts / history / `cors.json`), `RecipeHistoryEntry` (ADR 0012), `ConfigSnapshot` |
| `…/BrowserAccess` | `OriginRules`, `BrowserAccessPolicy` (CORS for `/bff/*`, ADR 0015): shared by the gateway, the admin app and the CLI |
| `…/Authoring` | `SourceCatalog` (wizard dropdown options, schema inference), `AutoBinder` (pre-filled values) |
| `src/ApiAggregator.Gateway` | `Program.cs` (one handler for `/bff/{**path}`), `BffCorsPolicyProvider` (CORS, rebuilt per config version), `ApiDocsPage` (Swagger UI at `/bff/docs`); the spec comes from `Engine/Authoring/OpenApiDocumentBuilder` |
| `src/ApiAggregator.Admin.Application` | `AdminService`: every admin use case with its rules and **role checks** (author / publisher / admin), `AdminActor`, `AdminResult`, `OpenApiImporter`. No ASP.NET Core types (ADR 0014) |
| `src/ApiAggregator.Admin` | The admin app on :5300 (ADR 0014). `Hosting/`: `AdminAuth` (cookie for pages, bearer only for `/admin/*`, dev sign-in, OIDC), `AdminEndpoints` (thin `/admin/*` wrappers over `AdminService`). Pages: `SignIn`, `Home` (dashboard), `Endpoints`, `EndpointEditor` (wizard: stepper, problems next to fields, run-order lanes, sticky footer), `Services`, `Logs`, `BrowserAccess` (Settings → Browser access (CORS)), `Help`. Shared: `PageShell` (every page: one primary action), `StatusPill` (icon + word, never colour alone), `ConfirmBox`, `ErrorBanner`, `Toasts`/`ToastService`, `FieldIssues`, `StepFlow`, `EmptyState`, `Loading`, `SignInEditor`. `Services/`: `CurrentUser` (the signed-in person), `LiveLogClient`, form models (`RecipeForm`, `SignInForm` + `SignInKinds`, `ServiceForms`) |
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
dotnet test                                                    # engine, gateway, admin app, UI and docs tests (must stay green)
./scripts/install-playwright.sh                                # once: browsers for the UI tests (skipped without them)
./scripts/screenshots.sh                                       # refresh docs/images (dev ports must be free)
./scripts/check-docs.sh                                        # run every checked example in docs/ (also part of dotnet test)
dotnet run --project tools/ApiAggregator.Cli -- validate --config samples/config   # check every recipe and auth profile (also run for config/)
dotnet run --project tools/ApiAggregator.Cli -- run customer-overview --route id=C-10042 --config samples/config   # needs mocks + Secrets__* env
./scripts/dev-run.sh                                           # mocks :5100, gateway :5200, admin app :5300 (--real for config/)
./scripts/try-gateway.sh                                       # call the sample endpoints with a dev token
```

Target framework: set once in `Directory.Build.props` (`AggregatorTargetFramework`, default `net10.0`).
Package versions: `Directory.Packages.props` (central management). Only Gateway, Admin (OpenID Connect, ADR 0014),
Admin.Application (Microsoft.OpenApi.Readers), Hosting and the tests use packages (the UI tests use Microsoft.Playwright, ADR 0013). Keep runtime packages on the 10.x line while targeting `net10.0`.

## How to make common changes

| Task | Skill / place |
|---|---|
| New downstream auth kind | `.claude/skills/add-auth-step` (and add a row to `SignInKinds` in the UI) |
| New value transform | `.claude/skills/add-transform` (rarely; see ADR 0002) |
| Write or fix a recipe | `.claude/skills/author-recipe` |
| Record a decision | `.claude/skills/write-adr` |
| Change anything security-relevant | `.claude/skills/security-review` |
| New validation rule | `RecipeValidator`: plain-English message, plus a test in `PlanningAndValidationTests` |
| New admin action | A method on `AdminService` that checks the role first (`NeedsAuthor` / `NeedsPublisher`), a test in `RoleTests`, then a thin wrapper in `Admin/Hosting/AdminEndpoints.cs` with the `api-author` or `api-publisher` policy; document it in `docs/architecture/04-admin-and-openapi.md`. Pages call the service, never the store or engine (`ArchitectureTests`) |
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
