# API Aggregator documentation

Start with [Getting started](guides/getting-started.md): 10 minutes from clone to calling a published
endpoint. Then pick what describes you.

## I'm a technician or tester

You connect services and build, test and publish aggregated endpoints in the admin UI. No coding needed.

1. [Getting started](guides/getting-started.md): run everything with the sample data
2. [Adding services](guides/adding-services.md): from a spec or by hand, what to ask the owner, every sign-in type, secrets
3. [Building endpoints](guides/building-endpoints.md): passing values between calls, inputs, conditions, run for each, caching
4. [Combine lists](guides/combine-lists.md): one list of records from several services, without guessing
5. [Testing and publishing](guides/testing-and-publishing.md): Try it, API docs, Postman, history and rolling back
6. [Troubleshooting](guides/troubleshooting.md): every message, what it means and what to do

The admin UI's **Help** has the same steps, shorter: a page per task (*Add a service · Build an endpoint · Test and
publish · Fix a problem*) and a few reference pages. Every screen links to its page, and each page links back here
(**Full guide**). New to it? **Build your first endpoint with the guide** walks you through one in the wizard.

## I'm building an app

You call the gateway from a web or mobile app.

- [For app developers](guides/for-app-developers.md): calling `/bff/…`, sign-in and scopes, every error
  code and what to do, partial answers, the correlation id, generating clients, C# and TypeScript examples
- [Testing and publishing](guides/testing-and-publishing.md#test-through-the-api-docs-swagger): try endpoints in the API docs or Postman
- [Troubleshooting: what apps report](guides/troubleshooting.md#what-apps-report)

## I run the platform

You deploy, configure, monitor and secure it.

- [Operations](guides/operations.md): Docker and Azure Container Apps, a public sample demo on Vercel, connecting your identity provider
  (Entra ID, Okta, Keycloak, Auth0), the configuration reference,
  health checks, logs and alerts, scaling, backups, Key Vault and secret rotation
- [Benchmarking](guides/benchmarking.md): load, broken services, abusive requests, publishing under load, memory
  over time: run it before a release, read the report
- [Security and privacy](guides/security-and-privacy.md): what's protected, what's never logged, and what
  your security and privacy people must review
- [Roadmap](roadmap.md): what's needed before production

## I'm changing the code

- [`CLAUDE.md`](../CLAUDE.md): principles, security invariants, layout, commands and conventions
- Architecture, the design record:
  [01 Overview](architecture/01-overview.md) ·
  [02 Recipes](architecture/02-recipes.md) ·
  [03 Auth and security](architecture/03-auth-and-security.md) ·
  [04 Admin and OpenAPI](architecture/04-admin-and-openapi.md) ·
  [05 Operations](architecture/05-operations.md) ·
  [06 Testing](architecture/06-testing.md)
- [Architecture decision records](adr/README.md): why things are the way they are
- [Sample services and data](../samples/README.md): the mocks and what each sample endpoint shows

## For everyone

- [Glossary](guides/glossary.md): the words used in the UI and these guides
- [FAQ](guides/faq.md)

## How these guides stay correct

Every response shown in the guides is real. The examples run against the mock services with the sample
configuration in [`samples/config/`](../samples/config/). `./scripts/check-docs.sh` runs each example,
with the `aggregator` command-line tool or through the gateway, and compares the output with the guide.
It also runs in `dotnet test` (`tests/ApiAggregator.Docs.Tests`), so a change that alters an answer fails
the build until the guide is updated.

To add a checked example, put a comment before the code block that shows the output:

````markdown
<!-- check: run customer-summary --route id=C-10042 -->
```json
{ "name": "Alex Example", ... }
```
````

| Check | Runs |
|---|---|
| `run <endpoint> [--route k=v] [--query k=v] [--header k=v] [--token t] [--repeat n]` | `aggregator run` against the mocks |
| `validate` | `aggregator validate` |
| `gateway GET <path> [scope=a,b] [token=none] [header:Name=value]` | A call through the gateway with a test token (scope `customer.read` if not given) |

Add `expect=502` for an answer that isn't 200, and `partial` when the block shows only some fields of each
object. A `json` block is compared as JSON; correlation ids may differ. A `text` block's lines must appear
in that order; timings and spacing are ignored.

Screenshots in [`images/`](images/) are refreshed with `./scripts/screenshots.sh`.
