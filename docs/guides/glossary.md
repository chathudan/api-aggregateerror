# Glossary

Words used in the admin UI and these guides, in plain English. The word in `code` is what the files call it.

| Word | Meaning | Example |
|---|---|---|
| **Aggregated endpoint** (`recipe`) | What apps call. It runs several steps and returns one answer | `GET /bff/customer/C-10042/summary` |
| **API docs** | The page where anyone can read and try the published endpoints (Swagger UI) | `/bff/docs` |
| **Auth profile** | The saved sign-in for a service: one or more sign-in steps | `service-k-key-and-login` |
| **Base address** (`baseUrl`) | The part of a service's address every endpoint path is added to | `http://localhost:5100/a` |
| **BFF** | *Backend for frontend*: a gateway shaped around what app screens need. All aggregated endpoints are under `/bff/` | |
| **Browser access** (CORS) | Which other websites' pages may call the gateway from a browser and read the answer. Not a permission: calls still need a token | **Settings → Browser access (CORS)** |
| **Cache** | A copy of an answer kept for a short time, so repeat calls are faster. Off unless turned on, and per user | `customer-profile`, 30 s |
| **Combine lists** (`combine`) | Merging lists of the same kind of record from several services, on an exact shared id | `people-directory` |
| **Condition** (`when`) | "Only run when": a step runs only if a value is true or equal to something | `{{a.body.isPremium}} == true` |
| **Correlation id** | A code on every response that finds the matching lines in the logs | `X-Correlation-Id: 7c19d1a6…` |
| **Draft** | An endpoint being built or changed. Never served to apps | `drafts/customer-overview.json` |
| **Endpoint (of a service)** | One operation of a service | `GET /customers/{customerId}` |
| **Error code** (`code`) | The stable, machine-readable reason in an error answer | `auth_failed` |
| **Gateway** | The program apps call. It runs the published endpoints | `http://localhost:5200` |
| **Origin** | The start of a website's address: scheme, host and port only, no path | `https://portal.example.com` |
| **History** | Every published version of an endpoint, with who and when. Never changed | `history/customer-overview.v3.json` |
| **Input** | A value the caller may send: in the route, the query or a header. Nothing else gets through | `route.id`, `query.status` |
| **Live** | The version apps get now | |
| **Mock services** | Pretend backend services with invented data, for learning and tests | `http://localhost:5100` |
| **OpenAPI (Swagger) spec** | A file that describes an API's endpoints. Used to add services and to generate app code | `/d/openapi.json`, `/bff/openapi.json` |
| **Optional step** (`required: false`) | A step the endpoint can do without. If it fails, apps get a partial answer | `loyalty` in `customer-summary` |
| **Partial answer** | 200 with `_errors`: some optional data is missing | `C-30003` |
| **Path language** | The small, fixed way of picking values: `a.body.items[0].id`, plus transforms. Not a programming language | |
| **Problem details** | The standard JSON shape of every error answer | |
| **Publish** | Make a tested draft live. Needs the publisher role | |
| **Publisher** (`aggregator.publisher`) | Someone allowed to publish, roll back and delete endpoints | |
| **Author** (`aggregator.author`) | Someone allowed to add services and build and test endpoints | |
| **Roll back** | Make an older version live again, as a new version number | |
| **Run for each** (`forEach`) | Call a step once for each item in a list | Order lines for each order |
| **Sample data** | The invented services and endpoints in `samples/config/`. The UI shows a badge | |
| **Scope** | A permission the app user's token must have to call an endpoint | `customer.read` |
| **Secret** | A password, key or token. Configuration holds only its name | `secret:service-a-password` |
| **Service** | A backend API the gateway calls | Service A (customers) |
| **Sign-in** | How the gateway proves who it is to a service. Not how app users sign in | API key, OAuth client |
| **Step** (`alias`) | One call to a service endpoint inside an aggregated endpoint | `customer`, `orders` |
| **Suggested** | A value the wizard filled in for you. Check it | |
| **Transform** | A small conversion after a `\|` | `count`, `join(',')`, `distinct('id')` |
| **Try it** | Run a draft for real from the wizard, with every call shown | |
