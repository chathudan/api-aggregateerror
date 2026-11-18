# 0006 — Drafts apart from published; test before publish; append-only history

- Status: Accepted
- Date: 2026-11-18

## Context
Non-developers change live endpoints. A mistake must not reach callers, and if one does it must be
reversible at once.

## Decision
Drafts (`config/drafts`) are never served. Publishing needs the `aggregator.publisher` scope **and**
a passing test of the exact draft content (by hash). Every published version is written to
`config/history`. Rollback republishes old content as a **new** version.

## Consequences
- A safe workflow with a clear audit trail.
- The passing-test record is in memory in the admin API today, so it resets on restart (move it to
  the database store).
