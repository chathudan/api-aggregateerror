using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using ApiAggregator.Engine.Config;
using ApiAggregator.Engine.Model;

namespace ApiAggregator.Bench;

/// <summary>How long each phase runs and how hard. Picked with --profile, any value can be overridden by a flag.</summary>
public sealed record Profile(
    string Name, int WarmupSeconds, int[] RampLevels, int RampSeconds, int SteadySeconds, int ChaosSeconds, int ChaosRecoverySeconds,
    int ChurnSeconds, int AbuseSeconds, double SoakMinutes, int IdleSeconds, int Workers, int Callers, int Recipes, int Services, double P99LimitMs)
{
    public static Profile Get(string name) => name switch
    {
        "smoke" => new("smoke", 10, [25, 100, 200], 8, 30, 10, 6, 20, 15, 0, 70, 200, 5000, 500, 20, 1500),
        "default" => new("default", 30, [25, 50, 100, 200, 400], 20, 120, 20, 10, 60, 30, 6, 70, 200, 5000, 2000, 50, 1000),
        "soak" => new("soak", 30, [50, 200], 20, 120, 20, 10, 60, 30, 30, 70, 200, 5000, 2000, 50, 1000),
        _ => throw new ArgumentException($"Unknown profile '{name}'. Use smoke, default or soak.")
    };
}

/// <summary>A fault switched on in one service of the mocks (BenchServices.Chaos).</summary>
public sealed record Fault(string Name, string Description, object Settings);

/// <summary>Says whether one answer is what the gateway should give in this phase. Null: fine; otherwise why not.</summary>
public delegate string? Judge(BenchEndpoint endpoint, int status, string? code, bool partial);

/// <summary>
/// Drives the gateway through the phases (warm-up, ramp, steady, faults, config churn, abuse, soak, idle) with many
/// callers at once, and reads the gateway's memory, threads and CPU all along. Everything it sees goes into the report.
/// </summary>
public sealed class LoadRunner(Stack stack, string configDir, List<BenchEndpoint> endpoints, Profile profile, TextWriter progress)
{
    /// <summary>
    /// Optional path to the dotnet-gcdump tool. When set, the main checkpoints also save a heap report (the largest types
    /// by size and count) next to report.md: the first place to look when memory doesn't come back.
    /// </summary>
    public string? GcDumpTool { get; init; }

    public string? HeapReportDir { get; init; }

    private readonly HttpClient _http = new(new SocketsHttpHandler
    {
        MaxConnectionsPerServer = int.MaxValue,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        UseCookies = false,
        AllowAutoRedirect = false
    }) { Timeout = TimeSpan.FromSeconds(30) };

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<BenchEndpoint> _hot = [];
    private string[] _tokens = [];
    private string _authorToken = "", _wrongScopeToken = "";
    private volatile string _phase = "start";
    private TimeSpan _lastCpu;
    private double _lastCpuAt;

    public List<PhaseStats> Phases { get; } = [];
    public List<PhaseStats> Ramp { get; } = [];
    public List<(Fault Fault, string Service, PhaseStats Affected, PhaseStats Others)> Chaos { get; } = [];
    public List<(string Case, string Expected, PhaseStats Stats)> Abuse { get; } = [];
    public List<Sample> Samples { get; } = [];
    public List<Sample> Checkpoints { get; } = [];
    public List<(string What, double Ms, int Status)> Probes { get; } = [];
    public int ConfigChanges { get; private set; }
    public string? Crash { get; private set; }
    public string ChaosService { get; private set; } = "";

    public async Task RunAsync()
    {
        // The popular tenth: every tenth endpoint, so every run (and its baseline) favours the same ones.
        _hot.AddRange(endpoints.Where((_, i) => i % 10 == 3));
        if (_hot.Count == 0) _hot.AddRange(endpoints);
        await GetTokensAsync();
        using var sampling = new CancellationTokenSource();
        var sampler = SampleLoopAsync(sampling.Token);

        try
        {
            await CheckpointAsync("start");
            await PhaseAsync("warm-up", profile.WarmupSeconds, profile.Workers, Normal);
            await CheckpointAsync("after warm-up");

            foreach (var level in profile.RampLevels)
                Ramp.Add(await PhaseAsync($"ramp {level}", profile.RampSeconds, level, Normal, keep: false));

            await PhaseAsync("steady", profile.SteadySeconds, profile.Workers, Normal, during: ProbeAsync);
            await CheckpointAsync("after steady");

            await ChaosAsync();
            await CheckpointAsync("after faults");

            await PhaseAsync("config churn", profile.ChurnSeconds, profile.Workers, Normal, during: ct => ChurnAsync(TimeSpan.FromSeconds(2), ct));
            await CheckpointAsync("after churn");

            await PhaseAsync("abuse", profile.AbuseSeconds, profile.Workers, Normal, during: AbuseAsync);

            if (profile.SoakMinutes > 0)
            {
                // Hours of traffic in miniature: steady load, an endpoint published every 10 s, a full GC reading every minute.
                await PhaseAsync("soak", (int)(profile.SoakMinutes * 60), profile.Workers, Normal, during: async ct =>
                {
                    var churn = ChurnAsync(TimeSpan.FromSeconds(10), ct);
                    var minute = 0;
                    while (!ct.IsCancellationRequested)
                    {
                        try { await Task.Delay(TimeSpan.FromSeconds(60), ct); } catch (OperationCanceledException) { break; }
                        await CheckpointAsync($"soak {++minute} min");
                    }
                    await churn;
                });
            }

            // Idle: a trickle of traffic (expired cache entries are cleared when the cache is used), then the final reading.
            await PhaseAsync("idle", profile.IdleSeconds, 1, Normal, pauseMs: 1000);
            await CheckpointAsync("after idle");
            await ProbeAsync(CancellationToken.None);
        }
        catch (GatewayDownException ex) { Crash = ex.Message; }
        finally
        {
            sampling.Cancel();
            await sampler;
        }
    }

    // ------------------------------------------------------------------ phases

    private async Task<PhaseStats> PhaseAsync(string name, int seconds, int workers, Judge judge, Func<CancellationToken, Task>? during = null,
        bool keep = true, int pauseMs = 0, Func<BenchEndpoint>? pick = null)
    {
        _phase = name;
        var stats = new PhaseStats(name) { Workers = workers };
        progress.WriteLine($"  {name}: {workers} worker(s) for {seconds} s");
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        var started = _clock.Elapsed;
        var tasks = Enumerable.Range(0, workers).Select(_ => WorkerAsync(stats, judge, pauseMs, pick ?? PickAny, stop.Token)).ToList();
        if (during is not null) tasks.Add(during(stop.Token));
        await Task.WhenAll(tasks);
        stats.Seconds = (_clock.Elapsed - started).TotalSeconds;
        if (keep) lock (Phases) Phases.Add(stats);
        progress.WriteLine($"    {stats.RequestsPerSecond:0} req/s, p99 {stats.Latency.PercentileMs(0.99):0.#} ms, unexpected {stats.UnexpectedCount}");
        if (!stack.GatewayAlive) throw new GatewayDownException($"The gateway stopped during '{name}' (exit code {stack.GatewayExitCode}).");
        return stats;
    }

    /// <summary>Like real traffic: 80% of requests go to a tenth of the endpoints.</summary>
    private BenchEndpoint PickAny() =>
        Random.Shared.NextDouble() < 0.8 ? _hot[Random.Shared.Next(_hot.Count)] : endpoints[Random.Shared.Next(endpoints.Count)];

    private async Task WorkerAsync(PhaseStats stats, Judge judge, int pauseMs, Func<BenchEndpoint> pick, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            var endpoint = pick();
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{stack.GatewayUrl}/bff/{Fill(endpoint.Route)}");
            request.Headers.Authorization = new("Bearer", _tokens[Random.Shared.Next(_tokens.Length)]);
            var (status, code, partial, hit, micros) = await SendAsync(request);
            stats.Latency.Record(micros);
            stats.Count(status, code, partial, hit);
            if (judge(endpoint, status, code, partial) is { } problem) stats.Flag($"{status} {code} on {endpoint.Id} ({endpoint.Shape}): {problem}");
            if (pauseMs > 0) { try { await Task.Delay(pauseMs, stop); } catch (OperationCanceledException) { } }
        }
    }

    /// <summary>
    /// Sends one request and reads the whole answer. In-flight requests are never cancelled when a phase ends: a request
    /// the gateway doesn't answer shows up as a client timeout, which is a finding.
    /// </summary>
    private async Task<(int Status, string? Code, bool Partial, bool CacheHit, double Micros)> SendAsync(HttpRequestMessage request)
    {
        var start = Stopwatch.GetTimestamp();
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            var status = (int)response.StatusCode;
            string? code = null;
            if (status >= 400) code = ProblemCode(await response.Content.ReadAsStringAsync());
            else await response.Content.CopyToAsync(Stream.Null);
            return (status, code, response.Headers.Contains("X-Aggregator-Partial"), response.Headers.Contains("X-Aggregator-Cache"),
                Stopwatch.GetElapsedTime(start).TotalMicroseconds);
        }
        catch (HttpRequestException ex) { return (0, "client:" + ex.HttpRequestError, false, false, Stopwatch.GetElapsedTime(start).TotalMicroseconds); }
        catch (TaskCanceledException) { return (0, "client:timeout", false, false, Stopwatch.GetElapsedTime(start).TotalMicroseconds); }
    }

    private static string? ProblemCode(string body)
    {
        try { return JsonNode.Parse(body)?["code"]?.GetValue<string>(); }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException) { return body.Length == 0 ? null : "not-json"; }
    }

    private static string Fill(string route) =>
        route.Replace("{id}", $"I-{Random.Shared.Next(1, 1001):0000}").Replace("{sub}", "x1");

    // ------------------------------------------------------------------ what each phase accepts

    /// <summary>All services healthy: every answer is a full 200. A 503 "overloaded" is the gateway protecting itself.</summary>
    private static string? Normal(BenchEndpoint e, int status, string? code, bool partial) =>
        code == "overloaded" ? null
        : status != 200 ? "expected 200 with all services healthy"
        : partial ? "partial answer with all services healthy"
        : null;

    /// <summary>
    /// One service is broken. Endpoints that need it may fail with a downstream error (502/503/504), never a 500. Endpoints
    /// that use it for an optional step must still answer 200 (partial). Every other endpoint must not notice.
    /// </summary>
    private static Judge Broken(string service) => (e, status, code, partial) =>
    {
        if (code == "overloaded") return null;
        if (e.RequiredServices.Contains(service))
            return status == 200 || (status is 502 or 503 or 504 && code is not (null or "internal_error")) ? null : $"expected 200 or a downstream error while {service} is broken";
        if (e.Services.Contains(service))
            return status == 200 ? null : $"{service} is optional here: expected 200 (partial)";
        return status == 200 && !partial ? null : $"doesn't use {service}, so it must not be affected";
    };

    /// <summary>Just after a fault: anything but a gateway error (500) or no answer.</summary>
    private static string? Settling(BenchEndpoint e, int status, string? code, bool partial) =>
        status is 500 or 0 ? "a gateway error or no answer while a service recovers" : null;

    // ------------------------------------------------------------------ faults

    private async Task ChaosAsync()
    {
        // The service most endpoints need: the worst one to lose.
        ChaosService = endpoints.SelectMany(e => e.RequiredServices).GroupBy(s => s).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).First().Key;
        var mockId = "s" + ChaosService[^2..];
        Fault[] faults =
        [
            new("slow", "answers in 1.5–2 s (its timeout is 2 s)", new { delayMs = 1500, jitterMs = 500 }),
            new("errors", "every call answers 500", new { failRate = 1.0, status = 500 }),
            new("flaky", "half the calls answer 503", new { failRate = 0.5, status = 503 }),
            new("hangs", "never answers", new { hang = true }),
            new("drops", "closes the connection without answering", new { drop = true }),
            new("huge", "answers 6 MB (the limit is 5 MB)", new { bodyKb = 6000 })
        ];
        // Half the workers call endpoints that use the broken service, half call the others. In one shared pool, workers
        // waiting on the broken service would crowd out everything else and hide whether the others are affected.
        var affected = endpoints.Where(e => e.Services.Contains(ChaosService)).ToList();
        var others = endpoints.Where(e => !e.Services.Contains(ChaosService)).ToList();
        foreach (var fault in faults)
        {
            (await _http.PostAsJsonAsync($"{stack.MocksUrl}/_chaos/{mockId}", fault.Settings)).EnsureSuccessStatusCode();
            var name = $"fault: {ChaosService} {fault.Name}";
            var half = Math.Max(1, profile.Workers / 2);
            var results = await Task.WhenAll(
                PhaseAsync($"{name} (endpoints that use it)", profile.ChaosSeconds, half, Broken(ChaosService), pick: () => affected[Random.Shared.Next(affected.Count)]),
                PhaseAsync($"{name} (all other endpoints)", profile.ChaosSeconds, half, Broken(ChaosService), pick: () => others[Random.Shared.Next(others.Count)]));
            Chaos.Add((fault, ChaosService, results[0], results[1]));
            (await _http.DeleteAsync($"{stack.MocksUrl}/_chaos")).EnsureSuccessStatusCode();
            await PhaseAsync($"recovering from {fault.Name}", profile.ChaosRecoverySeconds, profile.Workers, Settling, keep: false);
        }
        await PhaseAsync("after faults", 10, profile.Workers, Normal);
    }

    // ------------------------------------------------------------------ config churn (publishing while under load)

    private async Task ChurnAsync(TimeSpan every, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(every, ct); } catch (OperationCanceledException) { break; }
            var id = endpoints[Random.Shared.Next(endpoints.Count)].Id;
            var file = Path.Combine(configDir, "recipes", id + ".json");
            var recipe = System.Text.Json.JsonSerializer.Deserialize<Recipe>(await File.ReadAllTextAsync(file, CancellationToken.None), AggregatorJson.Options)!;
            // Written next to it and moved into place, as the admin app does, so the gateway never reads half a file.
            var temp = file + ".tmp";
            ConfigGenerator.Write(temp, ConfigGenerator.Copy(recipe, recipe.Version + 1, RecipeStatus.Published));
            File.Move(temp, file, overwrite: true);
            ConfigChanges++;
        }
    }

    // ------------------------------------------------------------------ abuse

    private async Task AbuseAsync(CancellationToken ct)
    {
        var someone = endpoints.First(e => !e.Anonymous && e.Route.Contains("{id}"));
        var route = someone.Route.Replace("{sub}", "x1");
        string Url(string id) => $"{stack.GatewayUrl}/bff/{route.Replace("{id}", id)}";
        HttpRequestMessage Get(string url, string? token)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (token is not null) request.Headers.Authorization = new("Bearer", token);
            return request;
        }
        // Its own caller, so the abuse doesn't use up a normal caller's rate limit (and the reverse).
        var good = await TokenAsync("bench-abuser", ConfigGenerator.Scope);
        (string Case, string Expected, Func<HttpRequestMessage> Build, Func<int, bool> Ok)[] cases =
        [
            ("no such endpoint", "404", () => Get($"{stack.GatewayUrl}/bff/nowhere/at/all", good), s => s == 404),
            ("no token", "401", () => Get(Url("I-0001"), null), s => s == 401),
            ("forged token", "401", () => Get(Url("I-0001"), "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJ4In0.bad"), s => s == 401),
            ("token without the scope", "403", () => Get(Url("I-0001"), _wrongScopeToken), s => s == 403),
            ("value that breaks the pattern", "400", () => Get(Url("a%20b%24"), good), s => s == 400),
            ("CR/LF in a value", "400", () => Get(Url("x%0D%0AX-Evil:%201"), good), s => s == 400),
            ("dot-dot in a value", "400 or 404", () => Get(Url("%2E%2E"), good), s => s is 400 or 404),
            ("40 KB header", "431", () => { var r = Get(Url("I-0001"), good); r.Headers.TryAddWithoutValidation("X-Big", new string('a', 40_000)); return r; }, s => s == 431),
            ("very long address", "4xx", () => Get(Url("I-0001") + "?q=" + new string('a', 20_000), good), s => s is >= 400 and < 500),
            ("wrong method", "404 or 405", () => new HttpRequestMessage(HttpMethod.Post, Url("I-0001")) { Content = JsonContent.Create(new { a = 1 }) }, s => s is 404 or 405),
            ("OpenAPI with a made-up Host", "200", () => { var r = Get($"{stack.GatewayUrl}/bff/openapi.json", null); r.Headers.Host = $"h{Random.Shared.Next(1_000_000)}.example"; return r; }, s => s == 200)
        ];
        var stats = cases.Select(c => new PhaseStats(c.Case)).ToArray();
        var loops = Enumerable.Range(0, 8).Select(async worker =>
        {
            for (var n = worker; !ct.IsCancellationRequested; n++)
            {
                var c = cases[n % cases.Length];
                using var request = c.Build();
                var (status, code, partial, hit, micros) = await SendAsync(request);
                var s = stats[n % cases.Length];
                s.Latency.Record(micros);
                s.Count(status, code, partial, hit);
                // 429 is also a correct refusal: these come fast, and callers without a token share their address's allowance.
                if (!c.Ok(status) && status != 429) s.Flag($"{status} {code}: expected {c.Expected}");
            }
        });
        await Task.WhenAll(loops.Append(FloodAsync(someone, ct)));
        for (var i = 0; i < cases.Length; i++)
            if (!stats[i].Statuses.Keys.Any(cases[i].Ok)) stats[i].Flag($"never answered {cases[i].Expected} (only {string.Join(", ", stats[i].Statuses.Keys)})");
        for (var i = 0; i < cases.Length; i++) Abuse.Add((cases[i].Case, cases[i].Expected, stats[i]));
    }

    /// <summary>One caller sending far more than its share: some answers must be 429, none a 5xx.</summary>
    private async Task FloodAsync(BenchEndpoint endpoint, CancellationToken ct)
    {
        var token = await TokenAsync("bench-flooder", ConfigGenerator.Scope);
        var stats = new PhaseStats("one caller floods");
        var sent = 0;
        await Task.WhenAll(Enumerable.Range(0, 20).Select(async _ =>
        {
            while (!ct.IsCancellationRequested && Interlocked.Increment(ref sent) <= 1000)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"{stack.GatewayUrl}/bff/{Fill(endpoint.Route)}");
                request.Headers.Authorization = new("Bearer", token);
                var (status, code, partial, hit, micros) = await SendAsync(request);
                stats.Latency.Record(micros);
                stats.Count(status, code, partial, hit);
                if (status is not (200 or 429)) stats.Flag($"{status} {code}: expected 200 or 429");
            }
        }));
        if (!stats.Statuses.ContainsKey(429)) stats.Flag("no 429 after 1000 requests from one caller");
        Abuse.Add(("one caller floods (1000 requests)", "200 then 429", stats));
    }

    // ------------------------------------------------------------------ readings

    private async Task SampleLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (await ReadGatewayAsync(forceGc: false) is { } sample) lock (Samples) Samples.Add(sample);
            try { await Task.Delay(2000, ct); } catch (OperationCanceledException) { break; }
        }
    }

    private async Task CheckpointAsync(string label)
    {
        var saved = _phase;
        _phase = label;
        if (await ReadGatewayAsync(forceGc: true) is { } sample) Checkpoints.Add(sample);
        if (GcDumpTool is not null && HeapReportDir is not null && label is "after warm-up" or "after churn" or "after idle")
            await SaveHeapReportAsync(label);
        _phase = saved;
        if (!stack.GatewayAlive) throw new GatewayDownException($"The gateway stopped before '{label}' (exit code {stack.GatewayExitCode}).");
    }

    private async Task<Sample?> ReadGatewayAsync(bool forceGc)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{stack.GatewayUrl}/_admin/runtime{(forceGc ? "?gc=true" : "")}");
            request.Headers.Authorization = new("Bearer", _authorToken);
            using var response = await _http.SendAsync(request);
            if (!response.IsSuccessStatusCode) return null;
            var n = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
            double Mb(string key) => n[key]!.GetValue<long>() / 1048576.0;
            var now = _clock.Elapsed.TotalSeconds;
            var cpu = CpuPercent(now);
            return new Sample(now, _phase, Mb(forceGc ? "heapAfterLastGcBytes" : "managedHeapBytes"), Mb("workingSetBytes"), Mb("committedBytes"),
                n["threads"]!.GetValue<int>(), n["threadPoolThreads"]!.GetValue<int>(), n["threadPoolQueue"]!.GetValue<long>(),
                n["requestsInFlight"]!.GetValue<int>(), n["gen2Collections"]!.GetValue<int>(), n["exceptionsThrown"]!.GetValue<long>(),
                Mb("totalAllocatedBytes"), cpu, forceGc);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return null; }
    }

    private double CpuPercent(double now)
    {
        try
        {
            using var process = Process.GetProcessById(stack.GatewayPid);
            var cpu = process.TotalProcessorTime;
            var percent = _lastCpuAt > 0 ? (cpu - _lastCpu).TotalSeconds / (now - _lastCpuAt) / Environment.ProcessorCount * 100 : 0;
            (_lastCpu, _lastCpuAt) = (cpu, now);
            return percent;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return 0; }
    }

    private async Task SaveHeapReportAsync(string label)
    {
        Directory.CreateDirectory(HeapReportDir!);
        var name = label.Replace(' ', '-');
        var dump = Path.Combine(HeapReportDir!, name + ".gcdump");
        await RunToolAsync(["collect", "-p", stack.GatewayPid.ToString(System.Globalization.CultureInfo.InvariantCulture), "-o", dump]);
        var report = await RunToolAsync(["report", dump]);
        await File.WriteAllTextAsync(Path.Combine(HeapReportDir!, name + ".txt"), report);
        progress.WriteLine($"    heap report: {Path.Combine(HeapReportDir!, name + ".txt")}");
    }

    private async Task<string> RunToolAsync(string[] arguments)
    {
        using var tool = Process.Start(new ProcessStartInfo(GcDumpTool!, arguments) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!;
        var output = await tool.StandardOutput.ReadToEndAsync();
        await tool.WaitForExitAsync();
        return output;
    }

    /// <summary>How long the admin app takes to list every endpoint, and the gateway to serve its OpenAPI document.</summary>
    private async Task ProbeAsync(CancellationToken ct)
    {
        foreach (var (what, url, token) in new[]
        {
            ("Admin: list all endpoints", $"{stack.AdminUrl}/admin/recipes", _authorToken),
            ("Gateway: OpenAPI document", $"{stack.GatewayUrl}/bff/openapi.json", (string?)null)
        })
        {
            for (var i = 0; i < 3; i++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (token is not null) request.Headers.Authorization = new("Bearer", token);
                var (status, _, _, _, micros) = await SendAsync(request);
                Probes.Add(($"{what} ({_phase})", micros / 1000, status));
            }
        }
    }

    // ------------------------------------------------------------------ tokens

    private async Task GetTokensAsync()
    {
        progress.WriteLine($"  getting {profile.Callers} caller tokens");
        _authorToken = await TokenAsync("bench-author", $"aggregator.author {ConfigGenerator.Scope}");
        _wrongScopeToken = await TokenAsync("bench-other", "something.else");
        _tokens = new string[profile.Callers];
        await Parallel.ForEachAsync(Enumerable.Range(0, profile.Callers), new ParallelOptions { MaxDegreeOfParallelism = 32 },
            async (i, _) => _tokens[i] = await TokenAsync($"bench-user-{i:00000}", ConfigGenerator.Scope));
    }

    private async Task<string> TokenAsync(string sub, string scope)
    {
        var answer = await _http.GetFromJsonAsync<JsonObject>($"{stack.GatewayUrl}/dev/token?sub={sub}&scope={Uri.EscapeDataString(scope)}");
        return answer!["access_token"]!.GetValue<string>();
    }
}

public sealed class GatewayDownException(string message) : Exception(message);
