# Architecture decision records

One file per decision. Status: Proposed → Accepted → Superseded. Never edit an accepted ADR's
decision; write a new ADR that supersedes it. Use `.claude/skills/write-adr` or copy `template.md`.

| # | Decision | Status |
|---|---|---|
| [0001](0001-custom-composition-engine.md) | Build a small composition engine instead of Ocelot/YARP aggregation or GraphQL | Accepted |
| [0002](0002-declarative-recipes-no-scripting.md) | Recipes are declarative JSON with a tiny path language: no scripting | Accepted |
| [0003](0003-infer-dependencies-from-bindings.md) | Infer run order from bindings | Accepted |
| [0004](0004-downstream-auth-as-step-chains.md) | Downstream auth as ordered step chains; secrets by reference | Accepted |
| [0005](0005-caching-off-and-per-caller-by-default.md) | Caching is opt-in and per caller by default | Accepted |
| [0006](0006-drafts-test-gate-append-only-history.md) | Drafts apart from published; test before publish; append-only history | Accepted |
| [0007](0007-file-config-store-first.md) | File config store first, PostgreSQL behind the same interface next | Accepted |
| [0008](0008-blazor-admin-ui.md) | Blazor (interactive server) for the admin UI | Accepted; hosting superseded by 0014 |
| [0009](0009-error-contract-and-logging.md) | Downstream error contract (problem details, codes) and structured logging | Accepted |
| [0010](0010-live-logs-in-admin-ui.md) | Live logs in the admin UI (in-memory buffer, long polling) | Accepted |
| [0011](0011-combining-records-no-fuzzy-matching.md) | Combining records across services by exact key; no fuzzy matching | Accepted |
| [0012](0012-history-entries-who-when-and-unpublish.md) | History entries record who and when; versions never reused; unpublish | Accepted |
| [0013](0013-playwright-for-admin-ui-tests.md) | Playwright for admin UI tests and documentation screenshots (test-only) | Accepted |
| [0014](0014-one-admin-app.md) | One admin app (UI + API), gateway separate; cookie for pages, bearer only for `/admin/*` | Accepted |
| [0015](0015-cors-for-the-gateway.md) | CORS for the gateway, admin-editable within operations-set limits; never credentials | Accepted |
| [0016](0016-try-it-shows-each-call.md) | Try it shows each call's request and response (query values too); never logged or stored | Accepted |
| [0017](0017-overload-protection-and-per-service-circuits.md) | Overload answers 503, one circuit breaker per service, bounded caches; a benchmark checks them | Accepted |
| [0018](0018-service-certificates.md) | Services with their own certificate: trust an exact one by fingerprint; "accept any" in Development only | Accepted |
| [0019](0019-authoring-contract.md) | An authoring contract: recipe JSON Schema, the value language as data, endpoint JSON in and out as drafts, llms.txt | Accepted |
| [0020](0020-activity-log.md) | An append-only activity log of author changes, with how they were made (page, API, MCP) | Accepted |
| [0021](0021-mcp-server-for-ai-agents.md) | An MCP server for AI agents in the admin app: drafts only, no values from real services, no secrets (new dependency) | Accepted |
