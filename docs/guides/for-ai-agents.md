# Working with an AI agent (MCP)

An AI agent such as Claude Code or Claude Desktop can do the groundwork of building an endpoint for you:
- import a service from its spec;
- set up its sign-in, with references to secrets only;
- write an aggregated endpoint as a **draft**;
- check the draft and try it.

**You** review it, test it with **Try it** and publish it in the admin app. The agent connects to the admin app's MCP
server at `/mcp`. The design is in [ADR 0021](../adr/0021-mcp-server-for-ai-agents.md).

## What the agent can't do, and why

| It can't | Because |
|---|---|
| Publish, roll back or delete an endpoint | Going live is a person's decision, after they've seen the real answer. There is no tool for it |
| Count its own try as the test publishing needs | A person must use **Try it** on the exact draft and see the real values |
| See values from real services | They can be personal or health data, which must not go to an AI model. Outside the sample data a try shows field names and types only |
| Read or set secret values | Passwords, keys and tokens are set by people in the app. A setting that looks like one is refused, and the agent is told which secret to ask you for |
| Fetch a spec from any address | It gives the spec as text. An address is allowed only on the host of a service a person has set up (not one the agent added), so the admin app can't be made to call anywhere else |
| Change browser access (CORS) | That needs the admin role, and has no tool |

Every change it makes is in the activity log with your name and the client's. The endpoint editor then shows a line
such as "Draft saved by Ann Author via MCP (Claude Code), 3 min ago". When you publish, the history notes "drafted by
… via MCP".

Text inside specs and service answers is data, never instructions. The tools and the server tell the agent so.

## Connect

You need a token with:
- the **author** role (`aggregator.author`), to write drafts;
- the **`aggregator.mcp`** scope, which an administrator grants to the tokens AI agents may use.

On your machine with the sample data, `./scripts/dev-run.sh` gives you one, with the same mechanism as
`try-gateway.sh`. It lasts an hour; run it again when the agent starts getting 401s:

```bash
TOKEN=$(curl -s "http://localhost:5300/dev/token?sub=$USER&scope=aggregator.author%20aggregator.mcp" | sed -E 's/.*"access_token":"([^"]+)".*/\1/')
```

The admin app serves plain `http://localhost:5300` in development. Use exactly that address: `https://`, or your
machine's network address (`192.168…`), won't answer.

**Claude Code:** add the server once, in the repository folder:

```bash
claude mcp add --transport http api-aggregator http://localhost:5300/mcp --header "Authorization: Bearer $TOKEN"
```

Then start `claude`; `/mcp` lists the server and its tools. With a new token, remove and add it again
(`claude mcp remove api-aggregator`). Or share the set-up with your team in a `.mcp.json` file in the repository root
(create it; it isn't in the repository). It reads the token from an environment variable, so the token is never
committed:

```json
{
  "mcpServers": {
    "api-aggregator": {
      "type": "http",
      "url": "http://localhost:5300/mcp",
      "headers": { "Authorization": "Bearer ${AGGREGATOR_TOKEN}" }
    }
  }
}
```

Then `export AGGREGATOR_TOKEN=$TOKEN` before starting `claude`.

**Claude Desktop:** use its configuration file, not **Settings → Connectors → Add custom connector**. Custom
connectors are reached from Anthropic's servers, so they need a public `https://` address with an OAuth sign-in. They
can't reach `localhost` or a private network address, and they can't send a token. In Claude Desktop open
**Settings → Developer → Edit Config** (`claude_desktop_config.json`) and add the server through the `mcp-remote`
bridge (it needs Node.js):

```json
{
  "mcpServers": {
    "api-aggregator": {
      "command": "npx",
      "args": ["-y", "mcp-remote", "http://localhost:5300/mcp", "--header", "Authorization:${AGGREGATOR_AUTH}"],
      "env": { "AGGREGATOR_AUTH": "Bearer <paste the token here>" }
    }
  }
}
```

Restart Claude Desktop. The tools appear under the tools icon in a new chat.

**In test and production**, use a token from your identity provider with the two scopes above; development tokens
only work in Development. The admin app must then be reachable from the agent's machine over `https://`.

Ask it, for example: *"Add an endpoint that lists every unique CRM contact with the notes of those who have orders.
Use the CRM service."*

## The tools

| Tool | What it does |
|---|---|
| `list_services`, `get_service` | Services and their endpoints (method, path, parameters, response fields) |
| `list_sign_ins`, `list_sign_in_kinds` | Sign-ins (secrets as references only) and the kinds with their settings |
| `list_endpoints`, `get_endpoint` | Aggregated endpoints; one as JSON (its draft, else the live version) |
| `get_recipe_schema`, `get_path_language` | The JSON Schema of an endpoint; the value language (paths, the fixed transforms, templates, conditions) |
| `list_source_fields` | What a step can use: inputs, real response fields of earlier steps, lists to run for each item |
| `validate_recipe` | Checks endpoint JSON without saving: `issues` (each with `where`) and `warnings` |
| `import_openapi_service` | Adds or updates a service from its spec text |
| `add_service`, `update_service` | A service without a spec; change name, address, sign-in or time limit |
| `save_sign_in` | A sign-in with `secret:<name>` references only |
| `save_endpoint_draft` | Saves endpoint JSON as a draft, never live |
| `try_endpoint_draft` | Runs a draft once: statuses, timings, errors, field names and types (values with the sample data only) |

## A worked example: unique contacts and their notes

This builds the sample endpoint *contacts-unique* from nothing, as an agent would, against the sample data. Each
step shows what the agent sends and what it gets back. The answers here are real: the documentation tests run them.

**1. The sign-in.** The CRM (`/f` in the mocks) uses Basic. The password is already set as the secret
`service-f-password`, so the agent only refers to it:

```json
[ { "kind": "Basic", "settings": { "username": "svc-f", "password": "secret:service-f-password" } } ]
```

<!-- check: mcp save_sign_in id=crm-agent-signin steps=@block -->
```json
{ "saved": "crm-agent-signin", "missingSecrets": [] }
```

Had it written the password itself, it would have been refused:

```json
[ { "kind": "Basic", "settings": { "username": "svc-f", "password": "pass-f" } } ]
```

<!-- check: mcp save_sign_in id=crm-agent-signin steps=@block expect=400 -->
```text
'password' looks like a password, key or token. Use a reference such as secret:crm-agent-signin-password. Ask a person to set secret 'crm-agent-signin-password' in Services. Secret values never go through MCP.
```

**2. The service, from its spec.** Usually the agent passes the spec's text (`spec_text`). Here the spec is on the
mocks' host, where the sample services were set up by a person, so its address is allowed:

<!-- check: mcp import_openapi_service service_id=crm-agent "name=CRM (added by an agent)" base_url=http://localhost:5100/f sign_in_id=crm-agent-signin spec_url=http://localhost:5100/f/openapi.json -->
```text
"id": "crm-agent",
"authProfileId": "crm-agent-signin",
"id": "listContacts",
"id": "getContactNotes",
"id": "listOrders",
```

**3. The endpoint.** Three steps: the contacts, the orders, and the notes of each contact that has an order. The
agent asks what the notes step can loop over (`list_source_fields` with `before_step=notes`), and finds
`orders.body.orders[*].contactId`:

```json
{
  "id": "contacts-by-agent",
  "name": "Unique contacts and their notes (by an agent)",
  "route": "agent/contacts/unique",
  "requiredScope": "people.read",
  "steps": [
    { "alias": "crm", "displayName": "Contacts from CRM", "endpoint": "crm-agent/listContacts" },
    { "alias": "orders", "displayName": "Orders from CRM", "endpoint": "crm-agent/listOrders" },
    { "alias": "notes", "displayName": "Notes for each contact with an order", "endpoint": "crm-agent/getContactNotes",
      "forEach": "{{orders.body.orders[*].contactId}}", "maxConcurrency": 3,
      "bind": [ { "from": "item", "to": "path.contactId" } ] }
  ],
  "response": {
    "uniqueContacts": "{{crm.body.contacts | distinct('id') | count}}",
    "contacts": "{{crm.body.contacts | distinct('id')}}",
    "notes": "{{notes[*].body}}"
  }
}
```

<!-- check: mcp list_source_fields recipe=@block before_step=notes -->
```text
"expression": "orders.body.orders[*].contactId",
```

**4. Checked, then saved as a draft.** `validate_recipe` lists every problem with where it is. This one has none:

```json
{
  "id": "contacts-by-agent", "name": "Unique contacts and their notes (by an agent)", "route": "agent/contacts/unique",
  "requiredScope": "people.read",
  "steps": [
    { "alias": "crm", "displayName": "Contacts from CRM", "endpoint": "crm-agent/listContacts" },
    { "alias": "orders", "displayName": "Orders from CRM", "endpoint": "crm-agent/listOrders" },
    { "alias": "notes", "displayName": "Notes for each contact with an order", "endpoint": "crm-agent/getContactNotes",
      "forEach": "{{orders.body.orders[*].contactId}}", "maxConcurrency": 3, "bind": [ { "from": "item", "to": "path.contactId" } ] }
  ],
  "response": { "uniqueContacts": "{{crm.body.contacts | distinct('id') | count}}", "contacts": "{{crm.body.contacts | distinct('id')}}", "notes": "{{notes[*].body}}" }
}
```

<!-- check: mcp validate_recipe recipe=@block -->
```json
{ "valid": true, "issues": [], "warnings": [] }
```

The same JSON goes to `save_endpoint_draft`. It comes back as a draft, version 1:

```json
{
  "id": "contacts-by-agent", "name": "Unique contacts and their notes (by an agent)", "route": "agent/contacts/unique",
  "requiredScope": "people.read",
  "steps": [
    { "alias": "crm", "displayName": "Contacts from CRM", "endpoint": "crm-agent/listContacts" },
    { "alias": "orders", "displayName": "Orders from CRM", "endpoint": "crm-agent/listOrders" },
    { "alias": "notes", "displayName": "Notes for each contact with an order", "endpoint": "crm-agent/getContactNotes",
      "forEach": "{{orders.body.orders[*].contactId}}", "maxConcurrency": 3, "bind": [ { "from": "item", "to": "path.contactId" } ] }
  ],
  "response": { "uniqueContacts": "{{crm.body.contacts | distinct('id') | count}}", "contacts": "{{crm.body.contacts | distinct('id')}}", "notes": "{{notes[*].body}}" }
}
```

<!-- check: mcp save_endpoint_draft recipe=@block -->
```text
"id": "contacts-by-agent",
"version": 1,
"status": "draft",
```

**5. Tried.** With the sample data the answer's values are shown; with real services only its field names and
types are (`bodyShape`), with a note saying why:

<!-- check: mcp try_endpoint_draft id=contacts-by-agent partial -->
```json
{ "passed": true, "statusCode": 200, "partial": false, "body": { "uniqueContacts": 7 } }
```

**6. Your turn.** Open the endpoint in the admin app. Its page shows "Draft imported by … via MCP (…)". Read the
draft, use **Try it** with a real record, and publish it if it's right.

## When something's wrong

| The agent reports | What to do |
|---|---|
| 401 from `/mcp` | The token is missing or expired; get a new one |
| 403 from `/mcp` | The token lacks `aggregator.mcp`. Ask an administrator for one that has it |
| 429 | Too many calls in a minute (`Mcp:RequestsPerMinute`, default 120). It slows down by itself |
| "You need the author or publisher role to do this." | The token has `aggregator.mcp` but not `aggregator.author` |
| "Ask a person to set secret '…' in Services." | Set that secret's value in the app (Services → the service → Sign-in), then let the agent carry on |
| "Over MCP, give the spec as text. …" | Give the agent the spec file, or import the spec yourself |
| "Values are hidden: …" | Expected with real services. Use **Try it** in the app to see the values |
| "Something went wrong on the server (reference …)" | Find the reference in the admin app's Logs |
