# 06 — Testing

| Layer | What | How |
|---|---|---|
| Unit | Expressions, auto-binding suggestions, transforms, conditions, planner, validator, input binding, request building (escaping, injection), executor (A→B→C, partial, 502, 400, cache on/off, per-caller isolation, a draft tried again after a rename), authoring (`RecipeRefactor`: renaming rewrites only matching roots in bindings, templates, when, forEach, response, combine sources/via/owners and refuses clashes and reserved names; step names from display names; "did you mean" at every unknown-step message; loop options; `Conditions.TryParseSimple`) | `tests/ApiAggregator.Engine.Tests` with xUnit: `dotnet test` |
| File store | Write-once history, entries from before ADR 0012, unpublish entries, a running store dropping a recipe another process unpublished | `tests/ApiAggregator.Engine.Tests/FileConfigStoreTests.cs` |
| Sample recipes | `people-directory`, `people-newest`, `contacts-unique` from `samples/config/recipes`, run against the mock people data (`samples/MockServices/PeopleSamples.cs`, linked into the test project): the exact counts and record placement documented in `samples/README.md` | `tests/ApiAggregator.Engine.Tests/SampleDataTests.cs` |
| Host (gateway) | The real `Program.cs` in memory (`WebApplicationFactory`), running as Production with its own config folder and stubbed downstream HTTP: valid, forged, expired and malformed JWTs (401 `unauthenticated`), missing scope (403 `insufficient_scope`), 404 `not_found`, 429 `rate_limited` with `Retry-After`, 500 `internal_error` without exception text, 502 `auth_failed` without secret or profile names, `X-Correlation-Id` (echoed when safe, replaced when not) and `Cache-Control: no-store` on every response including errors, `/_admin/logs` needing an aggregator role, `/bff/openapi.json` parsing as OpenAPI 3.0 without internal details, `/bff/docs` and `/dev/token` off outside Development, startup failing with `Auth:DevSigningKey` outside Development. Browser access (`CorsTests`, [ADR 0015](../adr/0015-cors-for-the-gateway.md)): preflights allowed and refused, needing no token and never rate limited; exposed headers and `Vary: Origin`; 400/401/403/404/429/500/502/504 keep the CORS headers; a new `header.*` input and an added or removed origin apply without a restart; admin entries outside the operations pattern ignored; invalid settings stop startup; a rejected origin logged once without path or query; never `Access-Control-Allow-Credentials` | `tests/ApiAggregator.Gateway.Tests` |
| Host (admin app) | Save → test → publish round trip keeps the recipe's `combine` block in the published file and the history copy; history records who and when; roll back (new version, restoredFrom, refused when no longer valid); versions never reused after a roll back or delete; delete endpoint (gateway store stops serving it, history kept, restorable); discard draft; publisher-only actions; service delete blocked while published endpoints use it; rename; startup fails with `Auth:DevSigningKey` outside Development, or without OpenID Connect. Sign-in ([ADR 0014](../adr/0014-one-admin-app.md)): `/admin/*` refuses a request carrying only the page cookie (401) and accepts a bearer token; the development sign-in (antiforgery token required, return address kept inside the app, not mapped outside Development); pages redirect to `/signin` when signed out. Roles: an author is refused publishing both through `AdminService` and through `/admin/*`; nobody without a role can do anything. History records the signed-in person (`by` and `byName`) and a script's token subject. Architecture: pages and components never hold the store or the engine; `ApiAggregator.Admin.Application` references no ASP.NET Core assembly. Browser access: authors and publishers can look and test but not change (service and `/admin/cors`), the admin role can add and remove and history records who, `*`/`null`/trailing slash/plain http refused, operations origins and patterns respected, the admin role alone gives no endpoint rights, and the admin app sends no CORS headers to a cross-origin call. Wizard form: every recipe in `samples/config` and `config/` loads into `RecipeForm` and comes back as identical JSON, including settings the wizard doesn't show (concurrency, timeouts, defaults, input limits, body templates, hand-written response values). Pickers (`StepChoicesTests`): sample loops and conditions open in the picker or in Custom with their text kept, picked conditions are saved in the engine's syntax, and renaming in the form gives exactly the engine's rename for several samples | `tests/ApiAggregator.Admin.Tests` |
| Admin app (browser) | Signs in once through the development sign-in. Journeys: add a service from its spec, build/test/publish an endpoint (and call it through the gateway), see the logs for a test run; build "notes for each contact with an order" with the pickers only (nothing like an expression or a step name is typed), rename the looped list and try it again; rename a referenced step of a live endpoint (the prompt mentions error details, the combine mapping follows, Try it passes, "Keep" keeps the name); an unused step's name follows its display name without asking. Every page: named controls, one h1, no skipped headings, at most one primary button, no sideways scrolling at 1280px; skip link first in the tab order with a visible focus outline; the top bar names the signed-in person, and signing out ends the session | `tests/ApiAggregator.UI.Tests` (Playwright, [ADR 0013](../adr/0013-playwright-for-admin-ui-tests.md)). Once per machine: `scripts/install-playwright.sh`; skipped without browsers. `ServiceFormTests` there need no browser: a spec's server becomes a base address only if it's http(s) |
| Guide examples | Every example in `docs/` marked with a `check:` comment: `aggregator run` / `validate` against the mocks, or a call through the gateway with a dev token, compared with the output the guide shows (JSON, or lines of text with timings ignored). The format is in [docs/README.md](../README.md#how-these-guides-stay-correct) | `tests/ApiAggregator.Docs.Tests` (built mocks, gateway and CLI as processes on free ports over a temporary copy of `samples/config`). Alone: `scripts/check-docs.sh` |
| Recipe checks in CI | Every recipe and auth profile is valid | `dotnet run --project tools/ApiAggregator.Cli -- validate` (exit code 1 on problems) |
| End to end (local) | Real HTTP against Services A/B/C mocks with Basic, session and OAuth/JWT | `scripts/dev-run.sh`, then `scripts/try-gateway.sh`, or `aggregator run customer-overview --route id=C-10042` |
| Admin "Try it" | QA runs a draft against the test environment before publishing | Admin UI step 5; required by the publish gate |
| Contract | Imported OpenAPI specs re-checked so a removed field or endpoint is caught | Roadmap: scheduled re-import with a diff |
| Load | Release build, real services, expected recipe mix | k6, bombardier or NBomber; watch p95, error rate, sign-in count, cache hit ratio |
| Security | Header injection, path traversal, parameter smuggling, cross-caller cache isolation | Unit tests exist for each; add a pen test before production |

Run everything:

```bash
dotnet test
dotnet run --project tools/ApiAggregator.Cli -- validate --config samples/config
dotnet run --project tools/ApiAggregator.Cli -- validate --config config   # your own config
```

`dotnet test` runs every test project. None needs network, running services or secrets: the UI tests start
their own copies of the apps on free ports (and are skipped if Playwright's browsers aren't installed).

Refresh the screenshots the guides use (stop `dev-run.sh` first; they're taken on the usual ports):

```bash
./scripts/install-playwright.sh   # once per machine, and after upgrading Microsoft.Playwright
./scripts/screenshots.sh          # writes docs/images/*.png
```

### How the host tests work

- Each test class gets a fresh host (`GatewayApp` / `AdminApp`) with a temporary config folder,
  deleted afterwards. Variants (a rate limit of 1, a config store that throws) are separate instances.
- The hosts run as **Production**, so Development-only features stay off and the tests see what a
  real deployment does. Because `Auth:DevSigningKey` is refused outside Development, the tests swap
  in their own signing key (`tests/Shared/TestAuth.cs`, test code only, never referenced from `src`).
- Settings go in with `UseSetting`, not `ConfigureAppConfiguration`, so `Program.cs` sees them
  before `Build()`.
- Downstream calls are answered by a stub `HttpMessageHandler` registered as the primary handler of
  the engine's downstream HttpClient, so the resilience pipeline still runs.
- The gateway and the admin app both declare a global `Program` class, so they're tested from separate
  projects.
