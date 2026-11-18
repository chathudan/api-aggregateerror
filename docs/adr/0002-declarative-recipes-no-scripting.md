# 0002 — Declarative recipes, no scripting

- Status: Accepted
- Date: 2026-11-18

## Context
QA and technicians author recipes. Recipes run on the request path and may handle personal data.

## Decision
Recipes are JSON validated by `RecipeValidator`. Values are moved with a **tiny path language**
(properties, index, slice, wildcard, quoted names) plus a **fixed whitelist of transforms** (join,
count, first, last, default, upper, lower, trim, string). Conditions support truthy, `==` and `!=`
only. There's no JavaScript, C# scripting, JSONata or arbitrary JSONPath filtering.

## Consequences
- No code injection surface; predictable performance; every recipe can be checked statically.
- Messages and the UI can explain any recipe in plain English.
- Complex reshaping is out of scope. It belongs in a downstream service or a dedicated BFF endpoint.
  Add a transform only when several recipes need it, with tests (skill `.claude/skills/add-transform`).

## Alternatives considered
JSONata or JMESPath (powerful, but hard to validate and explain to non-developers); Roslyn scripting
(a security risk).
