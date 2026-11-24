# 0020 — An append-only activity log of author changes, with how they were made

- Status: Accepted
- Date: 2026-11-24
- Deciders: Chathura Wijesinghe

## Context
History (ADR 0012) records what goes live: publish, roll back and unpublish, with who and when. Author changes have
no history:
- saving or importing a draft;
- discarding a draft;
- saving a service or a sign-in;
- deleting a service.

The only trace of who did them is a few log lines. With AI agents able to make these changes over MCP (ADR 0021),
people need to see what an agent changed before they publish it. We also want to know how a change was made: a
page, a script on `/admin/*`, or an agent.

## Decision
- **An activity log**:
  - Each entry records the time, who (subject and display name), how (`page`, `api`, `mcp`, plus the MCP client's
    self-reported name), what was done, the kind (`endpoint`, `service`, `sign-in`, `secret`) and the id.
  - **Never contents, never values**, not even secret names' values. What changed is in the files, and once
    published in the history.
- **Append-only**:
  - The file store writes one JSON line per entry to `history/activity.jsonl` and never rewrites it.
  - A line cut short by a crash is skipped when reading.
  - The file is not a `*.v*.json`, so it is never mistaken for a version.
  - `IConfigStore` gains `AppendActivityAsync` and `GetActivityAsync`, so other stores must keep it too.
- **`AdminService` writes it.** Every author change records an entry after it is saved. If the log can't be
  written, the change stands and an error is logged: losing an audit line is serious, but undoing a saved change
  over it would be worse.
- **How a change was made** is part of the actor:
  - `AdminActor.Via` and `Client`;
  - pages are `page`, `/admin/*` sets `api`, and MCP sets `mcp` with the client's name;
  - the client name is a label only and never decides anything, because clients name themselves.
- **History says it too:**
  - Publish, roll back and unpublish entries record `via`.
  - A published entry records `drafted`: who last saved the draft, and how. The History panel then shows "drafted by
    Ann Author via MCP (Claude Code)".
- **Shown where people decide:**
  - the endpoint editor shows the newest change under the address;
  - the Services page shows the newest change per service;
  - `GET /admin/activity` lists them.

## Consequences
- A publisher sees whether a draft came from an agent before publishing it.
- The file grows with every change. Reading it scans the file, which is fine for the file store's size of
  installation; a database store would index it.
- The activity log is an audit record, so it is never pruned by the app. Retention is an operations decision (see
  operations).

## Alternatives considered
- **Version history for drafts:** rejected. Drafts change often and would flood the history people use to roll back.
- **Logs only:** rejected. Logs are for operations, can be sampled or rotated, and people in the app can't see them.
- **Writing the change and the log entry atomically:** not possible across files without a database. The order
  (change, then entry) and the error log are the compromise.
