# 0014 — One admin app (UI + API), gateway separate

- Status: Accepted
- Date: 2026-11-19
- Deciders: Chathura Wijesinghe
- Supersedes: the hosting part of [ADR 0008](0008-blazor-admin-ui.md) (Blazor stays)

## Context
The admin UI (Blazor Server, :5300) called a separate admin API (:5210) over HTTP for every action. The UI got
its token from `AdminTokenProvider`: a dev token in Development, and one shared `AdminApi:Token` everywhere else.
That meant:

- two apps to deploy, configure and secure for one internal tool, and an HTTP hop on every click;
- every change in history was recorded against the shared token's subject, not the person
  ([ADR 0012](0012-history-entries-who-when-and-unpublish.md) had to call `by` advisory);
- roles were enforced by the API's policies, while the UI only hid buttons. The rules were in `Program.cs`
  lambdas, so nothing else could reuse them.

Blazor Server already runs on the server, next to the engine, so the hop bought no isolation.

## Decision
**One ASP.NET Core app, `src/ApiAggregator.Admin`**: the Blazor pages and the existing `/admin/*` HTTP API in one
process, on port 5300. The gateway stays a separate app (5200).

**A modular monolith with one rule.** `src/ApiAggregator.Admin.Application` holds `AdminService`: every admin use
case (services, sign-ins, secrets, OpenAPI import, schema inference, drafts, validate, Try it, publish, history,
roll back, discard, delete, rename), with all business rules and **all role checks**. It knows nothing about Blazor
or HTTP endpoints: it takes an `AdminActor` (who, and their roles) and returns a value or a plain-English failure.
- Pages call `AdminService` directly, never the store or the engine.
- `/admin/*` endpoints are thin wrappers: bearer token → `AdminActor` → `AdminService` → the same routes, bodies,
  status codes and roles as before.
- `ArchitectureTests` (reflection, no package) fails the build if a page or component holds `IConfigStore`,
  `FileConfigStore`, `CompositionExecutor`, `DownstreamAuthenticator`, `ISecretResolver` or `OpenApiImporter`, or if
  the Application assembly references any `Microsoft.AspNetCore` assembly.

**Two authentication schemes, strictly separated.**

| | Pages | `/admin/*` |
|---|---|---|
| Accepts | The `aggregator.admin` cookie only | A bearer token (JWT) only |
| Set by | Development: a sign-in page (name + role, no password). Otherwise: OpenID Connect (`Auth:Oidc:Authority`, `ClientId`, `ClientSecret`), code flow with PKCE | Your identity provider (`Auth:Authority`, `Auth:Audience`), or `/dev/token` in Development |
| Roles from | Role claims `aggregator.author` / `aggregator.publisher` (dev sign-in, or the provider's app roles via `Auth:Oidc:RoleClaim`) | The token's scopes (`scope`/`scp`) or role claims |
| CSRF | Cookie `HttpOnly`, `SameSite=Lax`, `Secure` outside Development. Sign-in and sign-out are POST forms with an antiforgery token. The Blazor connection (`/_blazor`) is refused when its `Origin` isn't this app | Not exposed: the policies name the bearer scheme only, so a request carrying just the cookie gets 401 |

- Outside Development the app **refuses to start** without `Auth:Oidc:Authority` (and `ClientId`), next to the
  existing `Auth:DevSigningKey` check. The development sign-in is mapped only in Development.
- The person's OIDC access token stays in server memory (`UserTokenStore`, keyed by a random session id in the
  cookie). It's never put in the cookie or the page, and it goes on sign-out. It's used for Try it against
  "caller's own token" services and to read the gateway's live logs. If there's none, the Logs page uses
  `Gateway:LogsToken` or says what to configure. The admin app's own log lines come from its in-memory buffer.
- History records the signed-in person: `by` (their `sub`) and, new, `byName` (their display name). Old entries
  are unchanged (append-only, ADR 0012).

## Consequences
- One deployment, one port, no internal hop; Try it and the pages are faster and simpler.
- The same rule applies everywhere: an author is refused publishing from a page **and** from `/admin/*`
  (`RoleTests`). Pages still hide buttons, but no longer decide.
- History now names real people for UI actions; ADR 0012's shared-token caveat no longer applies to them.
  Scripts calling `/admin/*` are recorded under their token's subject, as before.
- **New runtime dependency:** `Microsoft.AspNetCore.Authentication.OpenIdConnect` 10.0.15 (Microsoft, the same
  line as the other ASP.NET packages; no known advisories when added). Only the admin app uses it.
- `/admin/*` no longer needs CORS (`Admin:AllowedOrigins` is gone): no browser page calls it cross-origin.
- Behaviour changes worth knowing: OpenAPI import errors are now 400 with a plain-English `title` (they used to
  surface as a generic error); a request without a valid antiforgery token gets 400, not 500.
- **Network zone:** the admin app holds the power to change what the gateway serves. It must not share public
  ingress with the gateway: internal ingress, SSO in front ([architecture 05](../architecture/05-operations.md)).
- **Privacy:** history files now hold a display name as well as an id. That's personal data. Retention and access
  for history are **for review by qualified people** ([03](../architecture/03-auth-and-security.md)); this ADR doesn't
  set them.

### When to split again
Split the API back out (keeping `AdminService` as the shared core) if any of these becomes true:
- a **browser-side UI** (Blazor WebAssembly, React) replaces Blazor Server, so the UI no longer runs on the server;
- **heavy external use of `/admin/*`** (pipelines or other teams' tools) needs its own scaling, rate limits or
  release cadence;
- **network-zone requirements** put the API and the pages in different zones (for example the API reachable from
  a build network that must never reach the pages).

## Alternatives considered
- **Keep two apps, add OIDC to the UI and pass the user's token to the API:** keeps the hop and two deployments,
  and still needs the token plumbing this removes.
- **Pages call the store and engine directly, no service layer:** fastest to write, but the rules would be copied
  between pages and endpoints, and roles enforced in two places.
- **One scheme for both (cookie for the API too):** it would expose `/admin/*` to CSRF and mix browser sessions
  with script credentials.
- **NetArchTest for the architecture rule:** a new package for two checks that 40 lines of reflection already cover.
