# Benchmarking

The benchmark answers one question before a release: **will the gateway keep answering, quickly and correctly,
with many devices, thousands of endpoints and services that misbehave?** It runs the real gateway, admin app and
mock services, drives them hard, breaks things on purpose, and gives a pass/fail report with the numbers behind
it. The design is in [ADR 0017](../adr/0017-overload-protection-and-per-service-circuits.md).

## Run it

```bash
./scripts/bench.sh                    # default profile, about 15 minutes
./scripts/bench.sh --profile smoke    # about 4 minutes: a quick check, or for CI
./scripts/bench.sh --profile soak     # adds a 30-minute soak, for the memory verdict
```

It builds in Release first and starts its own copies of the apps on free ports, so `dev-run.sh` can keep running.
Close other heavy programs: everything runs on your machine, including the load.

The report is written to `bench-results/<date>-<profile>/report.md`, with `report.json` for comparisons. The folder
is git-ignored. The exit code is 1 when a check fails.

| Option | Default | Meaning |
|---|---|---|
| `--profile smoke\|default\|soak` | `default` | How long each phase runs (below) |
| `--recipes 5000` | 2,000 (smoke 500) | Endpoints to generate |
| `--services 100` | 50 (smoke 20) | Services they call |
| `--callers 20000` | 5,000 | Different signed-in users (one token each) |
| `--workers 400` | 200 | Requests in flight at once |
| `--soak-minutes 60` | 6 (soak 30) | Length of the soak |
| `--baseline bench-results/<run>/report.json` | — | Adds a "compared with" table to the report |
| `--gcdump <path to dotnet-gcdump>` | — | Saves heap reports (largest types) after warm-up, churn and idle, to find what holds memory |

`./scripts/bench.sh micro` runs only the in-process timings (below), in seconds.

## What it does

1. **Generates a configuration**: 2,000 endpoints over 50 services, all valid (the same checks as the admin app). The
   services are generic ones in the mocks (`/bench/s01` … `/bench/s50`): a third sign in with an API key, a third with
   Basic, a third with none. Endpoints come in five shapes, a fifth each:
   - one call;
   - two calls in a chain;
   - three in parallel (one optional);
   - a list and then a call for each item (8 items);
   - a POST search with an optional follow-up.

   A fifth are cached per caller, and a tenth need no sign-in. Routes mix literal words and values in different
   places.
2. **Times the hot paths in-process**:
   - matching an address to an endpoint;
   - planning;
   - the path language;
   - building the OpenAPI document;
   - loading the config.
3. **Starts the apps** and gets a token for each of the 5,000 callers. Like real traffic, 80% of requests go to a
   tenth of the endpoints.
4. **Runs the phases** and reads the gateway's memory, threads and CPU every 2 seconds. After each main phase it
   forces a full garbage collection and reads the heap again: what is left then is what the gateway really holds.

| Phase | What happens | What must hold |
|---|---|---|
| Warm-up | Load from 200 workers | Every answer a full 200 |
| Ramp | 25, 50, 100, 200, 400 at once | Shows where throughput stops growing |
| Steady | 200 at once for 2 minutes | p99 under the profile's limit (1 s) |
| Faults | The service most endpoints need is made slow, then failing (500), flaky (503 half the time), hanging, dropping connections, then sending 6 MB answers. Half the workers call endpoints that use it, half call the others | Endpoints that need it answer 502/503/504, never 500. Those that use it for an optional step answer 200 (partial). **All others don't notice**, and stay fast |
| Config churn | An endpoint is published every 2 seconds under full load | Every answer a full 200 |
| Abuse | Alongside normal load: unknown routes, no or forged tokens, a token without the scope, values that break the pattern, CR/LF, `..`, a 40 KB header, a 20 KB address, the wrong method, a made-up `Host` on the OpenAPI document, one caller sending 1,000 requests | Each refused with the right 4xx (or 429), never a 5xx. The flooding caller gets 429s |
| Soak | Steady load, an endpoint published every 10 seconds, a heap reading every minute (each forces a full collection, which pauses the gateway briefly: it shows in the soak's maximum time) | The heap after a full collection doesn't grow: less than 2 MB per 10 minutes over the 30-minute soak, 10 MB over the default 6 minutes (fewer, noisier readings) |
| Idle | A trickle of traffic for 70 seconds | Memory comes back: within 25% or 50 MB of the level after warm-up. Some bounded memory rightly stays: the OpenAPI document (cached for two addresses, about 5 MB each at 2,000 endpoints) and a small pool of read buffers |

It also checks that:
- the gateway is running at the end (it must never stop);
- threads stay under 500;
- the admin app can list every endpoint quickly.

## Read the report

The top of `report.md` is the list of checks:

| Check | If it fails |
|---|---|
| The gateway never stopped | The process crashed or was killed. Look in `logs/ApiAggregator.Gateway.log` in the run's folder (warnings and errors are kept there) |
| Normal load: every answer is a full 200 | The **Unexpected answers** list at the end shows the first examples, with the endpoint and its shape |
| A broken service only affects the endpoints that need it | A fault leaked: a 500, or an unrelated endpoint failing. The **Faults** table shows which fault and which group |
| Other endpoints stay fast while a service is broken | The broken service is holding up unrelated requests (threads, connections or a shared circuit) |
| Bad and abusive requests are turned away with 4xx | One case got a 5xx or never got its expected answer: see the **Bad and abusive requests** table |
| p99 at steady load | Slower than the limit. Compare with a baseline run before blaming a change: machines differ |
| Memory doesn't grow during the soak | Something keeps memory per request or per change. Run again with `--gcdump` and compare the heap reports |
| Memory comes back when traffic stops | As above |
| Threads stay bounded | Something blocks threads (synchronous waits) |

Then come:
- the phases (requests, requests per second, p50/p95/p99/max, statuses, cache hits, 503s, unexpected answers);
- the ramp, the faults and the abuse cases;
- memory and threads at each checkpoint;
- the probes;
- the in-process timings.

The numbers compare runs **on the same machine**: the mocks, the gateway, the admin app and the load all share it.
They are not a capacity promise. To size production, load-test a release build against the real test services.

### Before and after

```bash
./scripts/bench.sh                                                          # on main: keep the report.json
git switch my-change
./scripts/bench.sh --baseline bench-results/<the main run>/report.json      # adds a "Compared with the baseline" table
```

### What the first runs found

The default profile, on one laptop (all apps and the load on the same machine), before and after
[ADR 0017](../adr/0017-overload-protection-and-per-service-circuits.md):

| | Before | After |
|---|---:|---:|
| Checks passed | 5 of 8 | 9 of 9 |
| Steady load: requests per second | 12,945 | 20,294 |
| Steady load: p99 | 44 ms | 22 ms |
| Unexpected answers under normal load | 2 (valid values refused with 400) | 0 of 12.2 million |
| Other endpoints while one service sends 6 MB answers: p99 | 1,588 ms | 389 ms |
| Endpoints that need a failing service: p99 | 4,107 ms (retries waited past the timeout) | 23 ms (its circuit opens) |
| Heap after full GC: after warm-up, then after idle | 86 MB, then 290 MB | 104 MB, then 104 MB |
| Heap during the soak (an endpoint published every 10 s) | Grew about 130 MB per 10 min | Flat (+1.5 MB per 10 min) |
| Matching an address at 2,000 endpoints | 53 µs, 40 KB of garbage | 18 µs, 678 bytes |

What it found, each now checked by the benchmark and covered by a unit test:

- **Every config version's plans kept forever.** This was most of the memory growth.
- **Address matching tried every endpoint.**
- **Input patterns had a 50 ms timeout**, which a garbage collection could exceed, so valid values were refused.
- **A service sending huge answers slowed every endpoint.** Answers were read into one growing array.
- **A connection cut mid-answer was a 500**, and bytes of the answer went to the error log.
- **The resilience pipeline:**
  - its rejections were 500s;
  - one circuit breaker was shared by all services;
  - retries waited about 2 s, longer than most service timeouts.
- **Expired cached answers stayed in memory** when traffic stopped.
- **A new OpenAPI document for every made-up `Host`.**
- **No limit on requests at once.**

Under the 6 MB-answer fault the heap still peaks at about 3 GB between garbage collections: that much data is
coming in. It is all freed afterwards, and the endpoints that don't use the service stay under 400 ms.

## Use it in CI

`./scripts/bench.sh --profile smoke` takes about 4 minutes and exits with 1 when a check fails. Keep `report.md`
as a build artifact. Timing checks depend on the build agent, so compare against a baseline from the same agent
type rather than across machines.
