# Security and privacy

**For:** everyone, and in particular your security team, privacy officer and the platform team.

The gateway sits between apps and services that may hold **personal and health information**. This page
says what it protects against, what it never records, and what **your** organisation still has to
decide. It doesn't give legal advice. The design detail is in
[architecture 03](../architecture/03-auth-and-security.md).

## What the gateway protects against

| Risk | What the gateway does |
|---|---|
| An app adds extra parameters (`?admin=true`) to reach more than it should | Only inputs declared on the endpoint are accepted, type-checked and passed on. Anything else is dropped |
| A value changes where a call goes (`../`, another host) | Each value is escaped for its place. `.` and `..` are refused, and the final address must stay on the service's host and base path |
| Header injection | Line breaks in header and cookie values are refused. `Authorization`, `Cookie`, `Host` and connection headers can't be set by an endpoint |
| One user's session or data reaching another user | No shared cookie jar between calls. Cache entries are kept per user by default |
| Credentials sent somewhere else by a redirect | Redirects are never followed |
| App users' tokens reaching services that don't need them | Only the **Use the caller's own token** sign-in passes it on, and only to the services that use it |
| Secrets leaking from the configuration | Configuration holds secret **names** only. The admin app refuses plain values and never returns one |
| Scripts or code hidden in endpoints | There's no scripting: a fixed path language and a fixed list of transforms |
| Matching the wrong person's records together | Lists are combined only on an exact shared id, never on names, emails or dates of birth. The wizard warns if you try |
| A web page on another website reading answers it shouldn't | Browser access (CORS) only on `/bff/*`, for listed origins, and never with cookies or credentials. `*` and `null` can't be listed; operations can limit which domains admins may add; only the admin role can change the list, and every change is kept. CORS isn't a permission: every call still needs a valid token and scope |
| Personal data cached by browsers or proxies | Every response has `Cache-Control: no-store`, `X-Content-Type-Options: nosniff` and `Referrer-Policy: no-referrer` |
| Overloading services | A per-user rate limit, timeouts, a 5 MB answer limit, at most 100 calls per "run for each" step, and at most 25 steps |
| Error messages revealing internals | Apps get a code and a short message, never service answers, stack traces, sign-in names or secret names |
| Unreviewed changes going live | Drafts are never served. Publishing needs the publisher role **and** a passing test of exactly that draft |
| Untracked changes | Every published version is kept, with who published it (the signed-in person, by id and name) and when. History is never changed, and version numbers are never reused |
| Someone tricking an admin's browser into making changes (CSRF) | The admin API (`/admin/*`) accepts only bearer tokens, never the pages' sign-in cookie. The cookie is `HttpOnly` and `SameSite=Lax`, sign-in and sign-out need an antiforgery token, and the pages' live connection must come from the admin app itself |
| An author publishing | Roles are checked inside the admin app's use cases on every action, whether it comes from a page or from `/admin/*` |
| Development shortcuts reaching production | The test-token issuer and the name-only sign-in page work only in Development. The gateway and admin app refuse to start if the dev key is set anywhere else, and the admin app refuses to start outside Development without OpenID Connect |

## What is never logged

Logs record **ids, status codes and timings**, never values:

| Logged | Never logged |
|---|---|
| Which endpoint ran (its id and version), and the result status | Request and response bodies |
| The caller's user id (`sub`) | Query values, header values, cookies, or the full address |
| Each step's service, outcome, error code and time | Tokens, passwords, keys, client secrets |
| The correlation id | Records in combined lists (only their counts) |

The admin UI's **Try it** shows the addresses of calls with **query values masked**, to authors only.
History records the publisher's user id, never their name or email, and holds no request data.

## What you can do to keep it that way

- Use **test data** in Try it, sample responses and the guides. Never real customer or patient data.
- Keep caching **off** for personal or health data unless your privacy officer agrees. If it's on, keep
  **per caller** on and the time short.
- Prefer ids in headers or bodies over the query string, which proxies and browsers may record.
- Pick **Use the caller's own token** only when the service owner confirms it.
- Ask for the smallest scope that works for each endpoint.
- Send secret **names** to administrators. Values go straight from the service owner into Key Vault.

## Needs review by qualified people

These aren't decided by this project. Each needs sign-off by your **privacy officer**, **security
team** and **legal advisers**, and, where health information is involved, whoever is responsible for clinical safety in your organisation.

| Item | Question to answer | Who |
|---|---|---|
| Privacy law | Which obligations apply. In Australia, the *Privacy Act 1988* and the Australian Privacy Principles (notably APP 11, security of personal information), and the Notifiable Data Breaches scheme | Privacy officer, legal |
| My Health Record | If any connected system touches it, the *My Health Records Act 2012* and the Australian Digital Health Agency's requirements | Privacy officer, legal |
| Caching health data | Whether it's allowed at all, and the maximum time | Privacy officer, clinical safety |
| Data residency | Where gateway replicas, caches and logs may run | Privacy officer, platform team |
| Log and history retention | How long logs and history (which hold user ids and display names) are kept, who can read them, and any minimum for audit | Privacy officer, compliance |
| Audit store | A tamper-evident log store with agreed retention (an open item) | Security team, compliance |
| Admin sign-in | Who is given the `aggregator.author` and `aggregator.publisher` roles, whether multi-factor sign-in is required, and whether the 8-hour session suits you. The admin app signs people in with OpenID Connect; the setup is in [Operations](operations.md#deploy-to-azure-container-apps) | Security team |
| Threat model and penetration test | Sign-off before production | Security team |
| Service agreements | Whether each service's data may be combined with the others' and passed to apps | Service owners, privacy officer |

## Reporting a problem

If you think personal data was exposed, follow your organisation's incident and data breach process
straight away. Include the correlation ids and times, never the data itself.

Changes that touch any protection on this page need a security review and a test (see `CLAUDE.md`, and
the `security-review` skill in `.claude/skills/`).
