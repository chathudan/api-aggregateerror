# 0017 — Overload protection, one circuit per service, and a benchmark that checks them

- Status: Accepted
- Date: 2026-11-22
- Deciders: Chathura Wijesinghe

## Context
The gateway will serve many devices, thousands of endpoints and many services, and must not fall over. A benchmark
(`tools/ApiAggregator.Bench`, [guide](../guides/benchmarking.md)) was added to show how it behaves under load, with
broken services, abusive requests, config changes and over time. Reading the code for it, and the first runs, found:

- **Pipeline rejections were 500s.** The standard resilience handler throws its own exceptions (open circuit, its
  timeout, its rate limiter). The engine didn't treat them as step failures, so callers got `internal_error`, even
  for optional steps.
- **One circuit for all services.** The handler had one pipeline for the whole downstream client, so one broken
  service could open the circuit for every service.
- **Retries that never ran.** The default back-off (about 2 s) is as long as a typical service timeout, so the step
  timed out before any retry.
- **Nothing limited requests in flight.** Each request can hold up to 100 downstream answers of up to 5 MB. Too
  much traffic would queue until memory or threads ran out, rather than being turned away.
- **Growth with endpoints and changes.**
  - Plans were cached per config version and never dropped.
  - Route matching tried every endpoint and allocated memory for each.
  - The OpenAPI document was rebuilt for every new `Host` header, with tens of milliseconds and tens of
    megabytes per build at 2,000 endpoints.

- **Found only by running it:**
  - **False 400s under load.** Input patterns had a 50 ms timeout. A garbage collection or a busy CPU could push a
    trivial match past it, and a valid value was refused (2 of 7.3 million requests).
  - **Huge answers hurt everyone.** Answers were read into a growing `MemoryStream`, so a service sending 6 MB
    answers made gigabytes of garbage and slowed unrelated endpoints to a p99 of 1.6 s.
  - **A cut-off answer was a 500.** A connection breaking while an answer was read threw an `IOException`, not
    treated as a step failure. Its message, with bytes of the answer, went to the error log.
  - **Expired cached answers stayed in memory** until the cache was next used, so with no traffic they stayed for
    good.

## Decision
- **Rejections are connection problems.** A small handler outside the pipeline turns any `ExecutionRejectedException`
  into an `HttpRequestException`. Optional steps are skipped (a partial answer), and required ones give 502
  `unreachable`. Never a 500.
- **One pipeline per service.** The engine names the service on each request (`EngineHttpClients.ServiceId`), and the
  pipeline is chosen by it, so a circuit opens for that service only, even when services share a host.
- **A bulkhead per service.** At most `Gateway:MaxCallsInFlightPerService` (default 200) calls to one service at once;
  more fail straight away as `unreachable`. A slow service, or one sending huge answers, holds only part of the gateway.
- **The engine's timeouts govern.**
  - Retries (idempotent methods only, as before) wait 100 ms, then 200 ms.
  - The pipeline's own timeouts sit above the 60 s maximum step timeout.
- **Overload answers 503.** At most `Gateway:MaxConcurrentRequests` (default 1000) `/bff` requests run at once.
  - Beyond that the gateway answers 503 `overloaded` with `Retry-After: 5` straight away.
  - The check comes after CORS, so browsers can read the answer, and before authentication, so turning a request
    away costs little.
  - Health checks aren't counted.
- **Input patterns** run on the non-backtracking regex engine (no ReDoS, so no timeout). Patterns it can't run get
  250 ms. Each pattern is parsed once.
- **Answers are read in small pooled pieces** (a `PipeReader`, 16 KB segments) and parsed from those pieces, never
  copied into one large array.
  - The runs ruled out the simpler options. One growing array per answer was garbage that slowed everyone. Pooled
    large arrays kept about a gigabyte. Unpooled large arrays left the large-object heap fragmented, and the process
    never gave the memory back.
  - A broken connection while reading is a step failure (502 `unreachable`), and only the exception type is logged.
- **Expired cached answers are swept** every 30 seconds whatever the traffic.
- **Bounded caches.**
  - Plans are kept for the current config version only.
  - The OpenAPI document is kept for up to two addresses; any other address gets one shared copy without a server
    address.
- **Routing pre-check.** Routes are grouped by method and segment count, and literal words are compared before the
  framework's matcher runs. Precedence is unchanged.
- **Config reloads don't block.** One request checks the files while the others keep using the current config.
  Duplicate ids are refused with a plain message, and the last good config stays in use.
- **Diagnostics.** `GET /_admin/runtime` (off unless `Gateway:Diagnostics:Enabled`, authors and publishers only) shows
  numbers only: memory, collections, threads, requests in flight, requests turned away. The benchmark reads it.
  Forcing a garbage collection is allowed only in Development with `Gateway:Diagnostics:AllowForcedGc`.
- **Benchmark.**
  - It is our own small tool with no packages, run by `scripts/bench.sh`.
  - Its checks are pass/fail. The gateway must:
    - never stop;
    - give no unexpected answers under normal load;
    - let a broken service affect only the endpoints that need it;
    - refuse abusive requests with 4xx;
    - keep memory flat during the soak, and bring it back when traffic stops;
    - keep threads bounded.
  - Run it before releases and after changes to the engine or gateway pipeline.

## Consequences
- Apps can get a new answer, 503 `overloaded`. It is in the OpenAPI document and the app guide, and it is retryable.
- Each service has its own circuit breaker (about the cost of a small object per service).
- The benchmark runs everything on one machine. Its numbers compare runs; they don't size production. Size with a
  release build against the real services.
- Callers without a token are rate-limited by IP address. Behind a proxy or load balancer, enable forwarded headers
  (`ASPNETCORE_FORWARDEDHEADERS_ENABLED`) or they all share one allowance.

## Alternatives considered
- **k6 or NBomber** for the load: rejected. A second toolchain (k6) or a new dependency with licence terms (NBomber).
  The checks we need (memory readings, faults in the mocks, config churn) are easier in plain C# next to the code.
- **A queue instead of a hard limit:** rejected. Queued requests still hold memory and usually time out anyway. A quick
  503 with `Retry-After` lets apps and load balancers react.
- **A circuit per host:** rejected. Several services often share one host behind an API platform.
