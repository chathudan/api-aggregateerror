# Combine lists

**For:** technicians and QA. App developers: the result shape is under [What apps get](#what-apps-get).

Use **Combine lists** when several services return the same kind of record (people, customers,
accounts) and the app should get **each record once**. For example, Identity knows a person's email, the
CRM knows their phone number and Billing knows their plan. The app wants one list of people with all
three.

The gateway merges records **only when they share an identifier**, compared exactly. It never decides
that two records are the same person because names, emails or dates of birth look alike
([ADR 0011](../adr/0011-combining-records-no-fuzzy-matching.md)). Matching people that way belongs in the
system that owns identities, with the safeguards that needs.

## The sample data

The mocks hold the same invented people in three systems that **don't share an id**
([`PeopleSamples.cs`](../../samples/MockServices/PeopleSamples.cs)):

| Service | Records | Its key | How it links to Identity |
|---|---|---|---|
| **E Identity** `/e/users` | users `u-1` … `u-6` | `id` | It *is* the reference |
| **F CRM** `/f/contacts` | contacts `c-101` … `c-107` (8 rows: `c-104` twice) | `id` (its own) | `identityUserId` holds the Identity id |
| **G Billing** `/g/accounts` | accounts `ACC-1` … `ACC-6` | `accountNo` | Not at all: only through **H Links** |
| **H Links** `/h/links` | rows `{ billingAccountNo, userId }` | | Says which account is which user |

## In the wizard

In step 4, **Response**, choose **Combine lists**.

1. Name the list, for example `people`.
2. For each list, pick it under **List** (the lists come from your steps) and pick **Match on**: the
   field that holds the shared id. Use **Add list** for more lists.
   - Identity: list `identity.body.users`, match on `id`.
   - CRM: list `crm.body.contacts`, match on `identityUserId`.
3. Billing has no user id, so tick **Different ids? Link through another list**. Pick the link list
   `links.body.links`, the link field that matches this list's id (`billingAccountNo`), and the link field
   to use (`userId`).
4. Choose **which list wins** when values differ: **First list wins** (`priority`), **Newest wins**
   (`newest:<date field>`), and **Choose per field** for fields that one service owns.
5. Under **More options**: ignore spaces and upper/lower case when matching, leave out records without a
   match value, show which services each record came from, sort, and set the most records allowed.
6. Choose **Add to response**, then **Try it**.

The recipe this builds, [`people-directory.json`](../../samples/config/recipes/people-directory.json):

```json
"combine": {
  "people": {
    "sources": [
      { "from": "identity.body.users",   "key": "id",             "name": "identity" },
      { "from": "crm.body.contacts",     "key": "identityUserId", "name": "crm" },
      { "from": "billing.body.accounts", "key": "accountNo",      "name": "billing",
        "via": { "from": "links.body.links", "match": "billingAccountNo", "use": "userId" } }
    ],
    "normalize": ["trim", "lower"],
    "onConflict": "priority",
    "owners": { "email": "identity", "plan": "billing", "id": "identity" },
    "trackSources": true
  }
},
"response": { "people": "{{combine.people}}", "count": "{{combine.people | count}}" }
```

## What Try it shows

**Try it** (and the command line) shows a line of **counts** for the combined list, never the records:

<!-- check: run people-directory -->
```text
Succeeded combine.people -        4 ms  calls=0   — identity 6, crm 8, billing 6 → 10 records (10 matched, 1 unmapped, 1 ambiguous, 1 without a key)
```

| Count | Meaning | Here |
|---|---|---|
| **matched** | Records that joined one already in the list. It counts merges, not people | CRM 6 (c-101, c-102, c-103, c-104 twice, c-107) + Billing 4 (ACC-1 to ACC-4) |
| **unmapped** | The id isn't in the link list. Kept on its own | ACC-5 |
| **ambiguous** | The id links to two different ids. Never merged: the gateway won't guess | ACC-6 links to both u-5 and u-6 |
| **without a key** | No value in the match field. Kept on its own (or left out, if you chose that) | CRM c-105 |
| **ignored** | Items that aren't records, such as plain text (0 here, so not shown) | |

A high **unmapped** count usually means the link list is incomplete, or its step failed.

## What apps get

Ten records, in the order they were first seen. With **Show which services each record came from**
(`trackSources`), each has `_sources`. This shows a few fields of each record (the check compares just
these):

<!-- check: run people-directory partial -->
```json
{
  "count": 10,
  "people": [
    { "id": "u-1",   "name": "Ava Thompson",    "phone": "+1 202 555 0101", "plan": "family",  "_sources": ["identity", "crm", "billing"] },
    { "id": "u-2",   "name": "Ben Okafor",                                  "plan": "single",  "_sources": ["identity", "crm", "billing"] },
    { "id": "u-3",   "name": "Chloe Nguyen",    "phone": "+1 202 555 0103",                    "_sources": ["identity", "crm"] },
    { "id": "u-4",   "name": "Daniel Kowalski", "email": "daniel.kowalski@example.com", "plan": "premium", "_sources": ["identity", "crm", "billing"] },
    { "id": "u-5",   "name": "Emma Rossi",                                                     "_sources": ["identity"] },
    { "id": "u-6",   "name": "Farid Haddad",                                                   "_sources": ["identity"] },
    { "id": "c-105", "name": "Grace Lee",                                                      "_sources": ["crm"] },
    { "id": "c-106", "name": "Hugo Martin",     "accountNo": "ACC-4",       "plan": "single",  "_sources": ["crm", "billing"] },
    { "accountNo": "ACC-5", "plan": "family",  "_sources": ["billing"] },
    { "accountNo": "ACC-6", "plan": "premium", "_sources": ["billing"] }
  ]
}
```

Why each record ends up where it does:

| Record | Why |
|---|---|
| `u-1` Ava | CRM c-101 **and** c-107 both say `u-1`, and Links send ACC-1 to `u-1` |
| `u-2` Ben | CRM c-102 says `" U-2"`. It matches only because of **Ignore spaces around the match value** and **Ignore upper/lower case** (`trim`, `lower`) |
| `u-3` Chloe | Matched in CRM; no billing account links to her |
| `u-4` Daniel | c-104 appears twice (a duplicate row): both match, the second changes nothing. Email comes from Identity, which **owns** it |
| `u-5`, `u-6` | ACC-6 links to both, so it joins **neither** |
| `c-105` Grace | Her `identityUserId` is empty: **without a key** |
| `c-106` Hugo | Points at `u-9`, whom Identity doesn't have, so he's **added**. ACC-4 links to `u-9` and joins him |
| `ACC-5` | Not in Links: **unmapped** |
| `ACC-6` | Linked to two users: **ambiguous** |

The last two have no `id`, `name` or `email`: Billing doesn't know whose they are. Apps should be ready for
records like these. [samples/README.md](../../samples/README.md) has the full walk-through.

## Which list wins

`people-newest` is the same recipe with `"onConflict": "newest:updatedAt"`. The records and counts are
the same. Only some values change:

<!-- check: run people-newest partial -->
```json
{
  "count": 10,
  "people": [
    { "id": "u-1", "phone": "+1 202 555 0101", "updatedAt": "2026-05-01T08:00:00Z" },
    { "id": "u-2" },
    { "id": "u-3", "name": "Chloe Nguyen-Smith", "updatedAt": "2026-04-15T08:00:00Z" },
    { "id": "u-4", "email": "daniel.kowalski@example.com", "updatedAt": "2026-04-20T08:00:00Z" },
    { "id": "u-5" }, { "id": "u-6" }, { "id": "c-105" }, { "id": "c-106" },
    { "accountNo": "ACC-5" }, { "accountNo": "ACC-6" }
  ]
}
```

- **u-3's name** is the CRM's `Chloe Nguyen-Smith`, because that contact (15 April) is newer than Identity's record (10 January).
- **u-4's email** stays Identity's, even though the newer CRM contact says `dan.k@example.com`: **owners**
  gives `email` to Identity, whatever the rule says.
- **u-1's phone** stays: the newest contact (c-107) has no phone, and an empty value never overwrites a real one.
- **id** has an owner too. CRM contacts also have an `id` field (`c-103`), and without
  `"id": "identity"` the newer CRM record would replace `u-3` with `c-103`. When two services use the same
  field name for different things, give that field an owner.

Merging works on **top-level fields**. A nested block, such as an address, is taken whole from one
service, never mixed from two.

## Things to try

| Change in **Combine lists** | Try it shows | What happened |
|---|---|---|
| Untick **Ignore upper/lower case** (normalize `trim` only) | `… → 11 records (9 matched, 1 unmapped, 1 ambiguous, 1 without a key)` | Ben splits: `u-2` from Identity, and CRM c-102 + ACC-2 as a second "Ben" |
| **Records without a match value** → **Leave them out** (`withoutKey: drop`) | `… → 7 records (10 matched, 1 unmapped, 1 ambiguous, 1 without a key)` | Grace, ACC-5 and ACC-6 are left out. The counts still say they existed |
| Match CRM on `email` | A warning under **Check before publishing** | Different people can share an email. Use an id, or a link list |

## Duplicates inside one list

To remove repeats **inside one list**, use the `distinct` transform; there's no need to combine.
`contacts-unique` ([recipe](../../samples/config/recipes/contacts-unique.json)) shows both ways:

```json
"response": {
  "contactRows":    "{{crm.body.contacts | count}}",
  "uniqueContacts": "{{crm.body.contacts | distinct('id') | count}}",
  "orderCount":     "{{orders.body.orders | count}}",
  "notesCalls":     "{{notes | count}}"
}
```

<!-- check: run contacts-unique partial -->
```json
{ "contactRows": 8, "uniqueContacts": 7, "orderCount": 6, "notesCalls": 3 }
```

- `| distinct('id')` keeps the first record for each `id`: 8 rows become 7. (It doesn't merge c-101 and
  c-107. They're different contacts for the same person, which is what `combine` is for.)
- The `notes` step **runs once for each** order's `contactId`. The 6 orders belong to only 3 contacts,
  so it makes **3** calls, not 6:

<!-- check: run contacts-unique -->
```text
Succeeded notes      200      3 ms  calls=3  GET http://localhost:5100/f/contacts/c-101/notes
```

## Rules worth knowing

- Combines run after all steps, and steps can't use them. One combine can't use another.
- A source whose step failed (an optional step) is skipped, and the failure is in `_errors`.
- At most 5,000 input records by default (`maxRecords`). Above that, the endpoint fails with `combine_failed`.
- Untick **Fail the endpoint if this can't be combined** (`"required": false`) to get `null` plus `_errors` instead of a 502 when it fails.
- Logs record the counts only, never the records (event 1500).

The full settings table is in [architecture 02](../architecture/02-recipes.md#combining-records-from-several-services).
