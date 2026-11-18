# 0007 — File config store first, PostgreSQL next

- Status: Accepted
- Date: 2026-11-18

## Context
We want to start simply, review recipe changes in git, and still have a path to multi-instance
production.

## Decision
`IConfigStore` hides storage. `FileConfigStore` (JSON files, atomic writes, reload every 2 s, keeps
the last good snapshot on a broken file) is the first implementation. A PostgreSQL implementation
with the same interface, plus `LISTEN/NOTIFY` for instant reload, is next.

## Consequences
- Zero infrastructure for development, and recipes can be reviewed in pull requests.
- For several gateway pods, share a read-only volume or move to PostgreSQL.
