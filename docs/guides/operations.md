# Operations

**For:** the people who deploy and run the platform.

What runs, how to deploy and configure it, and what to watch. The design reasons are in
[architecture 05](../architecture/05-operations.md). Read [Security and privacy](security-and-privacy.md)
before the first production deployment.

## What runs

| Program | Traffic | Replicas | Exposed to |
|---|---|---|---|
| **Gateway** (`ApiAggregator.Gateway`) | Every app call | 2 or more; stateless | Apps, through your edge (Front Door or Application Gateway with WAF) |
| **Admin API** (`ApiAggregator.Admin.Api`) | Low | 1 | The admin UI only. Internal network |
| **Admin UI** (`ApiAggregator.Admin.UI`) | Low | 1 | Staff only, behind SSO. Internal network |
| Mock services | — | — | **Never deploy.** Development only |

They share the **config store**: today a folder of JSON files, later PostgreSQL ([roadmap](../roadmap.md)).
The admin API writes it, and the gateway reads it and reloads changes within `ReloadCheckSeconds`.

## Deploy with Docker

One `Dockerfile` builds any of the programs. The runtime image is *chiseled*: no shell, no package
manager, non-root, listening on port 8080.

```bash
docker build --build-arg PROJECT=src/ApiAggregator.Gateway/ApiAggregator.Gateway.csproj   -t aggregator-gateway:1.0 .
docker build --build-arg PROJECT=src/ApiAggregator.Admin.Api/ApiAggregator.Admin.Api.csproj -t aggregator-admin-api:1.0 .
docker build --build-arg PROJECT=src/ApiAggregator.Admin.UI/ApiAggregator.Admin.UI.csproj   -t aggregator-admin-ui:1.0 .

docker run -p 8080:8080 \
  -v /srv/aggregator/config:/config:ro \
  -e Aggregator__Config__Directory=/config \
  -e Auth__Authority=https://login.example.com/tenant/v2.0 -e Auth__Audience=api-aggregator \
  aggregator-gateway:1.0 ApiAggregator.Gateway.dll
```

The gateway mounts the config **read-only**. Only the admin API writes it.
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

az containerapp create -g $RG -n aggregator-admin-api --environment $ENV \
  --image $ACR/aggregator-admin-api:1.0 --args "ApiAggregator.Admin.Api.dll" \
  --target-port 8080 --ingress internal --min-replicas 1 --max-replicas 1
```

Then:

- **Config folder.** Mount an Azure Files share at `/config` in both apps: read-only in the gateway,
  read-write in the admin API. Volumes are set in the app's YAML (`az containerapp update --yaml`).
  With more than one gateway replica, all of them read the same share. The PostgreSQL store will replace this.
- **Secrets from Key Vault.** Give each app a managed identity with *Key Vault Secrets User*, then map
  each secret to a setting (see [below](#secrets-and-key-vault)).
- **Health probes.** Liveness `/health/live` and readiness `/health/ready` on port 8080 (in the YAML).
- **Ingress.** The gateway behind Front Door or Application Gateway with WAF. Admin API and UI internal only.
- **Admin UI.** Set `AdminApi__BaseUrl` and `Gateway__BaseUrl` to their internal addresses. Outside
  Development the UI calls the admin API with one configured token (`AdminApi__Token`, from Key Vault)
  until the UI signs users in with OIDC ([roadmap](../roadmap.md)). Put it behind SSO, for example
  Container Apps authentication with Entra ID.

## Configuration reference

Settings come from `appsettings.json`, then environment variables (`:` becomes `__`, for example
`Gateway__RequestsPerMinutePerCaller=1200`), then any other configuration source you add.

| Key | Program | Default | Meaning |
|---|---|---|---|
| `Aggregator:Config:Directory` | gateway, admin API | `../../config` | The config folder |
| `Aggregator:Config:ReloadCheckSeconds` | gateway, admin API | 2 (gateway), 1 (admin API) | How often to check for published changes |
| `Aggregator:Engine:MaxResponseBytes` | gateway | 5242880 (5 MB) | Largest downstream answer accepted |
| `Aggregator:Engine:MaxForEachItems` | gateway | 100 | Most items one "run for each" step may call |
| `Aggregator:Engine:CacheSizeMb` | gateway | 256 | Memory for the response and step caches |
| `Gateway:RequestsPerMinutePerCaller` | gateway | 600 | Rate limit per user (or per IP without a token) |
| `Gateway:OpenApi:Enabled` | gateway | true | Serve `/bff/openapi.json` |
| `Gateway:OpenApi:IncludeInternalDetails` | gateway | false | Show internal service names in the spec. Keep off in production |
| `Gateway:ApiDocs:Enabled` | gateway | true in Development, else false | The Swagger UI page at `/bff/docs` |
| `Auth:Authority` | gateway, admin API | — | Your OpenID Connect issuer |
| `Auth:Audience` | gateway, admin API | `api-aggregator` | The audience tokens must have |
| `Auth:DevSigningKey`, `Auth:DevIssuer` | gateway, admin API | — | **Development only.** Startup fails if set anywhere else |
| `Secrets:<name>` | gateway, admin API | — | The value behind `secret:<name>` |
| `LiveLogs:Enabled`, `LiveLogs:Capacity` | gateway, admin API | true, 2000 | The in-memory lines behind the **Logs** page |
| `Admin:RequireTestBeforePublish` | admin API | true | The publish gate. Never turn it off in production |
| `Admin:AllowedOrigins` | admin API | `http://localhost:5300` | Browser origins allowed to call the admin API |
| `Admin:OpenApiAllowedHosts` | admin API | (any) | Hosts specs may be imported from. Set it in production |
| `AdminApi:BaseUrl`, `Gateway:BaseUrl` | admin UI | `http://localhost:5210/`, `http://localhost:5200/` | Where the UI finds them |
| `AdminApi:Token` | admin UI | — | Token for the admin API outside Development (until OIDC) |
| `Docs:BaseUrl` | admin UI | this repository's `docs/guides/` on GitHub | Where the Help page's **Full guide** links point. Set it to your own git host or docs site |

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
- Load-test with release builds against the real test services before sizing. The numbers in
  [architecture 05](../architecture/05-operations.md#why-its-fast) come from a debug build on one small container.

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
3. Restart the gateway and admin API replicas (or create a new Container Apps revision), so they read the new value.
4. On **Services**, choose **Check sign-in** for that service. For Basic, API key and fixed token, run
   **Try it** on an endpoint that uses it.
5. Ask the owner to retire the old value. Watch for `DownstreamAuthFailed` (event 1200).

Sessions and tokens the gateway already holds stay in memory until they expire. A restart drops them. A
`401` from the service also makes the gateway sign in again.

## Upgrades

1. Back up the config folder.
2. Run `dotnet run --project tools/ApiAggregator.Cli -- validate --config <a copy of the config>` with the
   new version. Exit code 1 means an endpoint no longer validates: fix it first.
3. Deploy the admin API, then the gateway (rolling), then the UI.
4. Check `/health/ready` on each replica, and watch the logs for 15 minutes.
