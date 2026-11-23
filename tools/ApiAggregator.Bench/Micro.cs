using System.Diagnostics;
using System.Text.Json.Nodes;
using ApiAggregator.Engine.Authoring;
using ApiAggregator.Engine.Config;
using ApiAggregator.Engine.Expressions;
using ApiAggregator.Engine.Planning;
using ApiAggregator.Engine.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ApiAggregator.Bench;

/// <summary>One in-process timing: average time and memory allocated per operation.</summary>
public sealed record MicroResult(string Name, int Operations, double MicrosecondsPerOp, double BytesPerOp);

/// <summary>
/// Times the gateway's hot paths in this process, against a generated config: route matching, planning, the path
/// language, the OpenAPI document and loading the config. Plain Stopwatch timings after a warm-up, one thread.
/// </summary>
public static class Micro
{
    public static async Task<List<MicroResult>> RunAsync(string configDir, List<BenchEndpoint> manifest)
    {
        var store = new FileConfigStore(Options.Create(new FileConfigOptions { Directory = configDir }), NullLogger<FileConfigStore>.Instance);
        var snapshot = await store.GetSnapshotAsync();
        var router = new RecipeRouter(store);
        var paths = manifest.Select(e => e.Route.Replace("{id}", "I-0042").Replace("{sub}", "x1")).ToArray();
        var results = new List<MicroResult>();

        // Route matching: every endpoint in turn (late ones are the slow case for a scan), and an address that matches nothing.
        var n = 0;
        results.Add(await TimeAsync("Route match (all endpoints in turn)", 20_000, async () => { await router.MatchAsync("GET", paths[n++ % paths.Length], default); }));
        results.Add(await TimeAsync("Route match (no such endpoint)", 5_000, async () => { await router.MatchAsync("GET", "nowhere/at/all", default); }));

        var recipes = snapshot.Recipes.ToArray();
        n = 0;
        results.Add(Time("Plan an endpoint", 20_000, () => ExecutionPlanner.Plan(recipes[n++ % recipes.Length])));

        var scope = new Scope(new Dictionary<string, JsonNode?>
        {
            ["item"] = JsonNode.Parse("""{"status":200,"body":{"id":"I-1","name":"Item","related":["a","b","c"],"price":12}}""")
        });
        results.Add(Time("Path language: {{item.body.related[*]}}", 100_000, () => Template.Resolve("{{item.body.related[*]}}", scope)));

        results.Add(Time($"OpenAPI document ({recipes.Length} endpoints)", 5, () => OpenApiDocumentBuilder.Build(snapshot, new OpenApiBuildOptions { ServerUrl = "http://localhost" }).ToJsonString()));

        results.Add(await TimeAsync($"Load the config ({recipes.Length} endpoints)", 3, async () =>
        {
            var fresh = new FileConfigStore(Options.Create(new FileConfigOptions { Directory = configDir }), NullLogger<FileConfigStore>.Instance);
            await fresh.GetSnapshotAsync();
        }));
        return results;
    }

    private static MicroResult Time(string name, int operations, Action op) =>
        TimeAsync(name, operations, () => { op(); return Task.CompletedTask; }).GetAwaiter().GetResult();

    private static async Task<MicroResult> TimeAsync(string name, int operations, Func<Task> op)
    {
        for (var i = 0; i < Math.Min(operations, 1000); i++) await op(); // warm-up: JIT and caches
        GC.Collect();
        var allocated = GC.GetTotalAllocatedBytes(precise: true);
        var clock = Stopwatch.StartNew();
        for (var i = 0; i < operations; i++) await op();
        clock.Stop();
        var bytes = GC.GetTotalAllocatedBytes(precise: true) - allocated;
        return new MicroResult(name, operations, clock.Elapsed.TotalMicroseconds / operations, (double)bytes / operations);
    }
}
