# 04 — Admin UI, lifecycle and OpenAPI import

## In-app help

The admin UI has a **Help** page (`/help`) written for technicians: what to ask a service owner,
which sign-in to pick, how secrets are handled, building and publishing, a troubleshooting table of
every common message, and the error codes apps receive. Each screen links to the matching section.

## Who does what

| Person | Can | Scope in token |
|---|---|---|
| Developer / admin | Register services, set up auth profiles and secret references | `aggregator.author` (profiles and services) |
| QA / technician | Build aggregated endpoints in the wizard, run "Try it", save drafts | `aggregator.author` |
| Approver | Publish and roll back | `aggregator.publisher` |

Technicians **never see credentials**. They pick services that are already set up.

## Lifecycle of an aggregated endpoint

```mermaid
stateDiagram-v2
    [*] --> Draft: Create endpoint
    Draft --> Draft: Edit and save
    Draft --> Tested: Try it passes (HTTP 200, not partial)
    Tested --> Draft: Any change (the test no longer matches)
    Tested --> Published: Publish (publisher role)
    Published --> Draft: Edit (live version keeps serving)
    Published --> Published: Roll back (republished as a new version)
```

- Drafts live in `config/drafts/` and are **never served**. The live version keeps serving while a
  draft is edited.
- **Publish requires a passing test of the exact draft.** The admin API hashes the recipe content,
  so any change after the test means running it again. You can turn this off with
  `Admin:RequireTestBeforePublish = false`, but don't do that in production.
- Publishing writes `config/recipes/<id>.json` and `config/history/<id>.v<n>.json`. The gateway
  picks it up within `ReloadCheckSeconds` (2 s) with no restart.
- **Rollback** republishes an old version's content as a **new** version, so history only ever grows.

## Adding a service (Services page → Add service)

| Path | What the user does |
|---|---|
| **From an OpenAPI spec** (recommended) | Paste the spec address and choose **Load**. The name, base address, operations and a suggested sign-in are filled in. Untick operations you don't need, complete the sign-in and save |
| **Enter by hand** | Name and base address, then sign-in. Add endpoints afterwards with **Add endpoint**: method, path (path values such as `{orderId}` are detected), extra query or header values, and optionally a **sample response** so the wizard can offer its fields |

**Sign-in** is picked from cards in plain words: username and password, log in first and reuse the
session, OAuth client (JWT), API key, fixed token, the caller's own token, or none. **Combine with
another sign-in step** chains several. **Check sign-in** logs in or gets a token once, so problems
show up before anyone builds an endpoint.

**Secrets typed in the UI**
- *Development:* saved to `config/secrets.local.json`, which git ignores. The auth profile only
  stores `secret:<service>-<field>`. The gateway and admin API read the file directly whenever it
  has changed (`LocalSecretsResolver`), so a value works on the very next call, including **Check sign-in**.
- *Other environments:* the UI never sends the value. It shows the secret **name** to add in Key
  Vault, and `PUT /admin/secrets/{name}` is refused.

## The wizard

| Screen | What the user does | What keeps it simple |
|---|---|---|
| 1. Endpoint | Name, method, route, sign-in required, scope, cache on/off | The id is generated from the name; the address preview updates as you type |
| 2. Inputs | Caller inputs and their types | Route `{params}` are added automatically; test values sit next to each input |
| 3. Steps | **Add step** opens a searchable list of every service endpoint; pick one | The step is named after the endpoint, and every value is **pre-filled** where a match exists (`AutoBinder`): a same-named input, a same-named field from an earlier step, `<thing>Id` → the id of an earlier step about `<thing>`, or the route value for the first step. Pre-filled values carry a "suggested" tag until changed. "Runs after…" is computed automatically. Advanced (required, when, for each, cache) is hidden by default |
| 4. Response | **Add fields from <step>** in one click, rename or remove; or leave it empty | With no fields, each step's body is returned under its name |
| 5. Test and publish | Try it with test values; see each call's status, time and URL (query values masked); publish | The same validator as the server runs in the browser, so problems show up immediately in plain English |

## OpenAPI import

`POST /admin/services/{id}/openapi/preview` → `POST /admin/services/{id}/openapi/import`

| Step | Detail |
|---|---|
| Source | A spec URL (http/https, no redirects, 10 s timeout, 5 MB cap, optional host allow-list) or pasted text |
| Versions | Swagger 2.0, OpenAPI 3.0, 3.1 (Microsoft.OpenApi 1.6.x), JSON or YAML |
| Pick | The preview lists every operation. You import only the ones you tick |
| Extracted | Method, path, `operationId` (generated if missing), parameters with location and required flag, JSON request and 2xx response schemas (local `$ref`s resolved) |
| Auth hints | `securitySchemes` map to a suggested auth kind: `http/basic` → Basic, `http/bearer` → StaticBearer or PassThrough, `apiKey` → ApiKey, `oauth2`/`openIdConnect` → OAuthClientCredentials |
| Re-import | Endpoints are matched on `operationId`. They're updated, not duplicated, and a manual auth override is kept |

**Services without a spec:** add endpoints by hand in `services.json` or with
`PUT /admin/services/{id}`. To get autocomplete, paste a sample response into
`POST /admin/schema/infer` and store the returned schema as `responseSchema`.

**Gateway spec:** `GET /bff/openapi.json` describes every **published** aggregated endpoint, so web
and mobile teams can generate typed clients (NSwag or Kiota for .NET MAUI, `openapi-typescript`
for web).

## Admin API reference

| Method and path | Role | Purpose |
|---|---|---|
| `GET /admin/services`, `PUT /admin/services/{id}` | author | List and save services |
| `POST /admin/services/{id}/openapi/preview` · `/import` | author | OpenAPI import |
| `GET /admin/capabilities` | author | Whether secrets can be saved here; auth kinds |
| `PUT /admin/secrets/{name}`, `GET /admin/secrets/{name}` | author | Development only: save a secret value; check that one exists (values never come back) |
| `POST /admin/auth-profiles/{id}/check` | author | Sign in once and report the result |
| `GET /admin/auth-kinds` | author | Available auth kinds and their required settings |
| `GET /admin/auth-profiles`, `PUT /admin/auth-profiles/{id}` | author | Profiles (plain-text secrets are rejected) |
| `POST /admin/schema/infer` | author | Sample response → schema |
| `GET /admin/logs?after=&wait=` | author | Recent admin API log lines (long poll). The gateway has the same at `/_admin/logs` |
| `GET /admin/recipes`, `GET/PUT /admin/recipes/{id}` | author | List, read and save drafts |
| `POST /admin/recipes/{id}/validate` | author | Plain-English issues |
| `GET /admin/recipes/{id}/sources?beforeStep=x` | author | Options for the "use value from" dropdown |
| `POST /admin/recipes/{id}/test` | author | Run the draft (no cache, with trace) |
| `POST /admin/recipes/{id}/publish` | **publisher** | Make it live |
| `GET /admin/recipes/{id}/history`, `POST /admin/recipes/{id}/rollback/{v}` | **publisher** for rollback | History and rollback |
