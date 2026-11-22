# 0016 — Try it shows each call's request and response

- Status: Accepted
- Date: 2026-11-22
- Deciders: Chathura Wijesinghe

## Context
To check an endpoint, people testing it need to see what each service was sent and what it answered, not just the
combined answer. Try it showed each step's status, time and address with query values hidden (`?status=…`), while
path values and the whole combined answer were already shown. Logs must stay free of values (ADR 0009): they're
kept, copied to central stores and read by many people.

## Decision
- In **admin test runs only** (`ExecutionMode.IncludeRequestDetails`: Try it, and testing a service endpoint before
  adding it), each step's trace carries `Details`: per call, the full address **including query values**, the headers,
  cookies and body the recipe sent, the status, and the service's answer (status, headers without `Set-Cookie`, body).
- A failed call (4xx/5xx) includes what the service answered, so the reason is visible. Apps still never get
  downstream bodies: their error response is built separately, as before.
- Sign-in headers are added after the details are taken, so keys, tokens, passwords and session cookies never appear.
- Limits: each body over 64 KB is cut (marked), and a for-each step shows its first 20 calls.
- Details are shown on the page and returned by `/admin/recipes/{id}/test`; they're **never logged or stored**.
- The step's summary line (and the command line) keeps query values masked.
- Log lines name the call by its method and path **template** (`GET /contacts/{contactId}`), never the values, and
  Try it links each step to its log lines (`/logs?correlation=…&step=…`).

## Consequences
- Authors can validate each call and see why one failed, without reading service logs.
- Authors (and admins viewing a test) now see query values and per-call answers. In production those can be personal
  or health data, exactly like the combined answer they already saw. Who gets the author role matters accordingly.
- A shared screen or screenshot of Try it can show such data: test with test data where possible.

## Alternatives considered
- **Values in logs:** rejected. Logs are long-lived, copied and widely read; ADR 0009 stands.
- **Keep query values masked:** inconsistent, since path values and bodies were shown, and it hides a common cause
  of wrong results (a wrong filter value).
