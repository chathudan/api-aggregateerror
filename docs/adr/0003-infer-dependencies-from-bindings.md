# 0003 — Infer run order from bindings

- Status: Accepted
- Date: 2026-11-18

## Context
Users shouldn't have to manage `dependsOn` lists. Mistakes there either serialize calls (slow) or
start a call before its input exists (broken).

## Decision
`ExecutionPlanner` reads every expression in a step (bindings, templates, `when`, `forEach`). Any
root that names another step is a dependency. Steps are grouped into levels with Kahn's algorithm;
a level runs in parallel. Loops are rejected at validation.

## Consequences
- Maximum safe parallelism with no user effort, and the UI can show "Runs after…".
- A step that should wait for another without using any of its values must reference it, for
  example in `when`. This has been rare in practice; document it if it comes up.
