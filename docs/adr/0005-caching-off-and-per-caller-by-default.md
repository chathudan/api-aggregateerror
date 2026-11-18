# 0005 — Caching is opt-in and per caller by default

- Status: Accepted
- Date: 2026-11-18

## Context
Caching makes repeat calls far faster, but a shared cache that ignores the caller can serve one
person's data to another. With health or customer data, that would be a reportable breach.

## Decision
`cache.enabled` is a true/false switch per recipe and per step, **off by default**. When it's on,
keys include the caller's subject unless `perCaller: false` is set explicitly. Keys are SHA-256
hashed and include the recipe version and config version. Only 200 non-partial responses (recipe
level) and 2xx responses (step level) are stored. Memory is bounded. Responses are always sent with
`Cache-Control: no-store`.

## Consequences
- Safe default; an edit can never serve a stale response shape.
- Per-pod caches mean a lower hit ratio with many pods. `IResponseCache` allows a Redis
  implementation later.
- Reviewers must challenge any `perCaller: false` on personal data.
