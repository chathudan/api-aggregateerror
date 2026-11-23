# Operations

**For:** the people who deploy and run the platform.

What runs, how to deploy and configure it, and what to watch. The design reasons are in
[architecture 05](../architecture/05-operations.md). Read [Security and privacy](security-and-privacy.md)
before the first production deployment.

## What runs

| Program | Traffic | Replicas | Exposed to |
|---|---|---|---|
| **Gateway** (`ApiAggregator.Gateway`) | Every app call | 2 or more; stateless | Apps, through your edge (Front Door or Application Gateway with WAF) |
| **Admin app** (`ApiAggregator.Admin`): the pages and `/admin/*` ([ADR 0014](../adr/0014-one-admin-app.md)) | Low | 1 | Staff (pages) and your scripts or CI (`/admin/*`) only. Internal ingress, **never** the gateway's public ingress |
| Mock services | — | — | **Never deploy.** Development only |

They share the **config store**: today a folder of JSON files, later PostgreSQL ([roadmap](../roadmap.md)).
The admin app writes it, and the gateway reads it and reloads changes within `ReloadCheckSeconds`.

## Deploy with Docker

One `Dockerfile` builds any of the programs. The runtime image is *chiseled*: no shell, no package
manager, non-root, listening on port 8080.

```bash
docker build --build-arg PROJECT=src/ApiAggregator.Gateway/ApiAggregator.Gateway.csproj   -t aggregator-gateway:1.0 .
docker build --build-arg PROJECT=src/ApiAggregator.Admin/ApiAggregator.Admin.csproj       -t aggregator-admin:1.0 .

docker run -p 8080:8080 \
  -v /srv/aggregator/config:/config:ro \
  -e Aggregator__Config__Directory=/config \
  -e Auth__Authority=https://login.example.com/tenant/v2.0 -e Auth__Audience=api-aggregator \
  aggregator-gateway:1.0 ApiAggregator.Gateway.dll
```

The gateway mounts the config **read-only**. Only the admin app writes it.
`docker compose up --build` runs the development stack (mocks and dev tokens included). **Don't** use
that file for production.

## Deploy to Azure Container Apps

A starting point. Adapt names, networking and identity to your landing zone.

```bash
RG=rg-aggregator; ENV=cae-aggregator; ACR=myregistry.azurecr.io

az containerapp env create -g $RG -n $ENV --location australiaeast   # add --infrastructure-subnet-resource-id for a private VNet

az containerapp create -g $RG -n aggregator-gateway --environment $ENV \
  --image $ACR/aggregator-gateway:1.0 --args "ApiAggregator.Gateway.dll" \
  --target-port 8080 --ingress external --min-replicas 2 --max-replicas 10 \
  --scale-rule-name http --scale-rule-type http --scale-rule-http-concurrency 100 \
  --env-vars Aggregator__Config__Directory=/config Auth__Authority=https://login.example.com/tenant/v2.0 Auth__Audience=api-aggregator

az containerapp create -g $RG -n aggregator-admin --environment $ENV \
  --image $ACR/aggregator-admin:1.0 --args "ApiAggregator.Admin.dll" \
  --target-port 8080 --ingress internal --min-replicas 1 --max-replicas 1 \
  --env-vars Aggregator__Config__Directory=/config Gateway__BaseUrl=https://<the gateway's internal address>/ \
             Auth__Authority=https://login.example.com/tenant/v2.0 Auth__Audience=api-aggregator \
             Auth__Oidc__Authority=https://login.example.com/tenant/v2.0 Auth__Oidc__ClientId=<admin app client id> \
             Auth__Oidc__ClientSecret=secretref:admin-oidc-client-secret
# Both apps: the same browser-access limits (ADR 0015), for example
#   Gateway__Cors__AllowedOrigins__0=https://portal.example.com Gateway__Cors__AllowedOriginPatterns__0=https://*.example.com
```

Then:

- **Config folder.** Mount an Azure Files share at `/config` in both apps: read-only in the gateway,
  read-write in the admin app. Volumes are set in the app's YAML (`az containerapp update --yaml`).
  With more than one gateway replica, all of them read the same share. The PostgreSQL store will replace this.
- **Secrets from Key Vault.** Give each app a managed identity with *Key Vault Secrets User*, then map
  each secret to a setting (see [below](#secrets-and-key-vault)).
- **Health probes.** Gateway: liveness `/health/live`, readiness `/health/ready`. Admin app: liveness `/health/live`. Port 8080 (in the YAML).
- **Ingress.** The gateway behind Front Door or Application Gateway with WAF. The admin app gets **internal
  ingress only** (`--ingress internal`), reached from the staff network or VPN. It must not share the gateway's
  public ingress: it can change what the gateway serves.
- **Admin sign-in (OpenID Connect).** Register the admin app with your identity provider (for example an Entra ID
  app registration): redirect URI `https://<admin address>/signin-oidc`, two app roles `aggregator.author` and
  `aggregator.publisher`, plus `aggregator.admin` for the few people who manage browser access (CORS), assigned to
  people or groups. Set `Auth:Oidc:Authority`, `Auth:Oidc:ClientId` and
  `Auth:Oidc:ClientSecret` (a Container Apps secret from Key Vault, as above). The app refuses to start outside
  Development without them. Add the gateway's scope to `Auth:Oidc:Scopes` so the Logs page can read the
  gateway's logs with the person's token, or set `Gateway:LogsToken`.
- **Scripts and CI** call `https://<admin address>/admin/*` with a bearer token for `Auth:Audience` that has the
  `aggregator.author` or `aggregator.publisher` scope (`aggregator.admin` for `/admin/cors`). The page cookie never
  works there.
- **Browser access (CORS).** Only for web apps on another origin calling `/bff/*`. Set `Gateway:Cors:*` with the
  **same values on both apps**: the admin app uses them to check what admins add, and the gateway checks every
  entry again (a mismatch can only narrow access). Set `AllowedOriginPatterns` in production, so admins can only
  add your own domains. Invalid values stop the app at startup with a message naming the setting.

## Public sample demo on Vercel

A demo anyone can use, with the **sample data only**: the gateway, the admin app, the mock services and
`samples/config` in one container (`Dockerfile.vercel`, started by `deploy/vercel/start.sh`), behind Caddy on one port
(`deploy/vercel/Caddyfile`: `/bff`, `/dev/token`, `/health` and `/_admin/logs` go to the gateway, everything else to
the admin app). Vercel builds `Dockerfile.vercel` instead of `Dockerfile` when both exist.

> **Not for real services or real data.** Everything runs in Development: `/dev/token` gives anyone a token, the
> signing key is in this repository, and anyone can sign in to the admin app as publisher or admin. That's
> acceptable here only because every record is made up.
>
> **Admin changes don't last.** They're saved inside the container, so they're gone when Vercel stops it (about 5
> minutes without traffic) or on a redeploy, and a second instance has its own copy. The admin page may also
> reconnect now and then (Vercel limits how long a connection stays open). For lasting changes, edit on a developer
> machine, commit `samples/config`, and push.

1. In Vercel, **Add New → Project**, import the repository. Keep the root directory as the repository root.
2. Under **Settings → Environment Variables**, add `PORT` = `8080` (the gateway listens there; Vercel routes to
   `PORT`, 80 by default).
3. If your address isn't `api-aggregateerror-rust.vercel.app`, add `Gateway__BaseUrl` = `https://<your project>.vercel.app/`
   too (the admin app's API docs links and Logs page use it).
4. Deploy. The admin app is at `https://<your project>.vercel.app/` (sign in with any name and a role), the API docs
   at `/bff/docs` (it signs itself in, so **Execute** works).

To call it from a script or Postman, get a token first:
`https://<your project>.vercel.app/dev/token?sub=tester&scope=customer.read%20people.read%20profile.read`.
`GATEWAY=https://<your project>.vercel.app ./scripts/try-gateway.sh` checks every sample endpoint. Health checks
are `/health/live` and `/health/ready`.

To try the same image locally: `docker build -f Dockerfile.vercel -t aggregator-demo . && docker run -p 8080:8080 aggregator-demo`.

## Connect the gateway to your identity provider

Apps call `/bff/*` with an access token from your identity provider (IdP). The gateway trusts **one** IdP for all
endpoints, set with two settings (on the admin app too, for scripts calling `/admin/*`):

| Setting | What it is |
|---|---|
| `Auth:Authority` | The IdP's issuer address. The gateway reads its signing keys from `<Authority>/.well-known/openid-configuration` |
| `Auth:Audience` | The value tokens must have in `aud`: the gateway's own identifier in the IdP |

Each endpoint then says which permission it needs (**Required scope** in the wizard, e.g. `people.read`). The gateway
finds it in the token's `scope`, `scp` or `roles` claim.

### Microsoft Entra ID

1. **App registrations → New registration**: "API Aggregator gateway". No redirect URI.
2. In its **Manifest**, set `"accessTokenAcceptedVersion": 2`, so tokens use the v2 issuer below.
3. **Expose an API**: set the Application ID URI (e.g. `api://api-aggregator`), then **Add a scope** for each
   endpoint permission: `people.read`, `customer.read`, `profile.read`. These are for apps acting for a signed-in user.
4. **App roles → Create app role** with the same names and **Allowed member types: Applications**. These are for
   servers calling with their own token (client credentials); Entra puts them in `roles`.
5. For each app that will call the gateway: **API permissions → Add a permission → My APIs →** the gateway. Pick
   **Delegated** permissions (the scopes) for apps with users, **Application** permissions (the roles) for servers,
   then **Grant admin consent**.
6. Set the gateway:
   - `Auth__Authority=https://login.microsoftonline.com/<tenant id>/v2.0`
   - `Auth__Audience=<the gateway registration's Application (client) ID>` (with v2 tokens, `aud` is this id,
     not the `api://` URI)

### Okta, Keycloak and Auth0

| IdP | `Auth:Authority` | `Auth:Audience` | Where the permissions go |
|---|---|---|---|
| Okta | `https://<your Okta domain>/oauth2/default` (or your authorization server's id) | The authorization server's **Audience** (`api://default` unless changed) | **Scopes** on the authorization server, allowed for each app by an access policy |
| Keycloak | `https://<host>/realms/<realm>` | A client id for the gateway. Add an **audience mapper** to the apps' client scopes so it's in `aud` (by default `aud` is `account`) | **Client scopes** named like the permissions, assigned to the apps |
| Auth0 | `https://<your Auth0 domain>/` (with the slash) | The API's **Identifier** | The API's **Permissions**, granted to each app (machine-to-machine apps under **Machine to Machine Applications**) |

### When a token is refused

| The app gets | Check in the token | Usually |
|---|---|---|
| 401 `unauthenticated` | `iss` equals `Auth:Authority`'s issuer; `aud` equals `Auth:Audience`; `exp` hasn't passed | The app sent a token meant for another audience (its own backend, or Microsoft Graph), or an ID token instead of an access token |
| 403 `insufficient_scope` | The endpoint's scope is in `scp`, `scope` or `roles` | The permission wasn't granted or consented, or the app didn't ask for it |

To look inside a token, decode its middle part on your own machine: `echo '<token>' | cut -d. -f2 | base64 -d`
(add `=` at the end until it decodes). Don't paste real tokens into websites: they give access until they expire.

### Try it on a developer machine

In Development the local token issuer replaces the IdP, so a real token is refused. To test with your IdP, turn the
local issuer off and set the IdP:

```bash
Auth__DevSigningKey= Auth__Authority=<authority> Auth__Audience=<audience> ./scripts/dev-run.sh
```

`/dev/token` and the automatic sign-in on the API docs page don't work in this mode; use **Authorize** with a token
from your IdP.

## Configuration reference

Settings come from `appsettings.json`, then environment variables (`:` becomes `__`, for example
`Gateway__RequestsPerMinutePerCaller=1200`), then any other configuration source you add.

| Key | Program | Default | Meaning |
|---|---|---|---|
| `Aggregator:Config:Directory` | gateway, admin app | `../../config` | The config folder |
| `Aggregator:Config:ReloadCheckSeconds` | gateway, admin app | 2 (gateway), 1 (admin app) | How often to check for published changes |
| `Aggregator:Engine:MaxResponseBytes` | gateway | 5242880 (5 MB) | Largest downstream answer accepted |
| `Aggregator:Engine:MaxForEachItems` | gateway | 100 | Most items one "run for each" step may call |
| `Aggregator:Engine:CacheSizeMb` | gateway | 256 | Memory for the response and step caches |
| `Gateway:RequestsPerMinutePerCaller` | gateway | 600 | Rate limit per user (or per IP without a token: see [Overload and broken services](#overload-and-broken-services)) |
| `Gateway:MaxConcurrentRequests` | gateway | 1000 | Most `/bff` requests answered at once. More get a quick 503 `overloaded` with `Retry-After: 5` ([ADR 0017](../adr/0017-overload-protection-and-per-service-circuits.md)) |
| `Gateway:MaxCallsInFlightPerService` | gateway | 200 | Most calls to one service at once. More fail at once with 502 `unreachable`, so one slow service can't hold the whole gateway |
| `Gateway:Diagnostics:Enabled` | gateway | false | `GET /_admin/runtime`: memory, collections, threads, requests in flight and turned away (numbers only; authors and publishers). Used by the benchmark |
| `Gateway:Diagnostics:AllowForcedGc` | gateway | false | **Development only.** Lets the benchmark force a garbage collection for exact memory readings |
| `Gateway:OpenApi:Enabled` | gateway | true | Serve `/bff/openapi.json` |
| `Gateway:OpenApi:IncludeInternalDetails` | gateway | false | Show internal service names in the spec. Keep off in production |
| `Gateway:ApiDocs:Enabled` | gateway | true in Development, else false | The Swagger UI page at `/bff/docs` |
| `Gateway:Cors:AllowedOrigins` | gateway, admin app (same values) | none; Development: `http://localhost:3000`, `http://localhost:5173` | Websites always allowed to call `/bff/*` from a browser. Exact origins (`https://portal.example.com`, no path, no `*`). Can't be removed in the admin app |
| `Gateway:Cors:AllowedOriginPatterns` | gateway, admin app (same values) | none (admins may add any valid origin) | Limits for origins added in the admin app, for example `https://*.example.com`. A pattern never allows anything by itself. Set it in production |
| `Gateway:Cors:PreflightMaxAgeSeconds` | gateway | 600 | How long browsers remember a preflight answer (0 to 86400). Also how long a removed origin may keep working in a browser that already asked |
| `Auth:Authority` | gateway, admin app | — | Your identity provider, for bearer tokens (apps, and scripts calling `/admin/*`) |
| `Auth:Audience` | gateway, admin app | `api-aggregator` | The audience tokens must have |
| `Auth:DevSigningKey`, `Auth:DevIssuer` | gateway, admin app | — | **Development only.** Startup fails if set anywhere else |
| `Auth:Oidc:Authority`, `Auth:Oidc:ClientId` | admin app | — | Sign-in for the pages. **Required outside Development** (startup fails without them) |
| `Auth:Oidc:ClientSecret` | admin app | — | From Key Vault or user secrets, never in `appsettings.json` |
| `Auth:Oidc:RoleClaim` | admin app | `roles` | The claim holding `aggregator.author` / `aggregator.publisher` |
| `Auth:Oidc:Scopes` | admin app | — | Extra scopes to ask for, for example the gateway's, so Logs can read its lines |
| `Secrets:<name>` | gateway, admin app | — | The value behind `secret:<name>` |
| `LiveLogs:Enabled`, `LiveLogs:Capacity` | gateway, admin app | true, 2000 | The in-memory lines behind the **Logs** page |
| `Admin:RequireTestBeforePublish` | admin app | true | The publish gate. Never turn it off in production |
| `Admin:OpenApiAllowedHosts` | admin app | (any) | Hosts specs may be imported from. Set it in production |
| `Gateway:BaseUrl` | admin app | `http://localhost:5200/` | Where the Logs page reads the gateway's lines, and the API docs links |
| `Gateway:LogsToken` | admin app | — | A token with the author scope for reading the gateway's logs, when the person's own token can't be used. From Key Vault |
| `Docs:BaseUrl` | admin app | this repository's `docs/guides/` on GitHub | Where the Help page's **Full guide** links point. Set it to your own git host or docs site |

## Health checks

| Address | Answers | Use for |
|---|---|---|
| `GET /health/live` | `Healthy` while the process runs | Liveness: restart when it fails |
| `GET /health/ready` | The number of published endpoints and the config version | Readiness: take it out of the load balancer |

<!-- check: gateway GET /health/live token=none -->
```text
Healthy
```

<!-- check: gateway GET /health/ready token=none partial -->
```json
{ "status": "ready", "recipes": 9 }
```

Compare `configVersion` across replicas to confirm they all serve the same configuration.

## Logs and alerts

Outside Development, logs are **JSON on the console**: one object per line, ready for Azure Monitor (Log
Analytics), Seq, Elastic or Loki. Every line carries `CorrelationId`, and composition lines also carry
`RecipeId` and `RecipeVersion`. The events and their fields are listed in
[architecture 05](../architecture/05-operations.md#observability).

Logs **never** contain request or response values, header values, query values, tokens or secrets.
Keep it that way: don't turn on HttpClient request logging, which prints full addresses.

| Alert on | Event | Why |
|---|---|---|
| Any `DownstreamAuthFailed` | 1200 | Expired credentials or a rotated secret. Apps get `auth_failed` until it's fixed |
| `StepFailed` rising, grouped by `ServiceId` and `ErrorCode` | 1101 | A service is failing or slow |
| `ConfigReloadFailed` | 1401 | A bad config file. The gateway keeps serving the last good one |
| p95 of `RequestCompleted.DurationMs` per `Endpoint` | 2000 | Slow endpoints |
| 429 count per `Subject` | 2000 | An app calling in a loop |
| Any 503 | 2000 | The gateway is at `Gateway:MaxConcurrentRequests`: scale out, or find the slow service holding requests |
| Health probe failures | — | The process or its config |

Example Log Analytics query, failures per service in the last hour (column names depend on how your
collector maps the JSON):

```kusto
ContainerAppConsoleLogs_CL
| where TimeGenerated > ago(1h)
| extend e = parse_json(Log_s)
| where toint(e.EventId) == 1101
| summarize failures = count() by service = tostring(e.State.ServiceId), code = tostring(e.State.ErrorCode)
| order by failures desc
```

The admin UI's **Logs** page shows the last 2,000 lines of one process. With several replicas, use the
central store.

## Scaling and performance

- The gateway is **stateless**: scale it out on HTTP concurrency or CPU. Each replica keeps its own
  response cache and sign-in cache, so each signs in to each service once.
- Steps that don't depend on each other run at the same time, so an endpoint takes about as long as its
  slowest *chain* of calls.
- A shared Redis cache across replicas is on the roadmap. Until then, the cache hit rate drops as you add
  replicas.
- Run the [benchmark](benchmarking.md) (`./scripts/bench.sh`) before a release and after changing the engine or the
  gateway's pipeline: it checks the gateway under load, with a broken service, with abusive requests, while endpoints
  are published, and over time (memory). Its numbers compare runs on one machine; size production with a release
  build against the real test services.

## Overload and broken services

The gateway is built to keep answering, quickly, whatever its services do ([ADR 0017](../adr/0017-overload-protection-and-per-service-circuits.md)).

| Situation | What the gateway does | What to watch |
|---|---|---|
| More than `Gateway:MaxConcurrentRequests` requests at once | Answers 503 `overloaded` with `Retry-After: 5` straight away; health checks still answer | 503 count in the request log (event 2000). Scale out, or find the slow service behind it |
| A service is slow, failing or unreachable | Endpoints that **need** it answer 502/504 with the service named; optional steps are left out (a partial answer); other endpoints are unaffected | `StepFailed` (1101) by `ServiceId` |
| A service is slow or sends huge answers | At most `Gateway:MaxCallsInFlightPerService` calls to it at once; more fail straight away (502 `unreachable`), so it can only hold part of the gateway | `StepFailed` by `ServiceId` |
| A service keeps failing | Its circuit breaker opens for a few seconds: calls to it fail at once (502 `unreachable`) instead of waiting. One circuit per service, so others carry on | The same, plus Polly warnings naming the service |
| A safe call (GET) fails briefly | Tried again twice, after 100 ms and 200 ms, within the step's timeout. POST, PUT, PATCH and DELETE are never repeated | — |
| One app calls in a loop | 429 for that caller only, after `Gateway:RequestsPerMinutePerCaller` | 429 count per `Subject` |

Callers **without a token** share one allowance per IP address. Behind a reverse proxy or load balancer every caller
seems to come from the proxy, so enable forwarded headers (`ASPNETCORE_FORWARDEDHEADERS_ENABLED=true`, and trust
only your proxy) or anonymous endpoints are limited together.

## Backup of the configuration

The config folder is the state of the platform: services, sign-ins (with secret **names** only),
published endpoints, drafts and the full history.

| What | How | When |
|---|---|---|
| The config folder (or share) | Azure Files share snapshots, or a nightly copy | Daily, and before every upgrade |
| Changes | Commit `services.json`, `auth-profiles.json` and `recipes/` to a git repository and review them in pull requests | On every publish, if you work that way |
| Key Vault | Soft delete and purge protection on; backups per your policy | Always |

**Don't back up `secrets.local.json`.** It exists only in development. To restore, copy the folder back
and restart (or wait for the reload). History files are written once and never changed, so
the newest backup holds every older version.

## Secrets and Key Vault

Recipes and sign-ins refer to secrets by name (`secret:service-a-password`). The gateway asks for the
setting `Secrets:<name>`. Built-in Key Vault support is on the roadmap. Until then, use one of these:

- **Container Apps secrets with Key Vault references.** No code change:
  ```bash
  az containerapp secret set -g $RG -n aggregator-gateway \
    --secrets "service-a-password=keyvaultref:https://kv-aggregator.vault.azure.net/secrets/service-a-password,identityref:<identity resource id>"
  az containerapp update -g $RG -n aggregator-gateway \
    --set-env-vars "Secrets__service-a-password=secretref:service-a-password"
  ```
- **The Key Vault configuration provider** (a small change in `Program.cs`, with a new package: record it
  in an ADR). It maps the Key Vault secret `Secrets--service-a-password` to `Secrets:service-a-password`.

The admin UI never saves secrets outside Development. It shows the name to add. Don't put secrets in
`appsettings.json`, images or source control.

## Rotating secrets

1. Get the new value from the service owner. Many services allow two valid values while you switch.
2. Add it as a new **version** of the same Key Vault secret. The name stays, so nothing in the config changes.
3. Restart the gateway and admin app replicas (or create a new Container Apps revision), so they read the new value.
4. On **Services**, choose **Check sign-in** for that service. For Basic, API key and fixed token, run
   **Try it** on an endpoint that uses it.
5. Ask the owner to retire the old value. Watch for `DownstreamAuthFailed` (event 1200).

Sessions and tokens the gateway already holds stay in memory until they expire. A restart drops them. A
`401` from the service also makes the gateway sign in again.

## Upgrades

1. Back up the config folder.
2. Run `dotnet run --project tools/ApiAggregator.Cli -- validate --config <a copy of the config>` with the
   new version. Exit code 1 means an endpoint no longer validates: fix it first.
3. Deploy the admin app, then the gateway (rolling).
4. Check `/health/ready` on each replica, and watch the logs for 15 minutes.
