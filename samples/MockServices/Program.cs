// Mock downstream services for local development and QA:
//   /a  Service A - customers       - Basic auth            (svc-a / pass-a)
//   /b  Service B - orders          - session cookie login  (svc-b / pass-b)
//   /c  Service C - recommendations - OAuth client credentials -> JWT (client-c / secret-c)
//   /d  Service D - billing         - API key (X-Api-Key: key-d), has an OpenAPI spec at /d/openapi.json
//   /b  also POST /b/orders/search with a JSON body (for "send a value in the body")
// One per remaining sign-in type, so every guide example runs:
//   /i  Service I - loyalty         - fixed token (Authorization: Bearer token-i)
//   /j  Service J - preferences     - the caller's own token (any Bearer token; the mock reads its "sub")
//   /k  Service K - appointments    - API key (X-Api-Key: key-k) AND a token from POST /k/auth/login (svc-k / pass-k)
// For "Combine lists" (data in PeopleSamples.cs, explained in samples/README.md), each with /<x>/openapi.json:
//   /e  Identity  - users                     - API key (X-Api-Key: key-e)
//   /f  CRM       - contacts, notes, orders   - Basic auth (svc-f / pass-f)
//   /g  Billing   - accounts                  - API key (X-Api-Key: key-g)
//   /h  Links     - billing account -> user   - no auth (internal)
// GET /_stats shows how many times each endpoint was called (handy for checking the cache).
// Test data only. No real customer or patient data belongs here.

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MockServices;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var calls = new ConcurrentDictionary<string, int>();
void Count(string name) => calls.AddOrUpdate(name, 1, (_, n) => n + 1);
app.MapGet("/_stats", () => calls.OrderBy(k => k.Key).ToDictionary(k => k.Key, k => k.Value));

var customers = new Dictionary<string, object>
{
    ["C-10042"] = new { id = "C-10042", name = "Alex Example", accountNo = "ACC-778", segmentCode = "family", isPremium = true },
    ["C-20001"] = new { id = "C-20001", name = "Sam Sample", accountNo = "ACC-901", segmentCode = "single", isPremium = false }
};

// ------------------------------------------------------------------ Service A (Basic)
var a = app.MapGroup("/a").AddEndpointFilter(async (ctx, next) =>
{
    var header = ctx.HttpContext.Request.Headers.Authorization.ToString();
    var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("svc-a:pass-a"));
    return header == expected ? await next(ctx) : Results.Unauthorized();
});
a.MapGet("/customers/{customerId}", (string customerId) =>
{
    Count("a.getCustomer");
    return customers.TryGetValue(customerId, out var c) ? Results.Ok(c) : Results.NotFound();
});
a.MapGet("/customers/{customerId}/addresses", (string customerId) =>
{
    Count("a.getAddresses");
    return Results.Ok(new[] { new { type = "home", suburb = "Carlton", state = "VIC", postcode = "3053" } });
});
app.MapGet("/a/openapi.json", () => Results.Text(OpenApiSpecs.ServiceA, "application/json"));

// ------------------------------------------------------------------ Service B (session login)
var sessions = new ConcurrentDictionary<string, DateTimeOffset>();
app.MapPost("/b/auth/login", (LoginRequest body, HttpContext http) =>
{
    Count("b.login");
    if (body.Username != "svc-b" || body.Password != "pass-b") return Results.Unauthorized();
    var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
    sessions[id] = DateTimeOffset.UtcNow.AddMinutes(20);
    http.Response.Headers.Append("Set-Cookie", $"SESSIONID={id}; Path=/; HttpOnly");
    return Results.Ok(new { ok = true });
});
var b = app.MapGroup("/b").AddEndpointFilter(async (ctx, next) =>
{
    var sid = ctx.HttpContext.Request.Cookies["SESSIONID"];
    return sid is not null && sessions.TryGetValue(sid, out var exp) && exp > DateTimeOffset.UtcNow ? await next(ctx) : Results.Unauthorized();
});
b.MapGet("/orders", (HttpRequest req, string? account, string? status, int? page) =>
{
    Count("b.getOrders");
    var customerId = req.Headers["X-Customer-Id"].ToString();
    if (string.IsNullOrEmpty(customerId)) return Results.BadRequest(new { error = "X-Customer-Id header is required" });
    var items = Enumerable.Range(1, 7).Select(i => new { id = $"O-{customerId[^3..]}-{i}", total = 20.5 * i, status = status ?? "open" });
    return Results.Ok(new { customerId, account, page = page ?? 1, tier = customerId == "C-10042" ? "gold" : "standard", items });
});
// Same orders as GET /orders, but the customer and status come in the JSON body.
b.MapPost("/orders/search", (OrderSearch body) =>
{
    Count("b.searchOrders");
    if (string.IsNullOrEmpty(body.CustomerId)) return Results.BadRequest(new { error = "customerId is required in the body" });
    var status = body.Status ?? "open";
    var items = Enumerable.Range(1, 3).Select(i => new { id = $"O-{body.CustomerId[^3..]}-{i}", total = 20.5 * i, status });
    return Results.Ok(new { customerId = body.CustomerId, status, count = 3, items });
});
b.MapGet("/orders/{orderId}/lines", (string orderId) =>
{
    Count("b.getOrderLines");
    return Results.Ok(new { orderId, lines = new[] { new { sku = "SKU-1", qty = 1 }, new { sku = "SKU-2", qty = 3 } } });
});

// ------------------------------------------------------------------ Service C (OAuth client credentials -> JWT)
var signingKey = RandomNumberGenerator.GetBytes(32);
app.MapPost("/c/oauth/token", async (HttpRequest req) =>
{
    Count("c.token");
    var form = await req.ReadFormAsync();
    if (form["grant_type"] != "client_credentials" || form["client_id"] != "client-c" || form["client_secret"] != "secret-c")
        return Results.Json(new { error = "invalid_client" }, statusCode: 401);
    return Results.Ok(new { access_token = Jwt.Create(signingKey, "client-c", TimeSpan.FromMinutes(10)), token_type = "Bearer", expires_in = 600 });
});
var c = app.MapGroup("/c").AddEndpointFilter(async (ctx, next) =>
{
    var auth = ctx.HttpContext.Request.Headers.Authorization.ToString();
    return auth.StartsWith("Bearer ") && Jwt.IsValid(signingKey, auth[7..]) ? await next(ctx) : Results.Unauthorized();
});
c.MapGet("/recommendations", (HttpRequest req, string? segment) =>
{
    Count("c.getRecommendations");
    var tier = req.Headers["X-Tier"].ToString();
    return Results.Ok(new { segment, tier, items = new[] { new { code = "R1", title = $"Offer for {segment}/{tier}" }, new { code = "R2", title = "Loyalty bonus" } } });
});

// ------------------------------------------------------------------ Service D (API key, OpenAPI spec)
var d = app.MapGroup("/d").AddEndpointFilter(async (ctx, next) =>
    ctx.HttpContext.Request.Headers["X-Api-Key"] == "key-d" ? await next(ctx) : Results.Unauthorized());
d.MapGet("/invoices", (string? customerId) =>
{
    Count("d.listInvoices");
    return string.IsNullOrEmpty(customerId)
        ? Results.BadRequest(new { error = "customerId is required" })
        : Results.Ok(new { customerId, total = 2, invoices = new[] { new { id = "INV-1", amount = 120.0, status = "paid" }, new { id = "INV-2", amount = 80.0, status = "due" } } });
});
d.MapGet("/invoices/{invoiceId}", (string invoiceId) =>
{
    Count("d.getInvoice");
    return Results.Ok(new { id = invoiceId, amount = 120.0, status = "paid", lines = new[] { new { item = "Consult", amount = 120.0 } } });
});
app.MapGet("/d/openapi.json", () => Results.Text(OpenApiSpecs.ServiceD, "application/json"));

// ------------------------------------------------------------------ Service I (fixed token)
var loyalty = new Dictionary<string, object>
{
    ["C-10042"] = new { customerId = "C-10042", points = 1250, tier = "gold", memberSince = "2019-04-01" },
    ["C-20001"] = new { customerId = "C-20001", points = 80, tier = "standard", memberSince = "2025-11-12" }
};
var i = app.MapGroup("/i").AddEndpointFilter(async (ctx, next) =>
    ctx.HttpContext.Request.Headers.Authorization == "Bearer token-i" ? await next(ctx) : Results.Unauthorized());
i.MapGet("/loyalty/{customerId}", (string customerId) =>
{
    Count("i.getLoyalty");
    return loyalty.TryGetValue(customerId, out var l) ? Results.Ok(l) : Results.NotFound();
});

// ------------------------------------------------------------------ Service J (the caller's own token)
// A real service would validate the token with the identity provider. The mock only reads "sub" so the
// answer shows the token arrived: never copy this into anything real.
var j = app.MapGroup("/j").AddEndpointFilter(async (ctx, next) =>
    ctx.HttpContext.Request.Headers.Authorization.ToString().StartsWith("Bearer ") ? await next(ctx) : Results.Unauthorized());
j.MapGet("/preferences", (HttpRequest req) =>
{
    Count("j.getPreferences");
    var subject = Jwt.SubjectOf(req.Headers.Authorization.ToString()["Bearer ".Length..]) ?? "unknown";
    return Results.Ok(new { subject, language = "en-AU", contactBy = "email", newsletter = false });
});

// ------------------------------------------------------------------ Service K (API key + login)
var kTokens = new ConcurrentDictionary<string, DateTimeOffset>();
app.MapPost("/k/auth/login", (LoginRequest body) =>
{
    Count("k.login");
    if (body.Username != "svc-k" || body.Password != "pass-k") return Results.Unauthorized();
    var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
    kTokens[token] = DateTimeOffset.UtcNow.AddMinutes(20);
    return Results.Ok(new { token, expiresIn = 1200 });
});
var k = app.MapGroup("/k").AddEndpointFilter(async (ctx, next) =>
{
    var h = ctx.HttpContext.Request.Headers;
    var auth = h.Authorization.ToString();
    var tokenOk = auth.StartsWith("Bearer ") && kTokens.TryGetValue(auth[7..], out var exp) && exp > DateTimeOffset.UtcNow;
    return h["X-Api-Key"] == "key-k" && tokenOk ? await next(ctx) : Results.Unauthorized();
});
k.MapGet("/appointments", (string? customerId) =>
{
    Count("k.listAppointments");
    if (string.IsNullOrEmpty(customerId)) return Results.BadRequest(new { error = "customerId is required" });
    return Results.Ok(new
    {
        customerId,
        appointments = new[]
        {
            new { id = "AP-1", date = "2026-12-02", time = "09:30", type = "review" },
            new { id = "AP-2", date = "2027-01-15", time = "14:00", type = "follow-up" }
        }
    });
});

// ------------------------------------------------------------------ E, F, G, H: people data for "Combine lists"
IResult Json(string name, string json) { Count(name); return Results.Text(json, "application/json"); }

var e = app.MapGroup("/e").AddEndpointFilter(async (ctx, next) =>
    ctx.HttpContext.Request.Headers["X-Api-Key"] == "key-e" ? await next(ctx) : Results.Unauthorized());
e.MapGet("/users", () => Json("e.listUsers", PeopleSamples.Users));

var f = app.MapGroup("/f").AddEndpointFilter(async (ctx, next) =>
{
    var header = ctx.HttpContext.Request.Headers.Authorization.ToString();
    var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("svc-f:pass-f"));
    return header == expected ? await next(ctx) : Results.Unauthorized();
});
f.MapGet("/contacts", () => Json("f.listContacts", PeopleSamples.Contacts));
f.MapGet("/contacts/{contactId}/notes", (string contactId) => Json("f.getContactNotes", PeopleSamples.Notes(contactId)));
f.MapGet("/orders", () => Json("f.listOrders", PeopleSamples.Orders));

var g = app.MapGroup("/g").AddEndpointFilter(async (ctx, next) =>
    ctx.HttpContext.Request.Headers["X-Api-Key"] == "key-g" ? await next(ctx) : Results.Unauthorized());
g.MapGet("/accounts", () => Json("g.listAccounts", PeopleSamples.Accounts));

app.MapGet("/h/links", () => Json("h.listLinks", PeopleSamples.Links));

// Specs are public, like /a and /d, so "Add service" can import them before a sign-in is set up.
app.MapGet("/e/openapi.json", () => Results.Text(OpenApiSpecs.ServiceE, "application/json"));
app.MapGet("/f/openapi.json", () => Results.Text(OpenApiSpecs.ServiceF, "application/json"));
app.MapGet("/g/openapi.json", () => Results.Text(OpenApiSpecs.ServiceG, "application/json"));
app.MapGet("/h/openapi.json", () => Results.Text(OpenApiSpecs.ServiceH, "application/json"));

app.Run();

record LoginRequest(string? Username, string? Password);

record OrderSearch(string? CustomerId, string? Status);

static class Jwt
{
    static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    static byte[] FromB64(string s) => Convert.FromBase64String(s.Replace('-', '+').Replace('_', '/').PadRight(s.Length + (4 - s.Length % 4) % 4, '='));

    public static string Create(byte[] key, string subject, TimeSpan life)
    {
        var header = B64(Encoding.UTF8.GetBytes("""{"alg":"HS256","typ":"JWT"}"""));
        var payload = B64(JsonSerializer.SerializeToUtf8Bytes(new { sub = subject, exp = DateTimeOffset.UtcNow.Add(life).ToUnixTimeSeconds() }));
        return $"{header}.{payload}.{B64(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes($"{header}.{payload}")))}";
    }

    /// <summary>The "sub" of a JWT, WITHOUT checking its signature (mock only), or null if it isn't a JWT.</summary>
    public static string? SubjectOf(string token)
    {
        var parts = token.Split('.');
        if (parts.Length != 3) return null;
        try
        {
            using var doc = JsonDocument.Parse(FromB64(parts[1]));
            return doc.RootElement.TryGetProperty("sub", out var sub) ? sub.GetString() : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException) { return null; }
    }

    public static bool IsValid(byte[] key, string token)
    {
        var parts = token.Split('.');
        if (parts.Length != 3) return false;
        var expected = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes($"{parts[0]}.{parts[1]}"));
        if (!CryptographicOperations.FixedTimeEquals(expected, FromB64(parts[2]))) return false;
        using var doc = JsonDocument.Parse(FromB64(parts[1]));
        return doc.RootElement.GetProperty("exp").GetInt64() > DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }
}

static class OpenApiSpecs
{
    public const string ServiceD = """
    {
      "openapi": "3.0.3",
      "info": { "title": "Service D (billing)", "version": "1.0.0" },
      "servers": [ { "url": "http://localhost:5100/d" } ],
      "components": { "securitySchemes": { "key": { "type": "apiKey", "name": "X-Api-Key", "in": "header" } } },
      "security": [ { "key": [] } ],
      "paths": {
        "/invoices": {
          "get": {
            "operationId": "listInvoices", "summary": "List invoices for a customer",
            "parameters": [ { "name": "customerId", "in": "query", "required": true, "schema": { "type": "string" } } ],
            "responses": { "200": { "description": "OK", "content": { "application/json": { "schema": {
              "type": "object",
              "properties": { "customerId": {"type":"string"}, "total": {"type":"integer"},
                "invoices": { "type": "array", "items": { "type": "object", "properties": { "id": {"type":"string"}, "amount": {"type":"number"}, "status": {"type":"string"} } } } }
            } } } } }
          }
        },
        "/invoices/{invoiceId}": {
          "get": {
            "operationId": "getInvoice", "summary": "Get one invoice",
            "parameters": [ { "name": "invoiceId", "in": "path", "required": true, "schema": { "type": "string" } } ],
            "responses": { "200": { "description": "OK" } }
          }
        }
      }
    }
    """;

    public const string ServiceA = """
    {
      "openapi": "3.0.3",
      "info": { "title": "Service A - Customers", "version": "1.0.0" },
      "servers": [ { "url": "http://localhost:5100/a" } ],
      "components": { "securitySchemes": { "basic": { "type": "http", "scheme": "basic" } } },
      "security": [ { "basic": [] } ],
      "paths": {
        "/customers/{customerId}": {
          "get": {
            "operationId": "getCustomer", "tags": ["customers"], "summary": "Get one customer",
            "parameters": [ { "name": "customerId", "in": "path", "required": true, "schema": { "type": "string" } } ],
            "responses": { "200": { "description": "OK", "content": { "application/json": { "schema": {
              "type": "object",
              "properties": { "id": {"type":"string"}, "name": {"type":"string"}, "accountNo": {"type":"string"}, "segmentCode": {"type":"string"}, "isPremium": {"type":"boolean"} }
            } } } } }
          }
        },
        "/customers/{customerId}/addresses": {
          "get": {
            "operationId": "getAddresses", "tags": ["customers"],
            "parameters": [ { "name": "customerId", "in": "path", "required": true, "schema": { "type": "string" } } ],
            "responses": { "200": { "description": "OK" } }
          }
        }
      }
    }
    """;

    public const string ServiceE = """
    {
      "openapi": "3.0.3",
      "info": { "title": "Service E - Identity", "version": "1.0.0" },
      "servers": [ { "url": "http://localhost:5100/e" } ],
      "components": { "securitySchemes": { "key": { "type": "apiKey", "name": "X-Api-Key", "in": "header" } } },
      "security": [ { "key": [] } ],
      "paths": {
        "/users": {
          "get": {
            "operationId": "listUsers", "summary": "List users",
            "responses": { "200": { "description": "OK", "content": { "application/json": { "schema": {
              "type": "object",
              "properties": { "users": { "type": "array", "items": { "type": "object", "properties": {
                "id": {"type":"string"}, "name": {"type":"string"}, "email": {"type":"string"}, "updatedAt": {"type":"string", "format":"date-time"} } } } }
            } } } } }
          }
        }
      }
    }
    """;

    public const string ServiceF = """
    {
      "openapi": "3.0.3",
      "info": { "title": "Service F - CRM", "version": "1.0.0" },
      "servers": [ { "url": "http://localhost:5100/f" } ],
      "components": { "securitySchemes": { "basic": { "type": "http", "scheme": "basic" } } },
      "security": [ { "basic": [] } ],
      "paths": {
        "/contacts": {
          "get": {
            "operationId": "listContacts", "summary": "List contacts",
            "responses": { "200": { "description": "OK", "content": { "application/json": { "schema": {
              "type": "object",
              "properties": { "contacts": { "type": "array", "items": { "type": "object", "properties": {
                "id": {"type":"string"}, "identityUserId": {"type":"string", "nullable": true}, "name": {"type":"string"},
                "email": {"type":"string"}, "phone": {"type":"string", "nullable": true}, "updatedAt": {"type":"string", "format":"date-time"} } } } }
            } } } } }
          }
        },
        "/contacts/{contactId}/notes": {
          "get": {
            "operationId": "getContactNotes", "summary": "Notes for one contact",
            "parameters": [ { "name": "contactId", "in": "path", "required": true, "schema": { "type": "string" } } ],
            "responses": { "200": { "description": "OK", "content": { "application/json": { "schema": {
              "type": "object",
              "properties": { "contactId": {"type":"string"}, "notes": { "type": "array", "items": { "type": "object", "properties": {
                "date": {"type":"string", "format":"date"}, "text": {"type":"string"} } } } }
            } } } } }
          }
        },
        "/orders": {
          "get": {
            "operationId": "listOrders", "summary": "List orders",
            "responses": { "200": { "description": "OK", "content": { "application/json": { "schema": {
              "type": "object",
              "properties": { "orders": { "type": "array", "items": { "type": "object", "properties": {
                "id": {"type":"string"}, "contactId": {"type":"string"}, "total": {"type":"number"} } } } }
            } } } } }
          }
        }
      }
    }
    """;

    public const string ServiceG = """
    {
      "openapi": "3.0.3",
      "info": { "title": "Service G - Billing", "version": "1.0.0" },
      "servers": [ { "url": "http://localhost:5100/g" } ],
      "components": { "securitySchemes": { "key": { "type": "apiKey", "name": "X-Api-Key", "in": "header" } } },
      "security": [ { "key": [] } ],
      "paths": {
        "/accounts": {
          "get": {
            "operationId": "listAccounts", "summary": "List billing accounts",
            "responses": { "200": { "description": "OK", "content": { "application/json": { "schema": {
              "type": "object",
              "properties": { "accounts": { "type": "array", "items": { "type": "object", "properties": {
                "accountNo": {"type":"string"}, "plan": {"type":"string"}, "status": {"type":"string"} } } } }
            } } } } }
          }
        }
      }
    }
    """;

    public const string ServiceH = """
    {
      "openapi": "3.0.3",
      "info": { "title": "Service H - Links (internal)", "version": "1.0.0" },
      "servers": [ { "url": "http://localhost:5100/h" } ],
      "paths": {
        "/links": {
          "get": {
            "operationId": "listLinks", "summary": "Billing account to Identity user links",
            "responses": { "200": { "description": "OK", "content": { "application/json": { "schema": {
              "type": "object",
              "properties": { "links": { "type": "array", "items": { "type": "object", "properties": {
                "billingAccountNo": {"type":"string"}, "userId": {"type":"string"} } } } }
            } } } } }
          }
        }
      }
    }
    """;
}
