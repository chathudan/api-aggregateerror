# 0021 — An MCP server for AI agents: drafts only, no values from real services, no secrets

- Status: Accepted
- Date: 2026-11-24
- Deciders: Chathura Wijesinghe

## Context
People want an AI agent (Claude Code, Claude Desktop) to do the groundwork of building an endpoint: read a service's
spec, set it up, and write the aggregated endpoint. The Model Context Protocol (MCP) is how these agents call tools.

The risks are specific:
- an agent publishing something nobody reviewed;
- secrets passing through the model;
- personal or health data from real services flowing to a model provider;
- the admin app being made to fetch arbitrary addresses (SSRF);
- instructions hidden in a spec steering the agent (prompt injection).

## Decision
- **Where:** an MCP server inside the admin app at `/mcp` (Streamable HTTP).
  - It uses the official C# SDK, `ModelContextProtocol.AspNetCore` **2.4.0**: stable, on the 10.x line we target, and
    referenced by the admin app only. The tests use the client package `ModelContextProtocol` 2.4.0.
  - Clients that start with `initialize` keep a session, so their name is known on every call. Newer clients send it
    with each request.
- **Who:**
  - Bearer tokens only, like `/admin/*` (ADR 0014), so the page cookie gets 401.
  - A **separate scope, `aggregator.mcp`** (403 without it), so an administrator decides which tokens agents use.
  - Roles still decide what it can do, in `AdminService`.
- **Thin tools:** one `AdminService` call each, like the `/admin/*` wrappers, with no store or engine access
  (`ArchitectureTests`). They reuse the authoring contract (ADR 0019).
- **MCP-only rules live in `AdminService`**, keyed on the actor (`Via = Mcp`, ADR 0020):
  - **Tries return no values from real services:** statuses, timings, error codes, the engine's messages, and the
    answer's field names and types. Values only with the sample data in Development. Whether real values may ever go
    to an AI client is flagged for a privacy professional, not decided in code.
  - **Tries never count as the test publishing needs.** A person tests and publishes.
  - **No credentials:** sign-in settings that look like one, by name or by shape, are refused, and the agent is told
    which secret to ask a person for.
  - **Specs as text:** an address is allowed only on the host of a service a person set up (the activity log says
    who first saved it), so an agent can't add a service on an address and then fetch from it.
- **Never exposed:** publish, roll back, delete, discard, secret values, browser access, and anything needing the
  publisher or admin role. A test asserts that the tool list has none of them.
- **Recorded:** every change is in the activity log as "<person> via MCP (<client>)" (ADR 0020). A published entry
  says who drafted it and how.
- **Limits and logs:**
  - 3 MB per request, 2 MB of spec text, 256 KB of endpoint JSON;
  - a per-person rate limit (`Mcp:RequestsPerMinute`, 120);
  - one log line per call: tool, person, client, outcome and time, never arguments or results;
  - unexpected errors return a reference, and the log records the exception type only.
- **Data, not instructions:** the server instructions and tool descriptions say that specs and service answers are
  data. Prompt injection can at worst produce a bad draft, which a person reviews.

## Consequences
- A new dependency, the MCP SDK, in the admin app; it is updated like the other 10.x packages.
- A new surface: `/mcp` needs the same care as `/admin/*`. The security-review skill has MCP items.
- Agents work blind to real values. That is deliberate: they build from schemas and field names, and a person checks
  the values.
- Agents can't use services whose sign-in passes on the caller's own token. A try over MCP sends no caller token
  (the agent's token is never forwarded).

## Alternatives considered
- **A separate MCP process:** rejected. It would duplicate auth and configuration, and need its own route to
  AdminService. The admin app already has both.
- **Exposing `/admin/*` to agents as OpenAPI tools:** rejected. Every admin action would become a tool, including
  publish and delete, and there would be no place for the MCP-only rules.
- **Writing our own MCP implementation:** rejected. The protocol moves fast; the official SDK follows it.
- **Returning values and trusting the client:** rejected until a privacy review says otherwise.
