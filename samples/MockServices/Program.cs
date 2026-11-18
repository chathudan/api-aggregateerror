// Mock downstream services for local development and QA:
//   /a  Service A - customers       - Basic auth            (svc-a / pass-a)
//   /b  Service B - orders          - session cookie login  (svc-b / pass-b)
//   /c  Service C - recommendations - OAuth client credentials -> JWT (client-c / secret-c)
//   /d  Service D - billing         - API key (X-Api-Key: key-d), has an OpenAPI spec at /d/openapi.json
// GET /_stats shows how many times each endpoint was called (handy for checking the cache).
// Test data only. No real customer or patient data belongs here.

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

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

app.Run();

record LoginRequest(string? Username, string? Password);

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
}
