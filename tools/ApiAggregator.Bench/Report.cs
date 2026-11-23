using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using ApiAggregator.Engine.Config;

namespace ApiAggregator.Bench;

public sealed record PhaseRow(
    string Name, int Workers, long Requests, double Rps, double P50, double P95, double P99, double Max,
    Dictionary<string, long> Statuses, Dictionary<string, long> Codes, long Partial, long CacheHits, long Shed, long Unexpected, List<string> Examples)
{
    public static PhaseRow From(PhaseStats s) => new(
        s.Name, s.Workers, s.Latency.Count, Math.Round(s.RequestsPerSecond, 1), Math.Round(s.Latency.PercentileMs(0.50), 2),
        Math.Round(s.Latency.PercentileMs(0.95), 2), Math.Round(s.Latency.PercentileMs(0.99), 2), Math.Round(s.Latency.MaxMs, 2),
        s.Statuses.OrderBy(k => k.Key).ToDictionary(k => k.Key.ToString(CultureInfo.InvariantCulture), k => k.Value),
        s.Codes.OrderBy(k => k.Key).ToDictionary(k => k.Key, k => k.Value), s.Partial, s.CacheHits, s.Shed, s.UnexpectedCount, s.Unexpected.ToList());
}

public sealed record FaultRow(string Fault, string Description, string Service, PhaseRow Affected, PhaseRow Others);
public sealed record AbuseRow(string Case, string Expected, PhaseRow Result);
public sealed record ProbeRow(string What, double Ms, int Status);
public sealed record CheckRow(string Check, bool Passed, string Detail, bool Informational = false);

public sealed record BenchReport(
    string Profile, DateTimeOffset At, string Machine, string Commit, int Recipes, int Services, int Callers, int Workers,
    List<CheckRow> Checks, List<PhaseRow> Phases, List<PhaseRow> Ramp, List<FaultRow> Faults, List<AbuseRow> Abuse,
    List<Sample> Checkpoints, List<Sample> Samples, List<ProbeRow> Probes, List<MicroResult> Micro,
    double? HeapSlopeMbPer10Min, int ConfigChanges, long InfoLogLines, string? Crash, string ChaosService)
{
    public bool Passed => Checks.Where(c => !c.Informational).All(c => c.Passed);
}

/// <summary>Turns what the run saw into pass/fail checks, a readable report.md and a report.json for later comparison.</summary>
public static class Report
{
    private static readonly string[] NormalPhases = ["warm-up", "steady", "after faults", "config churn", "abuse", "soak", "idle"];

    public static BenchReport Build(LoadRunner run, Stack stack, Profile profile, List<MicroResult> micro)
    {
        var phases = run.Phases.Where(p => !p.Name.StartsWith("fault:", StringComparison.Ordinal)).Select(PhaseRow.From).ToList();
        var faults = run.Chaos.Select(c => new FaultRow(c.Fault.Name, c.Fault.Description, c.Service, PhaseRow.From(c.Affected), PhaseRow.From(c.Others))).ToList();
        var abuse = run.Abuse.Select(a => new AbuseRow(a.Case, a.Expected, PhaseRow.From(a.Stats))).ToList();
        var checkpoints = run.Checkpoints;
        var samples = run.Samples.ToList();

        // Memory trend: the heap after a full collection, read every minute of the soak (a line fitted through them).
        var soak = checkpoints.Where(c => c.Phase.StartsWith("soak", StringComparison.Ordinal)).ToList();
        double? slope = soak.Count >= 5 ? Slope(soak) * 600 : null;
        // A short soak gives few, noisy readings: allow more before calling it growth.
        var slopeLimit = soak.Count >= 20 ? 2.0 : 10.0;
        var afterWarmup = checkpoints.FirstOrDefault(c => c.Phase == "after warm-up");
        var afterIdle = checkpoints.FirstOrDefault(c => c.Phase == "after idle");

        var checks = new List<CheckRow>
        {
            new("The gateway never stopped", run.Crash is null && stack.GatewayAlive, run.Crash ?? "Running from start to end."),
            Unexpected("Normal load: every answer is a full 200", phases.Where(p => NormalPhases.Contains(p.Name))),
            Unexpected($"A broken service ({run.ChaosService}) only affects the endpoints that need it", faults.SelectMany(f => new[] { f.Affected, f.Others })),
            faults.Count > 0
                ? new($"Other endpoints stay fast while {run.ChaosService} is broken (p99 under {profile.P99LimitMs} ms)", faults.All(f => f.Others.P99 <= profile.P99LimitMs),
                    string.Join("; ", faults.Select(f => $"{f.Fault} {f.Others.P99} ms")) + ".")
                : new("Other endpoints stay fast while a service is broken", false, "No fault phase ran."),
            Unexpected("Bad and abusive requests are turned away with 4xx, never 5xx", abuse.Select(a => a.Result)),
            phases.FirstOrDefault(p => p.Name == "steady") is { } steady
                ? new($"p99 under {profile.P99LimitMs} ms at steady load ({steady.Workers} at once)", steady.P99 <= profile.P99LimitMs,
                    $"p99 {steady.P99} ms, {steady.Rps} requests/s, about {steady.Rps * 60 / profile.Callers:0} per caller per minute.")
                : new("p99 at steady load", false, "No steady phase ran."),
            slope is { } s
                ? new("Memory doesn't grow during the soak", s < slopeLimit, $"Heap after full GC changes by {s:+0.00;-0.00} MB per 10 min over {soak.Count} readings (limit +{slopeLimit} MB).")
                : new("Memory doesn't grow during the soak", true, "Not measured: the soak was too short (use --profile soak).", Informational: true),
            afterWarmup is not null && afterIdle is not null
                // Some bounded memory rightly stays: the OpenAPI document for a few addresses, the pool of read buffers.
                ? new("Memory comes back when traffic stops", afterIdle.HeapMb <= Math.Max(afterWarmup.HeapMb * 1.25, afterWarmup.HeapMb + 50),
                    $"Heap after full GC: {afterWarmup.HeapMb:0.0} MB after warm-up, {afterIdle.HeapMb:0.0} MB after idle (limit +25% or +50 MB).")
                : new("Memory comes back when traffic stops", false, "Missing readings (is Gateway:Diagnostics:Enabled on?)."),
            samples.Count > 0
                ? new("Threads stay bounded", samples.Max(x => x.Threads) < 500, $"At most {samples.Max(x => x.Threads)} threads (limit 500); thread-pool queue at most {samples.Max(x => x.ThreadPoolQueue)}.")
                : new("Threads stay bounded", false, "No readings.")
        };

        return new BenchReport(profile.Name, DateTimeOffset.Now, Machine(), Commit(stack.RepoRoot), profile.Recipes, profile.Services, profile.Callers, profile.Workers,
            checks, phases, run.Ramp.Select(PhaseRow.From).ToList(), faults, abuse, checkpoints, samples,
            run.Probes.Select(p => new ProbeRow(p.What, Math.Round(p.Ms, 1), p.Status)).ToList(), micro,
            slope is null ? null : Math.Round(slope.Value, 3), run.ConfigChanges, stack.InfoLines, run.Crash, run.ChaosService);
    }

    private static CheckRow Unexpected(string name, IEnumerable<PhaseRow> rows)
    {
        var list = rows.ToList();
        var count = list.Sum(r => r.Unexpected);
        var requests = list.Sum(r => r.Requests);
        var first = list.SelectMany(r => r.Examples.Select(e => $"{r.Name}: {e}")).FirstOrDefault();
        return new(name, count == 0 && requests > 0, count == 0 ? $"{requests:N0} requests, none unexpected." : $"{count:N0} of {requests:N0} unexpected, e.g. {first}");
    }

    /// <summary>Least-squares slope of the heap (MB per second).</summary>
    private static double Slope(List<Sample> points)
    {
        var mx = points.Average(p => p.AtSeconds);
        var my = points.Average(p => p.HeapMb);
        var num = points.Sum(p => (p.AtSeconds - mx) * (p.HeapMb - my));
        var den = points.Sum(p => (p.AtSeconds - mx) * (p.AtSeconds - mx));
        return den == 0 ? 0 : num / den;
    }

    public static void Save(BenchReport report, string dir, BenchReport? baseline)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "report.json"), JsonSerializer.Serialize(report, AggregatorJson.Options));
        File.WriteAllText(Path.Combine(dir, "report.md"), Markdown(report, baseline));
    }

    public static BenchReport? Load(string path) =>
        JsonSerializer.Deserialize<BenchReport>(File.ReadAllText(path), AggregatorJson.Options);

    // ------------------------------------------------------------------ report.md

    public static string Markdown(BenchReport r, BenchReport? baseline)
    {
        var md = new StringBuilder();
        md.AppendLine($"# Benchmark: {(r.Passed ? "PASSED" : "FAILED")}");
        md.AppendLine();
        md.AppendLine($"Profile **{r.Profile}**, {r.At:yyyy-MM-dd HH:mm}, commit `{r.Commit}`. {r.Recipes:N0} endpoints over {r.Services} services, " +
                      $"{r.Callers:N0} callers, {r.Workers} requests at once. Machine: {r.Machine}.");
        md.AppendLine();
        md.AppendLine("Everything runs on one machine (mocks, gateway, admin app and the load), so the numbers compare runs; they are not a capacity promise.");
        md.AppendLine();

        md.AppendLine("## Checks");
        md.AppendLine();
        md.AppendLine("| | Check | Result |");
        md.AppendLine("|---|---|---|");
        foreach (var c in r.Checks)
            md.AppendLine($"| {(c.Informational ? "ℹ️" : c.Passed ? "✅" : "❌")} | {c.Check} | {Cell(c.Detail)} |");
        md.AppendLine();

        if (baseline is not null) Compare(md, r, baseline);

        md.AppendLine("## Phases");
        md.AppendLine();
        PhaseTable(md, r.Phases);

        if (r.Ramp.Count > 0)
        {
            md.AppendLine("## Ramp (where throughput stops growing)");
            md.AppendLine();
            PhaseTable(md, r.Ramp);
        }

        md.AppendLine($"## Faults in {r.ChaosService}");
        md.AppendLine();
        md.AppendLine("Endpoints that need the service may fail with 502/503/504; endpoints that use it for an optional step must answer 200 (partial); all others must not notice.");
        md.AppendLine();
        md.AppendLine("| Fault | What the service does | Endpoints | Requests | Statuses | Codes | Partial | p99 ms | Unexpected |");
        md.AppendLine("|---|---|---|---:|---|---|---:|---:|---:|");
        foreach (var f in r.Faults)
        {
            md.AppendLine($"| {f.Fault} | {f.Description} | that use it | {f.Affected.Requests:N0} | {Counts(f.Affected.Statuses)} | {Counts(f.Affected.Codes)} | {f.Affected.Partial:N0} | {f.Affected.P99} | {f.Affected.Unexpected} |");
            md.AppendLine($"| | | all others | {f.Others.Requests:N0} | {Counts(f.Others.Statuses)} | {Counts(f.Others.Codes)} | {f.Others.Partial:N0} | {f.Others.P99} | {f.Others.Unexpected} |");
        }
        md.AppendLine();

        md.AppendLine("## Bad and abusive requests (sent during normal load)");
        md.AppendLine();
        md.AppendLine("| Case | Expected | Requests | Statuses | p99 ms | Unexpected |");
        md.AppendLine("|---|---|---:|---|---:|---:|");
        foreach (var a in r.Abuse)
            md.AppendLine($"| {a.Case} | {a.Expected} | {a.Result.Requests:N0} | {Counts(a.Result.Statuses)} | {a.Result.P99} | {a.Result.Unexpected} |");
        md.AppendLine();

        md.AppendLine("## Memory and threads (readings after a full garbage collection)");
        md.AppendLine();
        md.AppendLine($"Heap trend during the soak: {(r.HeapSlopeMbPer10Min is { } s ? $"{s:+0.000;-0.000} MB per 10 min" : "not measured (soak too short)")}. " +
                      $"Config changes published during the run: {r.ConfigChanges}. Information log lines written by the apps: {r.InfoLogLines:N0}.");
        md.AppendLine();
        md.AppendLine("| When | Heap MB | Working set MB | Committed MB | Threads | Gen2 GCs | Exceptions | Allocated MB |");
        md.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var c in r.Checkpoints)
            md.AppendLine($"| {c.Phase} | {c.HeapMb:0.0} | {c.WorkingSetMb:0.0} | {c.CommittedMb:0.0} | {c.Threads} | {c.Gen2} | {c.Exceptions:N0} | {c.AllocatedMb:N0} |");
        md.AppendLine();
        if (r.Samples.Count > 0)
        {
            md.AppendLine("Peaks under load: " +
                          $"working set {r.Samples.Max(x => x.WorkingSetMb):0} MB, heap {r.Samples.Max(x => x.HeapMb):0} MB, threads {r.Samples.Max(x => x.Threads)}, " +
                          $"thread-pool queue {r.Samples.Max(x => x.ThreadPoolQueue)}, requests in flight {r.Samples.Max(x => x.InFlight)}, CPU {r.Samples.Max(x => x.CpuPercent):0}% of the machine.");
            md.AppendLine();
        }

        md.AppendLine("## Probes");
        md.AppendLine();
        md.AppendLine("| What | ms | Status |");
        md.AppendLine("|---|---:|---:|");
        foreach (var p in r.Probes) md.AppendLine($"| {p.What} | {p.Ms} | {p.Status} |");
        md.AppendLine();

        if (r.Micro.Count > 0)
        {
            md.AppendLine("## Hot paths in-process (bench micro)");
            md.AppendLine();
            md.AppendLine("| Operation | µs per op | Bytes allocated per op |");
            md.AppendLine("|---|---:|---:|");
            foreach (var m in r.Micro) md.AppendLine($"| {m.Name} | {m.MicrosecondsPerOp:N2} | {m.BytesPerOp:N0} |");
            md.AppendLine();
        }

        var examples = r.Phases.Concat(r.Faults.SelectMany(f => new[] { f.Affected, f.Others })).Concat(r.Abuse.Select(a => a.Result))
            .SelectMany(p => p.Examples.Select(e => $"{p.Name}: {e}")).Take(40).ToList();
        if (examples.Count > 0)
        {
            md.AppendLine("## Unexpected answers (first examples)");
            md.AppendLine();
            foreach (var e in examples) md.AppendLine($"- {e}");
            md.AppendLine();
        }
        return md.ToString();
    }

    private static void Compare(StringBuilder md, BenchReport now, BenchReport before)
    {
        md.AppendLine($"## Compared with the baseline ({before.At:yyyy-MM-dd HH:mm}, commit `{before.Commit}`)");
        md.AppendLine();
        md.AppendLine("| | Before | Now |");
        md.AppendLine("|---|---:|---:|");
        foreach (var c in now.Checks)
            if (before.Checks.FirstOrDefault(b => b.Check == c.Check) is { } b && !c.Informational)
                md.AppendLine($"| {c.Check} | {(b.Passed ? "✅" : "❌")} | {(c.Passed ? "✅" : "❌")} |");
        foreach (var p in now.Phases)
            if (before.Phases.FirstOrDefault(b => b.Name == p.Name) is { } b)
                md.AppendLine($"| {p.Name}: requests/s · p99 ms · unexpected | {b.Rps:N0} · {b.P99} · {b.Unexpected:N0} | {p.Rps:N0} · {p.P99} · {p.Unexpected:N0} |");
        foreach (var f in now.Faults)
            if (before.Faults.FirstOrDefault(b => b.Fault == f.Fault) is { } b)
                md.AppendLine($"| fault {f.Fault}: unexpected · p99 ms of the other endpoints | {b.Affected.Unexpected + b.Others.Unexpected:N0} · {b.Others.P99} | {f.Affected.Unexpected + f.Others.Unexpected:N0} · {f.Others.P99} |");
        string Heap(BenchReport x, string phase) => x.Checkpoints.FirstOrDefault(c => c.Phase == phase) is { } c ? $"{c.HeapMb:0.0}" : "-";
        foreach (var phase in new[] { "after warm-up", "after churn", "after idle" })
            md.AppendLine($"| Heap MB {phase} | {Heap(before, phase)} | {Heap(now, phase)} |");
        md.AppendLine($"| Heap trend MB / 10 min (soak) | {before.HeapSlopeMbPer10Min?.ToString("0.000") ?? "-"} | {now.HeapSlopeMbPer10Min?.ToString("0.000") ?? "-"} |");
        foreach (var m in now.Micro)
            if (before.Micro.FirstOrDefault(b => b.Name == m.Name) is { } b)
                md.AppendLine($"| {m.Name}: µs · bytes per op | {b.MicrosecondsPerOp:N2} · {b.BytesPerOp:N0} | {m.MicrosecondsPerOp:N2} · {m.BytesPerOp:N0} |");
        md.AppendLine();
    }

    private static void PhaseTable(StringBuilder md, List<PhaseRow> rows)
    {
        md.AppendLine("| Phase | At once | Requests | Req/s | p50 ms | p95 ms | p99 ms | Max ms | Statuses | Cache hits | Shed (503) | Unexpected |");
        md.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---|---:|---:|---:|");
        foreach (var p in rows)
            md.AppendLine($"| {p.Name} | {p.Workers} | {p.Requests:N0} | {p.Rps:N0} | {p.P50} | {p.P95} | {p.P99} | {p.Max} | {Counts(p.Statuses)} | {p.CacheHits:N0} | {p.Shed:N0} | {p.Unexpected:N0} |");
        md.AppendLine();
    }

    private static string Counts(Dictionary<string, long> counts) =>
        counts.Count == 0 ? "-" : string.Join(", ", counts.Select(k => $"{k.Key}: {k.Value:N0}"));

    private static string Cell(string text) => text.Replace("|", "\\|").Replace("\n", " ");

    private static string Machine() =>
        $"{Environment.ProcessorCount} cores, {RuntimeInformation.OSDescription.Trim()}, {RuntimeInformation.FrameworkDescription}, {RuntimeInformation.ProcessArchitecture}";

    private static string Commit(string repoRoot)
    {
        try
        {
            using var git = Process.Start(new ProcessStartInfo("git", ["describe", "--always", "--dirty"])
            { WorkingDirectory = repoRoot, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!;
            var text = git.StandardOutput.ReadToEnd().Trim();
            git.WaitForExit();
            return text.Length > 0 ? text : "unknown";
        }
        catch (System.ComponentModel.Win32Exception) { return "unknown"; }
    }
}
