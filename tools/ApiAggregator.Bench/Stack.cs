using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace ApiAggregator.Bench;

/// <summary>
/// Starts the mocks, the gateway and the admin app from their built files (the same build configuration as this tool)
/// on free ports, with the generated config. Adapted from the UI tests' StackFixture. Stops them on dispose.
/// </summary>
public sealed class Stack : IAsyncDisposable
{
    private readonly List<(string Name, Process Process, StreamWriter Log)> _apps = [];
    private long _infoLines;

    /// <summary>Information log lines the apps wrote (not kept on disk).</summary>
    public long InfoLines => Interlocked.Read(ref _infoLines);

    public required string RepoRoot { get; init; }
    public required string LogDir { get; init; }
    public int MockPort { get; } = FreePort();
    public int GatewayPort { get; } = FreePort();
    public int AdminPort { get; } = FreePort();
    public string MocksUrl => $"http://localhost:{MockPort}";
    public string GatewayUrl => $"http://localhost:{GatewayPort}";
    public string AdminUrl => $"http://localhost:{AdminPort}";

    /// <summary>The gateway's process id, for memory and thread samples.</summary>
    public int GatewayPid => _apps.First(a => a.Name == "ApiAggregator.Gateway").Process.Id;

    public async Task StartAsync(string configDir, IReadOnlyDictionary<string, string> gatewaySettings)
    {
        Directory.CreateDirectory(LogDir);
        var common = new Dictionary<string, string>
        {
            ["Aggregator__Config__Directory"] = configDir,
            ["Secrets__bench-api-key"] = "key-bench",
            ["Secrets__bench-password"] = "bench-pass"
        };

        Start("MockServices", "samples/MockServices", MockPort, new() { ["Logging__LogLevel__Default"] = "Warning" });
        var gateway = new Dictionary<string, string>(common)
        {
            ["Gateway__Diagnostics__Enabled"] = "true",
            ["Gateway__Diagnostics__AllowForcedGc"] = "true"
        };
        foreach (var (key, value) in gatewaySettings) gateway[key] = value;
        Start("ApiAggregator.Gateway", "src/ApiAggregator.Gateway", GatewayPort, gateway);
        Start("ApiAggregator.Admin", "src/ApiAggregator.Admin", AdminPort, new(common) { ["Gateway__BaseUrl"] = GatewayUrl + "/" });

        await WaitUntilUpAsync($"{MocksUrl}/_stats", $"{GatewayUrl}/health/ready", $"{AdminUrl}/health/live");
    }

    /// <summary>True while the gateway process is running (the benchmark's first check: it must never crash).</summary>
    public bool GatewayAlive => _apps.FirstOrDefault(a => a.Name == "ApiAggregator.Gateway").Process is { HasExited: false };

    public int? GatewayExitCode => _apps.FirstOrDefault(a => a.Name == "ApiAggregator.Gateway").Process is { HasExited: true } p ? p.ExitCode : null;

    public async ValueTask DisposeAsync()
    {
        foreach (var (_, process, log) in _apps)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            catch (InvalidOperationException) { /* already gone */ }
            lock (log) log.Dispose();
            process.Dispose();
        }
        _apps.Clear();
    }

    private void Start(string name, string projectDir, int port, Dictionary<string, string> environment)
    {
        var dll = Path.Combine(RepoRoot, projectDir, "bin", BuildConfiguration(), "net10.0", name + ".dll");
        if (!File.Exists(dll)) throw new InvalidOperationException($"{dll} isn't built. Run scripts/bench.sh (it builds everything first).");

        var info = new ProcessStartInfo("dotnet", [dll])
        {
            WorkingDirectory = Path.Combine(RepoRoot, projectDir), // like dotnet run: appsettings are found
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        info.Environment["ASPNETCORE_ENVIRONMENT"] = "Development"; // for /dev/token; everything else as configured
        info.Environment["ASPNETCORE_URLS"] = $"http://localhost:{port}";
        foreach (var (key, value) in environment) info.Environment[key] = value;

        var process = Process.Start(info)!;
        var log = new StreamWriter(Path.Combine(LogDir, name + ".log")) { AutoFlush = true };
        // The apps log every request at Information, as in production (that cost is part of what is measured). Under
        // load that is gigabytes, so only warnings and errors are kept on disk; information lines are just counted.
        void Line(string? text)
        {
            if (text is null) return;
            if (text.StartsWith("info:", StringComparison.Ordinal) || text.StartsWith("dbug:", StringComparison.Ordinal)) { Interlocked.Increment(ref _infoLines); return; }
            lock (log) log.WriteLine(text);
        }
        process.OutputDataReceived += (_, e) => Line(e.Data);
        process.ErrorDataReceived += (_, e) => Line(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _apps.Add((name, process, log));
    }

    private async Task WaitUntilUpAsync(params string[] urls)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var deadline = DateTime.UtcNow.AddSeconds(90);
        foreach (var url in urls)
        {
            while (true)
            {
                try { if ((await http.GetAsync(url)).IsSuccessStatusCode) break; }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { /* not up yet */ }
                if (DateTime.UtcNow > deadline) throw new InvalidOperationException($"{url} didn't start within 90 s. See the logs in {LogDir}.");
                await Task.Delay(300);
            }
        }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>Debug or Release: the same as this tool (bin/&lt;configuration&gt;/net10.0).</summary>
    public static string BuildConfiguration() => new DirectoryInfo(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory)).Parent!.Name;

    public static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "ApiAggregator.sln"))) return dir.FullName;
        throw new InvalidOperationException("Run the benchmark from inside the repository.");
    }
}
