# 0008 — Blazor (interactive server) for the admin UI

- Status: Proposed
- Date: 2026-11-18

## Context
The team is .NET-first. The UI is internal, low traffic, and benefits from reusing the engine's
validator and source catalog so that problems show up instantly.

## Decision
Blazor Web App with interactive server rendering. It references `ApiAggregator.Engine` for models,
`RecipeValidator` and `SourceCatalog`, and calls the admin API for every change.

## Consequences
- One language; the browser shows exactly the validation the server enforces.
- Needs a persistent connection (SignalR). That's fine for an internal tool.
- Production needs OpenID Connect sign-in for the UI user (roadmap). Today Development uses the
  admin API's dev token.

## Alternatives considered
React plus TypeScript (larger skill set across teams, but the validator would be duplicated).
