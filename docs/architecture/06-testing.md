# 06 — Testing

| Layer | What | How |
|---|---|---|
| Unit | Expressions, auto-binding suggestions, transforms, conditions, planner, validator, input binding, request building (escaping, injection), executor (A→B→C, partial, 502, 400, cache on/off, per-caller isolation) | `tests/ApiAggregator.Engine.Tests` with xUnit: `dotnet test` |
| Host (gateway) | The real `Program.cs` in memory (`WebApplicationFactory`), running as Production with its own config folder and stubbed downstream HTTP: valid, forged, expired and malformed JWTs (401 `unauthenticated`), missing scope (403 `insufficient_scope`), 404 `not_found`, 429 `rate_limited` with `Retry-After`, 500 `internal_error` without exception text, 502 `auth_failed` without secret or profile names, `X-Correlation-Id` (echoed when safe, replaced when not) and `Cache-Control: no-store` on every response including errors, `/_admin/logs` needing an aggregator role, `/bff/openapi.json` parsing as OpenAPI 3.0 without internal details, `/bff/docs` and `/dev/token` off outside Development, startup failing with `Auth:DevSigningKey` outside Development | `tests/ApiAggregator.Gateway.Tests` |
| Host (admin API) | Save → test → publish round trip keeps the recipe's `combine` block in the published file and the history copy; startup fails with `Auth:DevSigningKey` outside Development | `tests/ApiAggregator.Admin.Api.Tests` |
| Recipe checks in CI | Every recipe and auth profile is valid | `dotnet run --project tools/ApiAggregator.Cli -- validate` (exit code 1 on problems) |
| End to end (local) | Real HTTP against Services A/B/C mocks with Basic, session and OAuth/JWT | `scripts/dev-run.sh`, then `scripts/try-gateway.sh`, or `aggregator run customer-overview --route id=C-10042` |
| Admin "Try it" | QA runs a draft against the test environment before publishing | Admin UI step 5; required by the publish gate |
| Contract | Imported OpenAPI specs re-checked so a removed field or endpoint is caught | Roadmap: scheduled re-import with a diff |
| Load | Release build, real services, expected recipe mix | k6, bombardier or NBomber; watch p95, error rate, sign-in count, cache hit ratio |
| Security | Header injection, path traversal, parameter smuggling, cross-caller cache isolation | Unit tests exist for each; add a pen test before production |

Run everything:

```bash
dotnet test
dotnet run --project tools/ApiAggregator.Cli -- validate --config config
```

`dotnet test` runs all three test projects. They need no network, no mock services and no secrets.

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
- The gateway and admin API both declare a global `Program` class, so they're tested from separate
  projects.
