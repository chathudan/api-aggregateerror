# 0019 — An authoring contract: the recipe schema, the value language as data, endpoint JSON in and out

- Status: Accepted
- Date: 2026-11-24
- Deciders: Chathura Wijesinghe

## Context
Endpoints (recipes) are built in the admin app's wizard. People and tools that write the JSON themselves, and soon AI
agents over MCP (ADR 0021), had nothing to work from:
- what the JSON may contain lived in C# types and doc comments;
- the value language (paths, transforms, templates, conditions) lived in the guides;
- an endpoint could only be checked once saved as a draft;
- endpoint JSON could not be taken out and put back as a draft in one step.

Writers had to guess, and fixed their mistakes by trial.

## Decision
- **The recipe JSON Schema** is generated from the model with the framework's `JsonSchemaExporter`, so there is no
  package and it can't drift from the types:
  - It uses the same JSON form as the config files.
  - Plain-English descriptions are kept in one table (`RecipeSchema`).
  - It is served at `GET /admin/schema/recipe` and printed by `aggregator schema`.
  - It is kept in `docs/schemas/recipe.schema.json`, and a test fails when the copy is out of date.
  - The schema says what is *allowed*; `RecipeValidator` still decides what is *valid* (references, routes, limits).
- **The value language as data** (`PathLanguage`, `Transforms.Described`) at `GET /admin/path-language`: the roots,
  the syntax, every transform with its usage, templates, conditions, binding targets and the fixed rules. It describes
  what already runs; nothing new can run (ADR 0002).
- **Endpoint JSON in and out**, each with a role check in `AdminService` and a thin `/admin/*` wrapper:
  - check JSON without saving it;
  - import JSON as a **draft**, never live, refused when another endpoint answers the same method and route;
  - export the draft, or the live version;
  - list what a step can use for JSON that isn't saved.
- **Ids are checked in `AdminService`**: lowercase letters, digits and dashes, at most 64, for endpoints, services and
  sign-ins. The HTTP filter did this for `/admin/*` only, and other callers (MCP) don't go through it.
- **`/llms.txt`**: a fixed plain-text map of the above for AI tools. It is open, like a help page, and contains
  nothing from the configuration.

## Consequences
- The model and the schema can't disagree. Changing the model means regenerating the copy in the docs, and the test
  says how.
- Nothing here publishes. Drafts are still tested and published by people (ADR 0006, 0012).
- MCP tools (ADR 0021) reuse these methods instead of having their own.

## Alternatives considered
- **A hand-written schema:** rejected. It drifts from the model.
- **A schema package (NJsonSchema and the like):** rejected. The framework's exporter is enough, and it adds no
  dependency.
- **Descriptions as `[Description]` attributes on the model:** rejected for now. Every property would carry two
  copies of its text. One table is easier to keep in plain English.
