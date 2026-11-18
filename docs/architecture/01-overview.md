# 01 — Overview

## Problem

Web and mobile apps need data from several backend services (Service A, B, C…). Calling each one
from the client is slow on mobile networks, leaks internal structure, duplicates auth logic in
every app and makes every screen fragile. We want **one call per screen**, composed on the server.

## What the system does

- Hosts **many aggregated endpoints** (for example `GET /bff/customer/{id}/overview`). Each one is a
  **recipe**: a list of steps that call service endpoints and a template for the response.
- Lets **QA and technicians** build, test and publish recipes in a guided **admin UI**, without code
  and without editing JSON.
- Passes values between calls: one service's response ID can go into another service's path, query,
  header, cookie or body. One step can use values from several earlier steps.
- Signs in to each downstream service the way that service expects: Basic auth, a session login,
  a JWT or OAuth token, an API key, or the caller's own token, in any combination.
- Imports service endpoints from **OpenAPI** specs when a service has one.
- **Caching**, switched on or off per endpoint and per step. Entries are kept per caller by default.

## Goals and non-goals

| Goals | Non-goals |
|---|---|
| Simple, readable code; one obvious place for each concern | A general workflow or ESB engine |
| Fast: parallel calls, pooled connections, no reflection, cached plans | Arbitrary scripting in recipes (no code execution, by design) |
| Secure by default: deny-by-default inputs, per-caller cache, secrets by reference | Replacing an API management product (rate plans, developer portal) |
| Safe change: drafts, test before publish, versioned history, one-click rollback | Transforming binary content (files, images) |

## Containers

```mermaid
flowchart LR
    subgraph Clients
        WEB[Web app]
        MOB[Mobile app]
        QA[QA / technician browser]
    end

    subgraph Aggregator
        GW[Gateway<br/>ASP.NET Core<br/>/bff/*]
        ADM[Admin API<br/>/admin/*]
        UI[Admin UI<br/>Blazor]
        ENG[[Engine library<br/>shared by all]]
        CFG[(Config store<br/>files today<br/>PostgreSQL later)]
        SEC[(Secrets<br/>Key Vault)]
    end

    IDP[Identity provider<br/>OIDC / JWT]
    A[Service A<br/>Basic]
    B[Service B<br/>session login]
    C[Service C<br/>OAuth / JWT]

    WEB -- JWT --> GW
    MOB -- JWT --> GW
    QA --> UI -- JWT --> ADM
    GW -.uses.-> ENG
    ADM -.uses.-> ENG
    GW -- read published --> CFG
    ADM -- read/write drafts, publish --> CFG
    GW --> SEC
    GW --> A & B & C
    WEB & MOB & QA -. sign in .-> IDP
```

| Container | Responsibility | Code |
|---|---|---|
| **Engine** | Recipe model, expression language, planner, validator, executor, downstream auth, caching | `src/ApiAggregator.Engine` (no NuGet dependencies) |
| **Gateway** | Serves **published** recipes under `/bff/*`; inbound JWT, scopes, rate limiting, resilience | `src/ApiAggregator.Gateway` |
| **Admin API** | Services, auth profiles, OpenAPI import, drafts, test runs, publish and rollback | `src/ApiAggregator.Admin.Api` |
| **Admin UI** | The wizard QA and technicians use | `src/ApiAggregator.Admin.UI` |
| **Hosting** | Shared inbound-JWT setup for the two web hosts | `src/ApiAggregator.Hosting` |
| **CLI** | `aggregator validate` / `aggregator run` for QA and CI | `tools/ApiAggregator.Cli` |
| **Mock services** | Services A/B/C with Basic, session and OAuth auth, for local work and demos | `samples/MockServices` |

## Request lifecycle (gateway)

```mermaid
sequenceDiagram
    participant App as Web/mobile app
    participant GW as Gateway
    participant EX as Executor
    participant A as Service A
    participant B as Service B
    participant C as Service C

    App->>GW: GET /bff/customer/C-1/overview (Bearer JWT)
    GW->>GW: validate JWT, scope, rate limit
    GW->>EX: recipe + inputs + caller
    EX->>EX: validate inputs (declared only)
    EX->>EX: response cache? (if enabled, per caller)
    EX->>A: level 1: GET /customers/C-1 (Basic)
    A-->>EX: { id, accountNo, segmentCode }
    EX->>B: level 2: GET /orders?account=… (X-Customer-Id header, session cookie)
    B-->>EX: { tier, items }
    EX->>C: level 3: GET /recommendations?segment=… (X-Tier, Bearer from OAuth)
    C-->>EX: { items }
    EX->>EX: build response from template
    EX-->>GW: 200 (or 200 partial with _errors, or 502)
    GW-->>App: JSON + X-Correlation-Id
```

Steps in the same level run **in parallel**. Levels are worked out from the bindings: if step `b`
reads `a.body.id`, then `b` waits for `a`. Nobody has to wire dependencies by hand.

## Outcomes the caller can see

Every error is RFC 7807 problem details (`application/problem+json`) with a stable `code`, a
`correlationId` and `retryable`. See [ADR 0009](../adr/0009-error-contract-and-logging.md).

| Situation | HTTP | `code` |
|---|---|---|
| Everything succeeded | 200 | — (response template) |
| An **optional** step failed | 200 | `_errors[]` entries in the body, plus `X-Aggregator-Partial: true` |
| A **required** step failed | 502 | Root cause, e.g. `auth_failed`, `unauthorized`, `server_error`, `unreachable` |
| A required step timed out (all root causes) | 504 | `timeout` |
| Inputs missing or invalid | 400 | `invalid_input` |
| No or invalid token | 401 | `unauthenticated` |
| Token lacks the recipe's scope | 403 | `insufficient_scope` |
| No published endpoint at that path | 404 | `not_found` |
| Too many requests from one caller | 429 | `rate_limited` (with `Retry-After`) |
| Unexpected gateway error | 500 | `internal_error` (no exception details) |

Example: the gateway can't sign in to Service B, which a recipe requires.

```http
HTTP/1.1 502 Bad Gateway
Content-Type: application/problem+json
X-Correlation-Id: 6f1c2a9e8b3d4e0f

{
  "type": "urn:api-aggregator:error:auth_failed",
  "title": "The gateway couldn't authenticate with a required service.",
  "status": 502,
  "code": "auth_failed",
  "detail": "Get orders: The gateway couldn't sign in to Service B (orders).",
  "correlationId": "6f1c2a9e8b3d4e0f",
  "retryable": false,
  "errors": [
    { "step": "b", "name": "Get orders", "service": "service-b", "code": "auth_failed",
      "status": null, "message": "The gateway couldn't sign in to Service B (orders).", "retryable": false }
  ]
}
```

The reason it failed (for example, "Login for profile 'service-b-session' failed with 401") goes to
the logs under the same correlation id, and to admin "Try it". Gateway callers never see it.

## Technology

.NET 10 (LTS), ASP.NET Core minimal APIs, System.Text.Json, `IHttpClientFactory` with
`SocketsHttpHandler`, Microsoft.Extensions.Http.Resilience, built-in rate limiting, Blazor
(interactive server) for the UI, Microsoft.OpenApi for imports. PostgreSQL, Redis and Azure Key Vault
are the planned production backing services (see [roadmap](../roadmap.md)).

## Read next

- [02 — Recipes: the composition model](02-recipes.md)
- [03 — Authentication and security](03-auth-and-security.md)
- [04 — Admin UI, lifecycle and OpenAPI import](04-admin-and-openapi.md)
- [05 — Operations, performance and scaling](05-operations.md)
- [06 — Testing](06-testing.md)
- [ADRs](../adr/)
