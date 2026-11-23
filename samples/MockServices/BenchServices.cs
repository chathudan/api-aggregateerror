using System.Collections.Concurrent;
using System.Text;

namespace MockServices;

/// <summary>
/// Generic services for the benchmark (tools/ApiAggregator.Bench, docs/guides/benchmarking.md): /bench/s01 ... /bench/sNN.
/// Answers are made up on the fly from the id, so any number of services and endpoints can point here.
/// Sign-in depends on the service number: n % 3 == 0 API key (X-Api-Key: key-bench), 1 Basic (bench / bench-pass), 2 none.
/// Faults can be switched on per service while a benchmark runs (POST /_chaos/sNN), to check the gateway copes.
/// Test data only.
/// </summary>
public static class BenchServices
{
    private static readonly ConcurrentDictionary<string, Chaos> Faults = new(StringComparer.OrdinalIgnoreCase);
    private static readonly string BasicExpected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("bench:bench-pass"));

    /// <summary>One fault setting. Several can be combined; a zero or false field is off.</summary>
    public sealed record Chaos(int DelayMs = 0, int JitterMs = 0, double FailRate = 0, int Status = 500, bool Hang = false, int BodyKb = 0, bool Drop = false);

    public static void MapBenchServices(this WebApplication app)
    {
        var bench = app.MapGroup("/bench/{svc}").AddEndpointFilter(async (ctx, next) =>
        {
            var http = ctx.HttpContext;
            var svc = (string)http.GetRouteValue("svc")!;
            if (!SignedIn(svc, http.Request)) return Results.Unauthorized();
            if (Faults.TryGetValue(svc, out var chaos))
            {
                if (chaos.Hang) await Task.Delay(Timeout.Infinite, http.RequestAborted);
                var delay = chaos.DelayMs + (chaos.JitterMs > 0 ? Random.Shared.Next(chaos.JitterMs) : 0);
                if (delay > 0) await Task.Delay(delay, http.RequestAborted);
                if (chaos.Drop) { http.Abort(); return Results.Empty; }
                if (chaos.FailRate > 0 && Random.Shared.NextDouble() < chaos.FailRate)
                    return Results.Json(new { message = "injected failure" }, statusCode: chaos.Status);
                if (chaos.BodyKb > 0)
                {
                    // Sent without a length, in pieces, like a service streaming a huge answer: the gateway can't refuse
                    // it up front and has to stop reading at its limit. The bytes are made once, so the mock stays cheap.
                    var body = BigBodies.GetOrAdd(chaos.BodyKb, BigBody);
                    http.Response.ContentType = "application/json";
                    for (var at = 0; at < body.Length; at += 64 * 1024)
                        await http.Response.Body.WriteAsync(body.AsMemory(at, Math.Min(64 * 1024, body.Length - at)), http.RequestAborted);
                    return Results.Empty;
                }
            }
            return await next(ctx);
        });

        bench.MapGet("/items/{itemId}", (string svc, string itemId) => Item(svc, itemId));
        bench.MapGet("/items/{itemId}/details", (string svc, string itemId) => new
        {
            id = itemId,
            service = svc,
            description = $"Details of {itemId}",
            tags = new[] { "alpha", "beta", "gamma" },
            updatedAt = "2026-01-01T00:00:00Z"
        });
        bench.MapGet("/items", (string svc, string? category, int? n) => new
        {
            category = category ?? "all",
            items = Enumerable.Range(1, Math.Clamp(n ?? 5, 0, 50)).Select(i => new { id = $"I-{i:0000}", rank = i }).ToArray()
        });
        bench.MapPost("/search", (string svc, SearchRequest? body) => new
        {
            term = body?.Term ?? "",
            total = 3,
            items = Enumerable.Range(1, 3).Select(i => Item(svc, $"{body?.Term}-{i}")).ToArray()
        });

        // Faults: local benchmark only (the mocks are never deployed with real services).
        app.MapPost("/_chaos/{svc}", (string svc, Chaos chaos) => { Faults[svc] = chaos; return Results.NoContent(); });
        app.MapDelete("/_chaos", () => { Faults.Clear(); return Results.NoContent(); });
    }

    private static bool SignedIn(string svc, HttpRequest request)
    {
        if (svc.Length < 2 || !int.TryParse(svc.AsSpan(1), out var n)) return false;
        return (n % 3) switch
        {
            0 => request.Headers["X-Api-Key"] == "key-bench",
            1 => request.Headers.Authorization == BasicExpected,
            _ => true
        };
    }

    private static object Item(string svc, string id) => new
    {
        id,
        service = svc,
        name = $"Item {id}",
        category = "c" + (id.Length % 5),
        price = 10 + id.Length,
        active = true,
        related = new[] { $"{id}-a", $"{id}-b", $"{id}-c" }
    };

    private static readonly ConcurrentDictionary<int, byte[]> BigBodies = new();

    private static byte[] BigBody(int kb)
    {
        var sb = new StringBuilder(kb * 1024 + 32).Append("{\"padding\":\"");
        sb.Append('x', kb * 1024);
        return Encoding.UTF8.GetBytes(sb.Append("\"}").ToString());
    }

    public sealed record SearchRequest(string? Term);
}
