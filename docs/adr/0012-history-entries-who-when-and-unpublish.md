# 0012 — History entries record who and when; versions are never reused; endpoints can be unpublished

- Status: Accepted
- Date: 2026-11-19
- Deciders: Chathura Wijesinghe

## Context
ADR 0006 made history append-only: every publish writes `config/history/<id>.v<n>.json`, a copy of
the recipe. The admin UI now shows that history, so people need to see **who** published each version
and **when**. They also need to delete (unpublish) an endpoint without losing its history.

Two problems turned up in the existing code:
- The draft's version was "live version + 1". After a roll back, which takes the next number, a later
  publish of that draft wrote the **same** version and silently overwrote a history file.
- After a delete there would be no live version, so the next publish would reuse `v1`.

## Decision
1. A history file is an **entry**, not a bare recipe:
   `{ "id", "version", "action": "published" | "rolledBack" | "unpublished", "at", "by", "restoredFrom", "recipe" }`.
   `by` is the caller's `sub` claim (an id, not a name or email). `at` is UTC.
2. **Unpublishing** removes `config/recipes/<id>.json` and writes `<id>.v<n>.unpublished.json`
   (no recipe content). The draft is discarded. A deleted endpoint is restored by rolling back to any
   version.
3. History files are **write-once**: the store refuses to replace one (`HistoryConflictException`,
   which the admin API turns into 409). The next version is one more than **any** version in the
   history or live, never one taken from the draft.
4. Publishing removes the draft, because it is now the live version. "Unpublished changes" then means
   what it says.
5. Old bare-recipe history files are still read. They show "Not recorded" for who and when, and are
   never rewritten.
6. Roll back validates the old version against today's services, and refuses if it would put a broken
   endpoint live.

`IConfigStore` gains `UnpublishAsync`, `DeleteDraftAsync`, `DeleteServiceAsync` and
`GetHistoryIdsAsync`. `PublishAsync` takes a `ChangeInfo`, and `GetHistoryAsync` returns
`RecipeHistoryEntry`. A future PostgreSQL store implements the same rules: append-only, write-once
entries, a unique (id, version).

## Consequences
- The history is an audit trail of publish, roll back and delete, each with an actor and a time. It
  still grows forever; pruning would need its own decision.
- The actor is only as good as the sign-in. Since [ADR 0014](0014-one-admin-app.md) the pages sign people in
  (development sign-in, or OpenID Connect in production), so UI actions record the real person: `by` is their
  `sub`, and `byName` their display name when the sign-in gives one. The earlier caveat (one shared
  `AdminApi:Token`, so `by` was advisory) no longer applies to UI actions. Calls to `/admin/*` record their
  token's subject, and a shared service token there still names the token, not a person.
- **Privacy:** history files now hold a user id, and since ADR 0014 a display name as well. They live next to the config, contain no request
  data, and are never logged or served by the gateway. Whether actor ids need a retention period, or
  must be kept for a minimum time, is **for review by qualified people**. This ADR doesn't set either.
- Deleting a service is blocked while a published recipe uses it, so a delete can't break live
  traffic. Its auth profile and secrets are kept, because they may be shared.
- Concurrent publishes of one endpoint: one wins and the other gets 409 instead of overwriting.

## Alternatives considered
- **Separate audit log file** (JSON lines) next to unchanged history files: two places to keep in
  step, and the history panel would have to join them.
- **Who and when as fields on `Recipe`:** they'd be copied into the live file and the test hash, and
  every recipe consumer would see audit data it doesn't need.
- **Soft delete flag on the live recipe:** the gateway would have to understand it, and the live
  folder would no longer mean "what's served".
