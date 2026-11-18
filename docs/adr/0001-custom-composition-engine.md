# 0001 — Build a small composition engine

- Status: Accepted
- Date: 2026-11-18

## Context
We need many aggregated endpoints that non-developers can build. Each one calls several services,
passes IDs between calls (path, query, header, body), runs steps in parallel where possible, signs in
to each service differently (Basic, session, JWT/OAuth, API key) and is published without a redeploy.

## Decision
Write a focused engine in C# (`ApiAggregator.Engine`) with **no NuGet dependencies**: a recipe model,
a small path language, a planner, a validator and an executor on top of `IHttpClientFactory`. Host it
in a minimal ASP.NET Core gateway.

## Consequences
- We own the code: the engine is about 2,500 lines (half of it comments), readable and unit-tested.
- Behaviour such as failure semantics, caching and auth is exactly what we need, with no workarounds.
- We must maintain it, and keep the feature set deliberately small (see 0002).

## Alternatives considered
- **Ocelot aggregates:** Ocelot can merge responses from several routes, but passing one service's
  response values into another call (or a custom aggregator for each case), mixed auth per service
  and editing by QA without a redeploy all need custom code anyway.
- **YARP:** an excellent reverse proxy, but it doesn't compose responses.
- **GraphQL federation (e.g. Hot Chocolate Fusion):** powerful, but every service needs a GraphQL
  schema or a hand-written adapter, and QA can't author it.
- **Commercial API management policies:** XML policy authoring isn't QA-friendly, and testing and
  versioning are harder. Still a good front door in front of our gateway.
