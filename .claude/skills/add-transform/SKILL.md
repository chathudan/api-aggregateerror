---
name: add-transform
description: Add a value transform (like join, count, default) to the recipe path language in the API Aggregator. Use only when several recipes need the same small value conversion; never for scripting.
---

# Add a transform

ADR 0002 keeps the language small on purpose. Before adding one, confirm that:
- at least two real recipes need it,
- it's a pure function of one value plus an optional string argument,
- it can't run unbounded (no regex from users, no loops over unbounded input).

## Steps

1. `src/ApiAggregator.Engine/Expressions/Transforms.cs`:
   - add the name to `Names`
   - add a case to `Apply`. Return a new node and never mutate the input. Use `?.DeepClone()` when returning a child node.
2. Tests in `tests/ApiAggregator.Engine.Tests/ExpressionTests.cs`: a normal case, a null input, and a wrong-type input.
3. Docs: add it to the transforms row in `docs/architecture/02-recipes.md`.
4. The validator picks up the new name automatically (`Transforms.Names`).
