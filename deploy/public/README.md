# Public demonstration server

**For:** whoever sets up a server so other people can see the aggregator working and try it.

This runs the gateway, the admin app and Keycloak in production mode on one server, starting from an empty
configuration. Caddy sits in front and gets free Let's Encrypt certificates. People sign in with accounts you give
them, each with a role. Everything they do is in the activity log under their name.

| Address | What |
|---|---|
| `https://admin.<domain>` | Admin app: build, try and publish endpoints |
| `https://api.<domain>` | Gateway: `/bff/...`, the API docs at `/bff/docs` |
| `https://auth.<domain>` | Keycloak: sign-in pages. Its console is at `/admin` |

> **Made-up data only.** Everyone you invite sees the values services return in **Try it**. Use the mock services
> (below) or test systems with invented records, never real personal or health data. Whether a demonstration may
> use any real data, and under what agreement, is a question for your privacy officer, not for this setup.

## What you need

- A Linux server with Docker and the Compose plugin, and at least 2 GB of memory and 10 GB of disk.
- Ports **80 and 443** open to the internet. Let's Encrypt checks the names over port 80.
- A domain where you can add DNS records. Point three names at the server's public address:

  ```text
  admin.demo.example.com   A   <server address>
  api.demo.example.com     A   <server address>
  auth.demo.example.com    A   <server address>
  ```

## Start

On the server:

```bash
git clone <this repository> api-aggregator && cd api-aggregator/deploy/public
./setup.sh demo.example.com you@example.com    # the addresses, random passwords (.env), an empty secrets.env
docker compose up --build -d                   # add --profile mocks for the made-up services (below)
docker compose logs -f caddy                   # watch the certificates arrive (a minute or so)
```

`setup.sh` prints the built-in sign-ins (`author`, `publisher`, `admin`) and the Keycloak console's password; run
it again to see them. Keep those for yourself.

## Invite people

Give each person their own account, so the activity log shows who did what:

```bash
./add-user.sh ann author "Ann Smith" ann@example.com
./add-user.sh raj publisher "Raj Patel"
```

The script prints a one-time password. Send it privately; they choose their own password when they first sign in.

| Role | Can |
|---|---|
| `author` | Add services and sign-ins, build drafts, use **Try it** |
| `publisher` | The same, plus publish, roll back and delete endpoints |
| `admin` | The same, plus browser access (CORS) |

For most visitors, `author` is enough: they can build and try everything, and you publish. To remove someone, use
the Keycloak console: Users → the person → Delete, or turn off **Enabled**.

## A demonstration script

1. **Add a service.** With `--profile mocks`, add a service with the spec address `http://mocks:5100/d/openapi.json`
   (sign-in: API key, header `X-Api-Key`). Its secret's value goes in `secrets.env`:

   ```bash
   Secrets__<the name the app shows>=key-d
   ```

   Then run `docker compose up -d`. The mocks are explained in [samples/README.md](../../samples/README.md). For
   your own test systems, use their real addresses.
2. **Build an endpoint** in the wizard, then **Try it** and **Publish**. Publishing needs a publisher.
3. **Call it like an app.** `./token.sh app` gives a token from the `test-app` client, which has the permission
   `bff.read`, so give the endpoint that scope:

   ```bash
   curl -H "Authorization: Bearer $(./token.sh app)" https://api.demo.example.com/bff/<route>
   ```

   Or open `https://api.<domain>/bff/docs`, choose **Authorize** and paste the token.
4. **Show the record.** Endpoint history (who published what, and when), and the activity log line on each page.

## AI agents (MCP)

Someone with an account can connect Claude Code from their own machine. The token comes from the `aggregator-agent`
client; only its tokens may use `/mcp`. Here the agent sees field names and types, never values:

```bash
TOKEN=$(curl -s https://auth.demo.example.com/realms/aggregator/protocol/openid-connect/token \
  -d grant_type=password -d client_id=aggregator-agent -d username=ann --data-urlencode "password=<their password>" \
  | sed -E 's/.*"access_token":"([^"]+)".*/\1/')
claude mcp add --transport http api-aggregator https://admin.demo.example.com/mcp --header "Authorization: Bearer $TOKEN"
```

People must sign in once in the browser first, to replace the one-time password. Tokens last an hour. Claude
Desktop's **Add custom connector** dialog doesn't work yet: `/mcp` doesn't publish the OAuth details it needs.

## Look after it

| Task | Command |
|---|---|
| Update to the latest code | `git pull && docker compose up --build -d` |
| See what's running, and logs | `docker compose ps`, `docker compose logs -f admin` |
| Stop (data kept) | `docker compose down` |
| Back up | Copy the `config`, `keycloak` and `caddy-data` volumes, for example with `docker run --rm -v aggregator-public_config:/v -v $PWD:/b alpine tar czf /b/config.tgz -C /v .` |
| Start over, empty | `docker compose down -v`. This also deletes the certificates; Let's Encrypt limits how often you can get new ones |

## How it fits together

- **Caddy** is the only thing reachable from outside. It answers on ports 80 and 443, gets and renews the
  certificates, and passes requests to the apps over the internal network. The apps take the real scheme and
  caller address from Caddy's `X-Forwarded-*` headers.
- **The same addresses inside and out.** The apps reach Keycloak by its public name, which points to Caddy inside the
  network. So a token's issuer is identical for browsers and apps, with a real certificate.
- **Keycloak.** The realm is [`deploy/keycloak/aggregator-realm.json`](../keycloak/aggregator-realm.json), the same
  as in [`deploy/production`](../production/README.md). It turns on brute-force protection.

## Not a production deployment

This server is for demonstrations. For real use, follow [Operations](../../docs/guides/operations.md):

- **Admin app.** It's on the public internet here. In production it gets internal ingress only.
- **Keycloak.** It keeps its data in a file database (`KC_DB=dev-file`). Its console is public, protected only by
  its password.
- **Secrets.** They sit in a file on the server, not in a key vault.
- **One server.** The gateway runs as a single copy, and backups are by hand.
