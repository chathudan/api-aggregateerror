---
name: write-adr
description: Record an architecture decision for the API Aggregator as a numbered ADR in docs/adr. Use when a change adds a dependency, changes a security invariant, changes storage or hosting, or picks between real alternatives.
---

# Write an ADR

1. Find the next number in `docs/adr/` (four digits).
2. Copy `docs/adr/template.md` to `docs/adr/NNNN-short-slug.md`.
3. Fill in Context, Decision, Consequences (including the **security and privacy impact**) and Alternatives considered. Keep it to about one page; link to code and docs.
4. Status is `Proposed` until the user confirms, then `Accepted`. To reverse an accepted decision, write a new ADR that supersedes it, and change only the old one's status line.
5. Add a row to the table in `docs/adr/README.md`.
6. If it changes an invariant, update `CLAUDE.md` (Security invariants) and the matching architecture doc in the same change.
7. Never present regulatory or clinical requirements as settled facts. List them as items for review by qualified people.
