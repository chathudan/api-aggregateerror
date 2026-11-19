# Frequently asked questions

## Building endpoints

**Do I need to write code?**
No. Services and endpoints are built in the admin UI and saved as JSON files. There's no scripting, on
purpose: a fixed path language and a fixed list of transforms keep endpoints safe and reviewable
([ADR 0002](../adr/0002-declarative-recipes-no-scripting.md)).

**How do I pass a value from one service to another?**
In step 3, pick it under **Use value from** for the value of the later step. It can go into the path, the
query, a header or the body. See [Passing values between calls](building-endpoints.md#passing-values-between-calls).

**How do I make two calls run at the same time?**
You don't have to. Steps that don't use each other's values run at the same time automatically. The
**Steps** section shows the lanes.

**A service is sometimes down. Does my whole endpoint fail?**
Only if that step is required. Under **Advanced**, untick **If this call fails, fail the whole
endpoint**, and apps get the rest with `_errors` ([example](building-endpoints.md#optional-steps-nice-to-have-data)).

**Can I call the same endpoint for every item in a list?**
Yes: **Run once for each item in**. Up to 100 items, and repeats are called once.

**Two services have the same people with different ids. Can I merge them?**
Yes, if a list exists that links the ids: [Combine lists](combine-lists.md#in-the-wizard). The gateway
never matches people on names or emails.

**Why does my draft not show in the API docs?**
Only published endpoints are listed. Test drafts with **Try it**.

**I changed a published endpoint. When do apps see it?**
When it's published again, within about 2 seconds. Until then, the old version keeps serving.

**Can I undo a publish?**
Yes: **History** → **Roll back to this** on the version you want ([History](testing-and-publishing.md#history-and-rolling-back)).

**Why can't I publish?**
Publishing needs the publisher role and a passing **Try it** of exactly the current settings. Point at the
button to see which is missing.

## Services and sign-ins

**The service has no OpenAPI spec.**
Add it by hand, then add each endpoint with a sample response so the wizard knows its fields
([Add a service by hand](adding-services.md#add-a-service-by-hand-no-spec)).

**Which sign-in do I pick?**
Ask the owner for an example request; its headers tell you. The table in [Pick the sign-in](adding-services.md#pick-the-sign-in)
matches common answers to cards.

**The service needs two things, like an API key and a login.**
Pick the first, then **Combine with another sign-in step** ([example](adding-services.md#combined-service-k-appointments)).

**Where do passwords go?**
On your computer, into `secrets.local.json`, which git ignores. Elsewhere, into Key Vault by an
administrator. Configuration files hold only the secret's name ([Secrets](adding-services.md#secrets)).

**Can apps' user tokens reach the services?**
Only for services set to **Use the caller's own token**. Every other sign-in keeps them away.

## Apps

**What do apps call?**
`https://<gateway>/bff/<route>` with the user's access token. See [For app developers](for-app-developers.md).

**How do apps get typed code for the endpoints?**
Generate it from `/bff/openapi.json` with NSwag or Kiota (.NET) or openapi-typescript
([Generating a client](for-app-developers.md#generating-a-client)).

**An app reports an error. Where do I start?**
Ask for the correlation id and paste it into **Logs** ([Find one request](troubleshooting.md#find-one-request)).

## Running it

**Sample mode or real mode?**
`./scripts/dev-run.sh` uses the invented sample data (with a **Sample data** badge). `./scripts/dev-run.sh --real`
uses your `config/` folder ([Getting started](getting-started.md#6-switch-to-your-own-configuration-1-minute)).

**Does anything need restarting after a publish?**
No. The gateway picks up changes by itself.

**Where's the data stored?**
In plain JSON files: `services.json`, `auth-profiles.json`, `recipes/`, `drafts/` and `history/`. A
PostgreSQL store for several gateway replicas is on the [roadmap](../roadmap.md).

**Can I use this for health data?**
The gateway was designed with that in mind, but your privacy officer, security team and legal advisers
must review it first ([Security and privacy](security-and-privacy.md#needs-review-by-qualified-people)).

**How do I know the guides are right?**
`./scripts/check-docs.sh` runs every example in them against the mock services and compares the output.
It's also part of `dotnet test`.
