# Sample services and data

`samples/MockServices` runs fake downstream services on **http://localhost:5100** (started by
`scripts/dev-run.sh`). Everything here is **test data**: invented people, `example.com` emails and
`555-01xx` phone numbers, which are reserved for fiction. Never put real customer or patient data here.

| Path | Service | Sign-in | Used by |
|---|---|---|---|
| `/a` | Customers (`C-10042` premium, `C-20001`, and `C-30003`, who has no loyalty record) | Basic `svc-a` / `pass-a` | `customer-overview`, `customer-profile`, `customer-summary` |
| `/b` | Orders (`GET /b/orders`, and `POST /b/orders/search` with a JSON body) | Session login `svc-b` / `pass-b` | `customer-overview`, `customer-summary` |
| `/c` | Recommendations | OAuth client credentials `client-c` / `secret-c` | `customer-overview`, `customer-summary` |
| `/d` | Billing (invoices) | API key `X-Api-Key: key-d` | Not in the config: use it to try **Add service** |
| `/e` | Identity: users | API key `X-Api-Key: key-e` | `people-directory`, `people-newest` |
| `/f` | CRM: contacts, notes, orders | Basic `svc-f` / `pass-f` | all three people recipes |
| `/g` | Billing: accounts | API key `X-Api-Key: key-g` | `people-directory`, `people-newest` |
| `/h` | Links: billing account → user | None (internal) | `people-directory`, `people-newest` |
| `/i` | Loyalty | Fixed token `Authorization: Bearer token-i` | `customer-loyalty`, `customer-summary` |
| `/j` | Preferences | The caller's own token (passed on) | `my-preferences` |
| `/k` | Appointments | API key `X-Api-Key: key-k` **and** a token from `POST /k/auth/login` (`svc-k` / `pass-k`) | `customer-appointments` |

`customer-summary` passes values from A into I's path, B's JSON body, and C's query and header (C also uses I's answer). With `C-30003` the optional loyalty step fails, which shows a partial answer. The guides in [`docs/guides/`](../docs/guides/) use these endpoints, and `scripts/check-docs.sh` checks every answer they show.

Each of A, D, E, F, G and H has its spec at `/<x>/openapi.json`, with response schemas, so the wizard's
dropdowns list the fields. `GET /_stats` shows how many times each endpoint was called. The E–H
services, sign-ins and recipes are in `samples/config/`, and the Development settings of the gateway and
admin app hold their mock secrets.

## Trying "Combine lists"

The data for E–H is in [`MockServices/PeopleSamples.cs`](MockServices/PeopleSamples.cs). It describes
the same people in three systems that **don't share an id**:

- **Identity** (`/e/users`): the system of record, users `u-1` … `u-6`.
- **CRM** (`/f/contacts`): its own contact ids `c-101` …, plus `identityUserId` pointing at Identity.
- **Billing** (`/g/accounts`): its own account numbers `ACC-1` … and **no user id at all**. Accounts are
  linked to users only through **Links** (`/h/links`, rows `{ billingAccountNo, userId }`).

Run the recipes from the command line (mocks running, secrets from the environment):

```bash
export Secrets__service-e-api-key=key-e Secrets__service-f-password=pass-f Secrets__service-g-api-key=key-g
dotnet run --project tools/ApiAggregator.Cli -- run people-directory --config samples/config
dotnet run --project tools/ApiAggregator.Cli -- run people-newest --config samples/config
dotnet run --project tools/ApiAggregator.Cli -- run contacts-unique --config samples/config
```

(zsh's `export` refuses names with dashes. There, put `env "Secrets__service-e-api-key=key-e" …` in front of each command.)

Or call the gateway at `/bff/people/directory`, `/bff/people/newest` and `/bff/contacts/unique` with a
token that has the `people.read` scope. The API docs page at `/bff/docs` signs in with it automatically
in Development. In the admin UI, open the recipe and choose **Try it**.

### What each recipe shows

| Recipe | What it shows | Expected result |
|---|---|---|
| `people-directory` | Combine Identity + CRM (key `identityUserId`) + Billing (`via` Links: match `billingAccountNo`, use `userId`). Normalize `trim` + `lower`, conflict rule `priority` (first list wins), owners `email: identity`, `plan: billing`, `id: identity`, `trackSources: true` | `identity 6, crm 8, billing 6 → 10 records (10 matched, 1 unmapped, 1 ambiguous, 1 without a key)` |
| `people-newest` | The same, with `onConflict: newest:updatedAt` | The same counts and the same 10 records. Only some field values differ (see below) |
| `contacts-unique` | `{{crm.body.contacts \| distinct('id')}}`, plus a `forEach` over the orders' `contactId` | `contactRows` **8**, `uniqueContacts` **7**, `orderCount` **6**, `notesCalls` **3** (the CLI shows `calls=3` for the `notes` step) |

**How to read the counts.** "matched" counts merges, not people: each time a record joins one already
in the list, that's one match. CRM gives 6 (c-101, c-102, c-103, c-104 twice, c-107) and Billing gives
4 (ACC-1 to ACC-4), so 10 in total. "unmapped", "ambiguous" and "without a key" records are never
merged. Each one stays as a record of its own.

### Where each record ends up (people-directory)

| # | Record | Comes from (`_sources`) | Why |
|---|---|---|---|
| 1 | `u-1` Ava Thompson | identity, crm, billing | CRM **c-101** and **c-107** both say `u-1`. Billing **ACC-1** links to `u-1` |
| 2 | `u-2` Ben Okafor | identity, crm, billing | CRM **c-102** says `" U-2"`: it matches only because of `trim` + `lower`. Links say ACC-2 → `"U-2"`, so normalize applies to mapped ids too. No phone: c-102's phone is null |
| 3 | `u-3` Chloe Nguyen | identity, crm | CRM **c-103** matches, but no billing account links to `u-3` |
| 4 | `u-4` Daniel Kowalski | identity, crm, billing | CRM **c-104** appears **twice** (an exact duplicate row). Both match, which counts 2 matches, and the second changes nothing. Billing **ACC-3** links to `u-4`. Email is Identity's |
| 5 | `u-5` Emma Rossi | identity | Links send ACC-6 to **both** `u-5` and `u-6`. The gateway won't guess, so ACC-6 joins neither |
| 6 | `u-6` Farid Haddad | identity | Same as above |
| 7 | `c-105` Grace Lee | crm | `identityUserId` is null: **without a key**, so kept on its own |
| 8 | `c-106` Hugo Martin | crm, billing | Points at `u-9`, whom Identity doesn't have, so the record is **added** to the list. Billing **ACC-4** links to `u-9`, so it joins Hugo |
| 9 | `ACC-5` (family, closed) | billing | Not in Links: **unmapped** |
| 10 | `ACC-6` (premium) | billing | Linked to two different users: **ambiguous** |

Records 9 and 10 have no `id`, `name` or `email`: Billing doesn't know who they belong to.

### What changes with newest:updatedAt (people-newest)

The records and counts are the same; the rule only decides which **values** win. Billing has no
`updatedAt`, so it only fills gaps, except for `plan`, which it owns.

| Record | people-directory (first list wins) | people-newest |
|---|---|---|
| `u-3` name | `Chloe Nguyen` (Identity is listed first) | `Chloe Nguyen-Smith`: CRM c-103 (Apr 15) is newer than Identity (Jan 10) |
| `u-4` email | `daniel.kowalski@example.com` | **Still** `daniel.kowalski@example.com`. c-104 is newer and has `dan.k@example.com`, but **owners** gives email to Identity |
| `u-1` phone | `+1 202 555 0101` (from c-101) | **Still** `+1 202 555 0101`. c-107 (May 1) is the newest and its phone is null, and a null never overwrites a value |
| `updatedAt` | Identity's dates | The newest date: u-1 `2026-05-01…`, u-3 `2026-04-15…`, u-4 `2026-04-20…` |
| `id` | Identity's | Identity's. Without `owners: { "id": "identity" }`, the newer CRM contacts would replace `u-3` with `c-103` |

Why `id` has an owner: merging works on top-level fields, and CRM contacts also have an `id`. When two
services use the same field name for different things, give that field an owner, as these recipes do.

### Things to try in the wizard

| Change in **Combine lists** | Expected Try it line | What happens |
|---|---|---|
| Untick ignore upper/lower case (normalize `trim` only) | `… → 11 records (9 matched, 1 unmapped, 1 ambiguous, 1 without a key)` | Ben is split: `u-2` comes from Identity only, and CRM `c-102` + `ACC-2` form a second "Ben" |
| **Leave out records without a match value** (`withoutKey: drop`) | `… → 7 records (10 matched, 1 unmapped, 1 ambiguous, 1 without a key)` | Grace (c-105), ACC-5 and ACC-6 are left out. The counts still say they existed |

### contacts-unique

- **`| distinct('id')`**: the CRM sends 8 rows. The second `c-104` is identical, so 7 remain, in
  first-seen order `c-101` … `c-107`. `distinct` removes repeats inside **one** list. It doesn't merge
  c-101 and c-107, which are different contacts for the same person: that's what `combine` is for.
- **`forEach` once per unique value**: the 6 orders belong to c-101 (3 orders), c-103 (2) and c-104
  (1). The `notes` step calls `/f/contacts/{id}/notes` **3** times, not 6, and returns one entry per
  contact in first-seen order: c-101, c-103, c-104. `GET /_stats` on the mocks shows
  `f.getContactNotes` going up by 3 per run.

These results are checked by `tests/ApiAggregator.Engine.Tests/SampleDataTests.cs`. The tests link
`PeopleSamples.cs` and load these recipe and config files from `samples/config`, so they don't need the mocks running. If
you change the data, update this page and the tests together.
