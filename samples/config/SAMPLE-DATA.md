# Sample data

This folder is the **sample configuration**: invented services, sign-ins and endpoints that run
against the mock services in `samples/MockServices` (http://localhost:5100). The guides in `docs/`
use it for every example, and `scripts/check-docs.sh` runs those examples.

The admin UI shows a **Sample data** badge while it uses this folder (it looks for this file).
Never put real customer or patient data here. For real systems, use `config/` (`./scripts/dev-run.sh --real`).
