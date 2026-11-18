# 06 — Testing

| Layer | What | How |
|---|---|---|
| Unit | Expressions, transforms, conditions, planner, validator, input binding, request building (escaping, injection), executor (A→B→C, partial, 502, 400, cache on/off, per-caller isolation) | `tests/ApiAggregator.Engine.Tests` with xUnit: `dotnet test` |
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

When generating this repo, the 52 unit tests were compiled and run against the engine, all passing,
and the CLI and gateway were exercised against the mock services. Run `dotnet test` on your machine
once NuGet restore has pulled xUnit.
