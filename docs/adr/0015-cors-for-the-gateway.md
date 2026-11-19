# 0015 — CORS for the gateway, admin-editable within operations-set limits

- Status: Accepted
- Date: 2026-11-20
- Deciders: Chathura Wijesinghe

## Context
Web apps on their own origin (for example `https://portal.example.com`) call `/bff/*` from the browser. Without
CORS headers the browser sends the request but won't let the page read the answer, so those apps can't use the
gateway. Mobile apps, servers and `curl` aren't affected: CORS is a browser rule.

Which websites need access changes more often than deployments do (a new portal, a test environment), and
waiting for a redeploy to add one slows teams down. At the same time, a careless entry (`*`, a typo, an `http://`
address) would let pages on websites nobody reviewed read personal data that a signed-in user's browser fetches.

## Decision
**CORS on `/bff/{**path}` only**, from one policy named `bff`, built by `BffCorsPolicyProvider`. The admin app,
`/bff/openapi.json`, `/bff/docs`, the health checks and the log endpoints send no CORS headers.

**Allowed origins = what operations set + what admins add, within operations' limits.**

| Source | Where | Who changes it |
|---|---|---|
| `Gateway:Cors:AllowedOrigins` | App settings of **both** apps (the same values) | Operations, by deploying. Can't be removed in the admin app |
| Admin entries | `cors.json` in the config store, with a note and who and when | People with the new **`aggregator.admin`** role, on **Settings → Browser access (CORS)** or `/admin/cors` |
| `Gateway:Cors:AllowedOriginPatterns` (optional) | App settings | Operations. Every admin entry must match one, for example `https://*.example.com`. A pattern never allows anything by itself |

Rules for every origin (`OriginRules`, in the engine, used by the gateway, the admin app and the CLI): exactly
`scheme://host[:port]`, as browsers send it. No path or trailing slash, no default port, no wildcards, no `*`, no
`null`, ASCII host (`xn--` form). `http://` only in Development or for `localhost`. At most 50 admin entries.
Invalid app settings stop the app at startup with a plain message.

**What browsers may send** is worked out from the published recipes, so it is always the smallest set that works:
- methods: those of the published recipes, plus `OPTIONS`;
- request headers: `Authorization`, `Content-Type`, `X-Correlation-Id`, plus every declared `header.*` input;
- headers pages may read: `X-Correlation-Id`, `X-Aggregator-Partial`, `X-Aggregator-Cache`, `Retry-After`;
- **never credentials**: no `Access-Control-Allow-Credentials`. Pages send their token in `Authorization`; cookies
  are never accepted cross-origin;
- preflight answers are cached by browsers for `Gateway:Cors:PreflightMaxAgeSeconds` (600).

**No restart.** `cors.json` is part of the config version, like recipes. The policy is rebuilt when the version
changes, so a published recipe's new header input, or an added or removed origin, applies within
`ReloadCheckSeconds`. The gateway re-checks every admin entry against **its own** settings and skips (and logs, event
2002) any that don't fit, so a mismatch between the two apps' settings can never widen access.

**Pipeline:** routing → CORS → authentication → authorization → rate limiter → endpoints. Preflights are answered
by the CORS middleware (204) and never reach authentication, the rate limiter or the catch-all. CORS headers are
added as the response starts, so every error (400, 401, 403, 404, 429, 500, 502, 504) keeps them and the page can
read the error body. If the config store fails, the last good policy is used, for the same reason.

**History** is append-only, like recipe history (ADR 0012): `config/history/cors/<n>.json`, one entry per added or
removed origin (who, display name, when, the origin and its note), written once before `cors.json` changes; numbers
are never reused.

**Logging:** a rejected origin is logged once a minute per origin (event 2001, Warning), with the origin only, never
the path, query or other headers. It shows on the Logs page.

## Consequences
- Web teams get access in seconds, by someone accountable, and the history says who and why.
- **CORS isn't access control.** It only decides whether a *browser page* may read an answer. Every call still needs
  a valid token with the recipe's scope; `curl` ignores CORS entirely. Docs and the UI say this plainly.
- **Why the blast radius is limited:** no credentials are ever allowed, so a page on an allowed origin only gets what
  it could get by sending a token it already holds; patterns cap which domains admins can add; `*` and `null` are
  impossible; responses stay `no-store`; the admin role is separate and its changes are recorded.
- A new role, `aggregator.admin`, separate from author and publisher (least privilege: managing browser access
  doesn't give publish rights, and publishers don't get it). Identity providers need a third app role.
- Operations must set `Gateway:Cors:*` identically on both apps. A mismatch can only narrow access (the gateway
  re-checks), and the Browser access page shows skipped entries with the reason.
- Changing `cors.json` changes the config version, so response-cache keys change too and cached answers are
  recomputed once. Acceptable: origins change rarely.
- Display names in `cors.json` and its history are personal data, like recipe history: retention is **for review by
  qualified people** ([03](../architecture/03-auth-and-security.md)).

## Alternatives considered
- **Origins in app settings only:** simplest, but every new portal needs a deployment. Kept as the operations source.
- **Allow any origin (`*`) because tokens protect the data:** true for curl, but it lets any website's page use a
  token it obtained (for example through a misconfigured sign-in) and read the answer. Rejected.
- **Allow credentials (cookies):** would let any allowed origin ride a signed-in session (CSRF-like). The gateway
  uses bearer tokens only, so there's nothing to gain. Rejected.
- **The admin app reads the limits from the gateway over HTTP:** one source of truth, but another endpoint to secure
  and a runtime dependency between the apps. Same settings in both, re-checked by the gateway, is simpler.
- **Put it in the reverse proxy (Front Door rules):** works, but admins can't change it and it can't follow the
  recipes' methods and headers.
