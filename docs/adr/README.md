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
| [0008](0008-blazor-admin-ui.md) | Blazor (interactive server) for the admin UI | Proposed |
| [0009](0009-error-contract-and-logging.md) | Downstream error contract (problem details, codes) and structured logging | Accepted |
| [0010](0010-live-logs-in-admin-ui.md) | Live logs in the admin UI (in-memory buffer, long polling) | Accepted |
