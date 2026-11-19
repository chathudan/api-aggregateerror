# 0010 — Live logs in the admin UI

- Status: Accepted
- Date: 2026-11-19

## Context
Technicians and developers want to see what's happening while they build and test endpoints: which
calls run, which fail, and why. They want that without access to servers or a log platform. Our logs
are already structured and free of values (ADR 0009), so showing them to authorised staff is safe.

## Decision
- Each process (gateway, admin API) adds an **in-memory log provider** (`LiveLogProvider`). It keeps
  the last N lines (default 2,000) of our own categories at Information and above, plus warnings
  and errors from framework categories. Each line keeps the fields that matter: time, level, event,
  message, correlation id, recipe, step, service, error code, and exception type and message
  (no stack trace).
- A protected endpoint returns new lines with **long polling** (`?after=<seq>&wait=10`):
  `GET /_admin/logs` on the gateway and `GET /admin/logs` on the admin API. Both require the
  `aggregator.author` or `aggregator.publisher` scope. Polls aren't logged themselves.
- The admin UI **Logs** page merges both sources, and has filters for source, level, text and
  correlation id, plus pause and clear. "Try it" links straight to its own correlation id.
- Turn it off per process with `LiveLogs:Enabled = false`.

> **Note (ADR 0014):** the admin API and UI are now one app. The Logs page reads the admin app's own lines straight
> from its in-memory buffer, and only the gateway's over HTTP (`/_admin/logs`, with the signed-in person's token,
> a dev token in Development, or `Gateway:LogsToken`). `/admin/logs` stays for scripts.

## Consequences
- Instant feedback while building, and an easy way to follow one app request by its correlation id.
- It shows **one process**. With several gateway pods, each pod has its own buffer, and the UI sees
  whichever pod answers. Production still needs a central log store (Azure Monitor, Seq, Elastic);
  this feature doesn't replace it.
- Small, fixed memory cost (about 2,000 short entries per process).
- The same "no values in logs" rule now also protects what staff can see. Any new log line must
  follow it (see `security-review`).

## Alternatives considered
- **SignalR push:** lower latency, but a hub plus connection management per process. Polling with a
  wait of about 10 seconds is near real time and much simpler.
- **Reading a log file:** doesn't work in containers or with several machines, and it's fragile.
- **Embedding Seq or Grafana:** a good production choice, but heavy for local development and QA.
  It can sit alongside this feature later.
