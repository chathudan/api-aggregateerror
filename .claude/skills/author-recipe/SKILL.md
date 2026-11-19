---
name: author-recipe
description: Write, fix or review an aggregated-endpoint recipe (config/recipes/*.json or a draft) for the API Aggregator, then validate and run it against the services. Use when asked to combine service endpoints into one endpoint, or when a recipe fails validation or testing.
---

# Author a recipe

Reference: `docs/architecture/02-recipes.md`. Example: `samples/config/recipes/customer-overview.json`. Sample recipes live in `samples/config`; real ones in `config/`.

## Steps

1. **Find the endpoints** in `services.json` of the config folder (`samples/config` or `config`) (`serviceId/endpointId`). Note required parameters and their location (path, query or header).
2. **Declare inputs** for everything the caller sends: every `{param}` in `route` needs `route.<param>`. Give ids a `pattern`, and closed lists an `enum`.
3. **Write steps** in a sensible order. Each `bind` item has `from` (or `template`) and `to`:
   - caller values: `request.route.id`, `request.query.status`, `request.header.X-Tenant`, `request.claims.sub`
   - earlier steps: `<alias>.body.<field>`, `<alias>.headers.<Name>`, `<alias>.status`
   - targets: `path.x`, `query.x`, `header.X-Name`, `cookie.x`, `body`, `body.a.b`
   - Don't add `dependsOn`: order is inferred from the bindings.
4. Mark steps whose failure shouldn't break the whole response as `"required": false`.
5. Use `when` and `forEach` only when needed. `forEach` should slice to a small number (`[0:5]`).
6. **Response template:** pick only the fields the app needs. Use `{{alias.body.field}}`.
7. **Caching:** leave it off unless asked. If you turn it on, keep `perCaller: true` for any customer, patient or user data.
8. **Validate:** `dotnet run --project tools/ApiAggregator.Cli -- validate --config samples/config` (or `--config config`), and fix every message.
9. **Run:** start `samples/MockServices` (or point at a test environment), export `Secrets__*`, then
   `dotnet run --project tools/ApiAggregator.Cli -- run <id> --route id=... --repeat 2`. Check each step's trace line.
10. New recipes start as `"status": "draft"` unless the user asks to publish. Publishing normally goes through the admin app (pages or /admin/*), so it's recorded with the publisher's identity.

## Common validator messages

| Message | Fix |
|---|---|
| "uses request input 'query.x', which isn't declared" | Add `query.x` under `inputs` |
| "'x' (path) is required by … but has no value" | Bind something to `path.x` |
| "Header 'Authorization' can't be set here" | Put the auth in the service's auth profile |
| "Steps depend on each other in a loop" | One of the bindings points backwards. Remove it |
