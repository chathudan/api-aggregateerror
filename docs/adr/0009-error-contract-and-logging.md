# 0009 — Downstream error contract and structured logging

- Status: Accepted
- Date: 2026-11-19

## Context
When a downstream call fails, and especially when the gateway can't authenticate with a service,
callers got a vague message ("Couldn't sign in to the service.") in an ad-hoc `{ title, errors[] }`
shape. Apps couldn't tell "retry later" from "configuration is broken", and support couldn't link
what an app saw to the gateway's logs. There will be many services and endpoints, so the failure
format must be stable and machine-readable. At the same time, logs must never contain request values
(customer or patient identifiers), tokens or secrets.

## Decision

**1. Every error response is RFC 7807 problem details** (`application/problem+json`) with a stable
`code`, a `correlationId` and a `retryable` flag. That covers downstream failures, invalid input,
and the gateway's own 401, 403, 404, 429 and 500.

**2. Downstream failures are classified** into a fixed set of codes:

| Code | Cause | Retryable |
|---|---|---|
| `auth_failed` | The gateway couldn't sign in: login or token call failed, secret missing, caller token missing for pass-through | No |
| `unauthorized` | The service answered 401 even after a fresh sign-in | No |
| `forbidden` | 403 | No |
| `not_found` | 404 | No |
| `bad_request` | Other 4xx | No |
| `rate_limited` | 429 from the service | Yes |
| `server_error` | 5xx | Yes |
| `timeout` | Step or recipe timeout | Yes |
| `unreachable` | DNS, connection refused, TLS | Yes |
| `invalid_response` | Invalid JSON or too large | No |
| `missing_value` | A value needed for the call was missing or invalid | No |
| `dependency_failed` | Not run because an earlier step failed (never reported as a root cause) | — |

**3. HTTP status for a failed required step:** 504 if every root cause is `timeout`, otherwise 502.
`Retry-After` is sent when the failure is retryable. An optional-step failure still returns 200,
with `_errors` entries of the same shape.

**4. Two levels of detail.** The caller gets a safe `message` that names the service and the kind
of failure. The internal `detail` (for example, which auth profile failed and why) is always logged,
and is returned only in admin "Try it" traces, never to gateway callers.

**5. Structured logging** with source-generated `LoggerMessage` methods (fast, typed) and fixed
event IDs. A log scope carries `CorrelationId`, `RecipeId` and `RecipeVersion` through every line.
There is one request line per gateway call, which logs the recipe id, never the raw path. JSON
console output outside Development. The default HttpClient logging, which prints full URLs including
query values, is removed.

## Consequences
- Apps can branch on `code` and `retryable`; support can search logs by `correlationId`.
- Breaking change to the error body and the `_errors` entries. Acceptable before the first production
  release; any later change to this contract needs a new ADR.
- The error code list is part of the public contract: add codes, never rename them.
- Logs stay free of values. Debugging value-level problems uses admin "Try it", which shows masked
  URLs and internal detail to authorised staff only.

## Alternatives considered
- **Pass the downstream status straight through** (for example, return 401 when a service says
  401). Rejected: it tells the app *its* token is bad, which isn't true, and it leaks internals.
- **Free-text messages only.** Rejected: apps can't act on them, and they get reworded over time.
- **Log full requests at Debug.** Rejected: Debug logging gets switched on in production during
  incidents, and identifiers and tokens would end up in log stores.
