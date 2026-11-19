# 0011 — Combining records across services; no fuzzy matching

- Status: Accepted
- Date: 2026-11-19

## Context
Aggregated endpoints often return the same real-world thing from several services: a user from
Identity, CRM and Billing, or overlapping lists from a new and a legacy system. Without a rule, apps
get duplicates or have to merge records themselves, each app slightly differently. Fan-out
(`forEach`) can also call a service several times with the same value.

Merging is risky. If two *different* people, customers or accounts are merged, the app shows one
party's details to another. That's a privacy breach in any domain, and worse where records drive
decisions (finance, health, access rights). The aggregator is used by several solutions, so the rules
must be general-purpose rather than tied to one domain.

## Decision
1. **`distinct`** transform for removing repeats inside one list
   (`{{b.body.items | distinct('id')}}`), and **`forEach` calls once per unique value** by default
   (`"distinct": false` to opt out).
2. A declarative **`combine`** block per recipe merges lists or objects from several steps by an
   **exact key**, after all steps finish, and the response uses it as `{{combine.<name>}}`.
   - Key: one field or a composite key, optionally normalised (`trim`, `lower`). No other matching.
   - Conflicts: `priority` (the first listed source wins; the default, because it's predictable),
     `newest:<field>`, and per-field `owners`. Null values never overwrite real values by default.
   - Records without a key are kept by default and never silently merged.
   - Provenance (`_sources`), stable ordering, an optional `sortBy`, and a `maxRecords` cap (default 5,000).
   - Failure follows ADR 0009: code `combine_failed`; 502 if the combine is required, otherwise a
     partial response with `_errors`.
3. **No fuzzy or probabilistic matching** (similar names, dates of birth, emails) in the gateway, ever.
   When services use **different identifiers** (the common case), a source can be linked through a
   **mapping step** (`via`). A step returns link rows, for example `{ billingAccountNo, crmId }`,
   from whichever system knows the relationship (an identity service, a CRM field, or a small
   cross-reference API), and the combine matches on the mapped id. Values that are missing or
   ambiguous in the mapping are never guessed: those records stay separate and are counted.
4. The **conflict rule is chosen per aggregated endpoint** when it's composed. In the wizard's
   Combine lists card that means first listed service wins, newest wins, or a choice per field.
   `priority` is only the default.
5. The validator **warns** (non-blocking) when a combine key looks like personal identity data
   (email, name, date of birth, phone, address), because such keys can merge different people's
   records. The warning is domain-neutral.

## Consequences
- Apps get one clean record per entity with documented, predictable rules. The OpenAPI spec
  describes the merged shape.
- Merging is shallow: top-level fields only. Nested objects are taken whole from the winning source.
  Deep merging can be added later if real recipes need it.
- Merging is linear time and bounded by `maxRecords`.
- Logs and admin traces show counts per source, never record values.
- Without a shared identifier, some service has to return the links (via a mapping step). That's
  deliberate: deciding which records belong together belongs to a system of record, not to a
  gateway's guesswork. If no service holds the links, an admin-maintained mapping table is a possible
  later addition. It would still be explicit data, never inference.

## Alternatives considered
- **Fuzzy matching in the gateway:** rejected for safety (see above), and because it's hard to
  explain and test.
- **Let each app de-duplicate:** inconsistent results and duplicated effort, and apps would receive
  more data than they need.
- **A general data-transformation language (JSONata, scripts):** rejected by ADR 0002.
