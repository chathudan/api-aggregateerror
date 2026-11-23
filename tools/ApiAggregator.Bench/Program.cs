// bench - load, soak, fault and memory checks for the gateway. Guide: docs/guides/benchmarking.md
//
//   bench generate [--recipes 2000] [--services 50] [--mocks http://localhost:5100] [--out bench-results/config]
//       writes a config folder with that many endpoints (all valid), pointing at the mocks' /bench services
//   bench micro [--recipes 2000] [--services 50]
//       times the hot paths in this process (route match, planning, path language, OpenAPI document, config load)
//   bench run [--profile smoke|default|soak] [--recipes n] [--services n] [--callers n] [--workers n] [--soak-minutes n]
//             [--baseline bench-results/<run>/report.json] [--out bench-results] [--gcdump <path to dotnet-gcdump>]
//       starts the mocks, the gateway and the admin app with a generated config, runs every phase and writes
//       report.md and report.json. Exit code 1 when a check fails. With --gcdump, heap reports at the main checkpoints.
//
// Use scripts/bench.sh: it builds in Release first (timings from a Debug build mean little).

using System.Globalization;
using ApiAggregator.Bench;

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.WriteLine("Usage: bench generate|micro|run [options]. See the top of tools/ApiAggregator.Bench/Program.cs or docs/guides/benchmarking.md.");
    return 0;
}

var flags = Flags.Parse(args.Skip(1));
var repoRoot = Stack.FindRepoRoot();
var resultsRoot = Path.GetFullPath(flags.Get("out") ?? Path.Combine(repoRoot, "bench-results"));
var profile = Profile.Get(flags.Get("profile") ?? "default");
profile = profile with
{
    Recipes = flags.Int("recipes") ?? profile.Recipes,
    Services = flags.Int("services") ?? profile.Services,
    Callers = flags.Int("callers") ?? profile.Callers,
    Workers = flags.Int("workers") ?? profile.Workers,
    SoakMinutes = flags.Double("soak-minutes") ?? profile.SoakMinutes,
    SteadySeconds = flags.Int("steady-seconds") ?? profile.SteadySeconds
};

switch (args[0])
{
    case "generate":
    {
        var dir = Path.GetFullPath(flags.Get("out") ?? Path.Combine(repoRoot, "bench-results", "config"));
        var manifest = ConfigGenerator.Generate(dir, profile.Recipes, profile.Services, flags.Get("mocks") ?? "http://localhost:5100");
        Console.WriteLine($"Wrote {manifest.Count} endpoints over {profile.Services} services to {dir}");
        foreach (var shape in manifest.GroupBy(m => m.Shape).OrderBy(g => g.Key)) Console.WriteLine($"  {shape.Key}: {shape.Count()}");
        return 0;
    }

    case "micro":
    {
        var dir = Path.Combine(resultsRoot, "micro-config");
        var manifest = ConfigGenerator.Generate(dir, profile.Recipes, profile.Services, "http://localhost:5100");
        Console.WriteLine($"Hot paths with {manifest.Count} endpoints:");
        foreach (var m in await Micro.RunAsync(dir, manifest))
            Console.WriteLine($"  {m.Name,-50} {m.MicrosecondsPerOp,12:N2} µs/op {m.BytesPerOp,12:N0} B/op");
        return 0;
    }

    case "run":
    {
        var runDir = Path.Combine(resultsRoot, $"{DateTime.Now:yyyyMMdd-HHmmss}-{profile.Name}");
        var configDir = Path.Combine(runDir, "config");
        var baseline = flags.Get("baseline") is { } baselinePath ? Report.Load(baselinePath) : null;
        Console.WriteLine($"Benchmark '{profile.Name}': {profile.Recipes} endpoints, {profile.Services} services, {profile.Callers} callers, {profile.Workers} at once. Output: {runDir}");

        await using var stack = new Stack { RepoRoot = repoRoot, LogDir = Path.Combine(runDir, "logs") };
        var manifest = ConfigGenerator.Generate(configDir, profile.Recipes, profile.Services, stack.MocksUrl);

        Console.WriteLine("Timing hot paths in-process...");
        var micro = await Micro.RunAsync(configDir, manifest);

        Console.WriteLine("Starting the mocks, the gateway and the admin app...");
        await stack.StartAsync(configDir, new Dictionary<string, string>());
        var runner = new LoadRunner(stack, configDir, manifest, profile, Console.Out)
        {
            GcDumpTool = flags.Get("gcdump"),
            HeapReportDir = Path.Combine(runDir, "heap")
        };
        await runner.RunAsync();

        var report = Report.Build(runner, stack, profile, micro);
        Report.Save(report, runDir, baseline);
        Console.WriteLine();
        foreach (var check in report.Checks)
            Console.WriteLine($"{(check.Informational ? "INFO" : check.Passed ? "PASS" : "FAIL")}  {check.Check}: {check.Detail}");
        Console.WriteLine();
        Console.WriteLine($"{(report.Passed ? "PASSED" : "FAILED")}. Report: {Path.Combine(runDir, "report.md")}");
        return report.Passed ? 0 : 1;
    }

    default:
        Console.Error.WriteLine($"Unknown command '{args[0]}'. Use generate, micro or run.");
        return 2;
}

/// <summary>--name value pairs.</summary>
sealed class Flags
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public static Flags Parse(IEnumerable<string> args)
    {
        var flags = new Flags();
        var list = args.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            if (!list[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= list.Count)
                throw new ArgumentException($"Expected '--name value', got '{list[i]}'.");
            flags._values[list[i][2..]] = list[++i];
        }
        return flags;
    }

    public string? Get(string name) => _values.GetValueOrDefault(name);
    public int? Int(string name) => Get(name) is { } v ? int.Parse(v, CultureInfo.InvariantCulture) : null;
    public double? Double(string name) => Get(name) is { } v ? double.Parse(v, CultureInfo.InvariantCulture) : null;
}
