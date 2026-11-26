# Production test stack

**For:** anyone who wants to try the aggregator **as it runs in production** on their own machine, against real
services, starting from an empty configuration.

It runs the gateway and the admin app with `ASPNETCORE_ENVIRONMENT=Production`, so:
- people sign in through an identity provider;
- every address is `https://`;
- certificates are checked;
- secret values come only from settings.

There's no sample data, no mock services, no `/dev/token` and no development sign-in. Keycloak plays the identity
provider.

| Address | What |
|---|---|
| https://localhost:8300 | Admin app (sign in through Keycloak) |
| https://localhost:8200 | Gateway: `/bff/...`, the API docs at `/bff/docs`, `/health/ready` |
| https://localhost:8443 | Keycloak. Its console is at `/admin` |

## Start

```bash
cd deploy/production
./setup.sh                    # once: random passwords in .env, an empty secrets.env
docker compose up --build -d
```

`setup.sh` prints the sign-ins; run it again to see them. Keycloak needs about 20 seconds on the first start.

| User | Can |
|---|---|
| `author` | Services, sign-ins, drafts, Try it |
| `publisher` | The same, plus publish, roll back, delete |
| `admin` | The same, plus browser access (CORS) |

**The certificate.** The first start makes a local test CA in `certs/` (git-ignored). Your browser warns about it
until you trust it. To trust it on a Mac (remove it later in Keychain Access, "API Aggregator local test CA"):

```bash
sudo security add-trusted-cert -d -r trustRoot -k /Library/Keychains/System.keychain certs/ca.crt
```

Without that, use `curl --cacert certs/ca.crt`, or `export CURL_CA_BUNDLE=$PWD/certs/ca.crt`.

## Use it

1. **Add a service** in the admin app (Services → Add service). From the containers, a service on your Mac is at
   `host.docker.internal`, not `localhost`.
2. **Its secrets.** In production the admin app can't store secret values: a sign-in refers to `secret:<name>` and
   the app tells you which values are missing. Add them to `secrets.env`, then run `docker compose up -d`:

   ```bash
   Secrets__crm-password=the-password
   ```

3. **Build, try and publish** an endpoint as usual. For the test app below, give it the scope `bff.read`.
4. **Call it like an app.** `token.sh app` gives a client-credentials token from the `test-app` client:

   ```bash
   curl --cacert certs/ca.crt -H "Authorization: Bearer $(./token.sh app)" https://localhost:8200/bff/<route>
   ```

   For other permissions, create a realm role named like the scope in Keycloak (Realm roles → Create role), then
   assign it to the service account (Clients → `test-app` → Service accounts roles).

**Scripts calling `/admin/*`:** `./token.sh admin [author|publisher|admin]`.

**An AI agent (MCP).** `./token.sh mcp [user]` gives a token from the `aggregator-agent` client. Only that client's
tokens carry `aggregator.mcp`. The stack has real-mode rules: a try shows field names and types, never values. For
Claude Code, which is Node.js, pass the test CA:

```bash
claude mcp add --transport http api-aggregator-prod https://localhost:8300/mcp --header "Authorization: Bearer $(./token.sh mcp)"
NODE_EXTRA_CA_CERTS=$PWD/certs/ca.crt claude
```

Tokens last an hour.

## Stop, or start over

```bash
docker compose down        # stop; your configuration and Keycloak's data are kept (volumes)
docker compose down -v     # also delete them: the next start is empty again, with the realm imported anew
```

Keycloak reads `.env` only when it first imports the realm. After that, change passwords in its console, or start
over with `down -v`. `certs/` stays until you delete it.

## How it fits together

- **Containers.** `init` runs once on every start. It makes the certificates and prepares the empty config volume.
  Keycloak and the admin app share the gateway's network, so `https://localhost:8443` is the same address for your
  browser and for the apps. A token's issuer must match exactly.
- **Trust.** The apps trust the test CA through `SSL_CERT_FILE` (`certs/ca-bundle.crt`, the public CAs plus the test
  CA). This is how they reach Keycloak and your real `https://` services.
- **Volumes.** The gateway reads the config volume read-only. The admin app writes it, and keeps its sign-in cookie
  keys in a volume so a restart doesn't sign people out.
- **Keycloak.** The realm is [`deploy/keycloak/aggregator-realm.json`](../keycloak/aggregator-realm.json), shared with [`deploy/public`](../public/README.md):
  - the roles `aggregator.author`, `aggregator.publisher` and `aggregator.admin`, put into a `roles` claim;
  - `api-aggregator` as the audience;
  - four clients: the admin app, scripts, AI agents and a test app.

## Not production grade

These choices fit a test on one machine. In a real deployment, follow [Operations](../../docs/guides/operations.md):

- The certificates come from a home-made CA, and the private key can be read by every container.
- Keycloak keeps its data in a file database (`KC_DB=dev-file`).
- The `token.sh` users sign in with the password grant.
- The ports are open on this machine only (`127.0.0.1`).
- The admin app shares the gateway's network. In production the admin app must not share the gateway's public
  ingress.
