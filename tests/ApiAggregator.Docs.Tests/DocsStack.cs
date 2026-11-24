using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace ApiAggregator.Docs.Tests;

/// <summary>
/// What the guides' examples run against: the built mock services, gateway and admin app (Development settings), each its own
/// process on a free port, over a temporary copy of samples/config. So a running dev-run.sh doesn't get in the way,
/// and nothing in the repository changes. Also runs the built CLI.
/// </summary>
public sealed class DocsStack : IAsyncLifetime
{
    private readonly List<(string Name, Process Process, StringWriter Log)> _apps = [];

    public static string RepoRoot { get; } = FindRepoRoot();
    public string ConfigDir { get; } = Path.Combine(Path.GetTempPath(), "aggregator-docs-" + Guid.NewGuid().ToString("n"));
    public string GatewayUrl { get; private set; } = "";
    public string AdminUrl { get; private set; } = "";
    private int _mockPort;

    /// <summary>The guides show the usual mock address. Output from here uses a free port, so it's mapped back.</summary>
    public string AsDocumented(string output) => output.Replace($"localhost:{_mockPort}", "localhost:5100");

    /// <summary>The other way round, for what a guide sends (e.g. a spec address on the mocks).</summary>
    public string AsRunning(string input) => input.Replace("localhost:5100", $"localhost:{_mockPort}");

    /// <summary>The mock secrets the gateway uses in Development, for the CLI (it reads them from the environment).</summary>
    private Dictionary<string, string> _secrets = [];

    public async Task InitializeAsync()
    {
        _mockPort = FreePort();
        var gatewayPort = FreePort();
        GatewayUrl = $"http://localhost:{gatewayPort}";
        var adminPort = FreePort();
        AdminUrl = $"http://localhost:{adminPort}";
        CopySampleConfig();
        _secrets = ReadDevelopmentSecrets();

        Start("MockServices", "samples/MockServices", _mockPort);
        Start("ApiAggregator.Gateway", "src/ApiAggregator.Gateway", gatewayPort);
        Start("ApiAggregator.Admin", "src/ApiAggregator.Admin", adminPort);
        await WaitUntilUp($"http://localhost:{_mockPort}/_stats", $"{GatewayUrl}/health/live", $"{AdminUrl}/health/live");
    }

    public Task DisposeAsync()
    {
        foreach (var (_, process, _) in _apps)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* already gone */ }
            process.Dispose();
        }
        try { Directory.Delete(ConfigDir, recursive: true); } catch (IOException) { /* best effort */ }
        return Task.CompletedTask;
    }

    /// <summary>Runs the built CLI with the sample config copy and the mock secrets. Returns its exit code and stdout.</summary>
    public async Task<(int ExitCode, string Output)> RunCliAsync(IEnumerable<string> args)
    {
        var dll = Path.Combine(RepoRoot, "tools", "ApiAggregator.Cli", "bin", BuildConfiguration(), "net10.0", "aggregator.dll");
        if (!File.Exists(dll)) throw new InvalidOperationException($"{dll} isn't built. Run dotnet build first.");

        var info = new ProcessStartInfo("dotnet", [dll, .. args, "--config", ConfigDir])
        {
            WorkingDirectory = RepoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var (name, value) in _secrets) info.Environment["Secrets__" + name] = value;

        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, (await output) + (await errors));
    }

    /// <summary>Calls the gateway as an app would: with a dev token for <paramref name="scope"/>, or no token at all.</summary>
    public async Task<(int Status, string Body, IReadOnlyList<string> CorsHeaders)> CallGatewayAsync(string method, string path, string? scope, IEnumerable<(string Name, string Value)> headers)
    {
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), GatewayUrl + path);
        if (scope is not null)
        {
            var token = (await http.GetFromJsonAsync<JsonObject>($"{GatewayUrl}/dev/token?scope={Uri.EscapeDataString(scope)}"))!["access_token"]!.GetValue<string>();
            request.Headers.Authorization = new("Bearer", token);
        }
        foreach (var (name, value) in headers) request.Headers.TryAddWithoutValidation(name, value);
        using var response = await http.SendAsync(request);
        // The browser-access (CORS) headers, one "Name: value" line each in name order, for guides that show them.
        var cors = response.Headers
            .Where(h => h.Key.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase) || h.Key.Equals("Vary", StringComparison.OrdinalIgnoreCase))
            .OrderBy(h => h.Key, StringComparer.OrdinalIgnoreCase)
            .Select(h => $"{h.Key}: {string.Join(", ", h.Value)}")
            .ToList();
        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync(), cors);
    }

    /// <summary>
    /// An MCP client on the admin app, as an AI agent connects (ADR 0021): a development token with the author role and
    /// the aggregator.mcp scope, and the client name "Docs Agent".
    /// </summary>
    public async Task<McpClient> AgentAsync()
    {
        var http = new HttpClient { BaseAddress = new Uri(AdminUrl) };
        var scope = Uri.EscapeDataString("aggregator.author aggregator.mcp");
        var token = (await http.GetFromJsonAsync<JsonObject>($"/dev/token?sub=docs-agent&scope={scope}"))!["access_token"]!.GetValue<string>();
        http.DefaultRequestHeaders.Authorization = new("Bearer", token);
        var transport = new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri($"{AdminUrl}/mcp") }, http, ownsHttpClient: true);
        return await McpClient.CreateAsync(transport, new McpClientOptions { ClientInfo = new Implementation { Name = "Docs Agent", Version = "1.0" } });
    }

    /// <summary>Calls one MCP tool. Returns 200 and its text, or 400 and the refusal.</summary>
    public async Task<(int Status, string Text)> CallToolAsync(string tool, IReadOnlyDictionary<string, object?> arguments)
    {
        await using var agent = await AgentAsync();
        var result = await agent.CallToolAsync(tool, arguments);
        var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(t => t.Text));
        return (result.IsError == true ? 400 : 200, text);
    }

    private readonly HashSet<string> _done = [];

    /// <summary>True the first time it's asked about this key on this stack (e.g. a guide example that sets something up).</summary>
    public bool FirstTime(string key) { lock (_done) return _done.Add(key); }

    public string Logs() => string.Join("\n", _apps.Select(a => $"--- {a.Name}\n{a.Log}"));

    // ------------------------------------------------------------------ helpers

    /// <summary>samples/config, with the mocks' address changed to the port used here.</summary>
    private void CopySampleConfig()
    {
        var source = Path.Combine(RepoRoot, "samples", "config");
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            if (relative == "secrets.local.json") continue; // never copy someone's local secrets
            var target = Path.Combine(ConfigDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (file.EndsWith(".json")) File.WriteAllText(target, File.ReadAllText(file).Replace("localhost:5100", $"localhost:{_mockPort}"));
            else File.Copy(file, target);
        }
    }

    private static Dictionary<string, string> ReadDevelopmentSecrets()
    {
        var settings = JsonNode.Parse(File.ReadAllText(Path.Combine(RepoRoot, "src", "ApiAggregator.Gateway", "appsettings.Development.json")),
            documentOptions: new() { CommentHandling = System.Text.Json.JsonCommentHandling.Skip });
        return (settings?["Secrets"] as JsonObject ?? []).ToDictionary(p => p.Key, p => p.Value!.GetValue<string>());
    }

    private void Start(string name, string projectDir, int port)
    {
        var dll = Path.Combine(RepoRoot, projectDir, "bin", BuildConfiguration(), "net10.0", name + ".dll");
        if (!File.Exists(dll)) throw new InvalidOperationException($"{dll} isn't built. Run dotnet build first.");

        var info = new ProcessStartInfo("dotnet", [dll])
        {
            WorkingDirectory = Path.Combine(RepoRoot, projectDir), // like dotnet run: appsettings are found
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        info.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        info.Environment["ASPNETCORE_URLS"] = $"http://localhost:{port}";
        info.Environment["Aggregator__Config__Directory"] = ConfigDir;
        info.Environment["Logging__LogLevel__Default"] = "Warning";

        var process = Process.Start(info)!;
        var log = new StringWriter();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (log) log.WriteLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (log) log.WriteLine(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _apps.Add((name, process, log));
    }

    private async Task WaitUntilUp(params string[] urls)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTime.UtcNow.AddSeconds(60);
        foreach (var url in urls)
        {
            while (true)
            {
                try { if ((await http.GetAsync(url)).IsSuccessStatusCode) break; }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { /* not up yet */ }
                if (DateTime.UtcNow > deadline) throw new InvalidOperationException($"{url} didn't start within 60 s.\n{Logs()}");
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

    /// <summary>Debug or Release: the same as this test assembly (bin/&lt;configuration&gt;/net10.0).</summary>
    private static string BuildConfiguration() => new DirectoryInfo(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory)).Parent!.Name;

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "ApiAggregator.sln"))) return dir.FullName;
        throw new InvalidOperationException("Couldn't find the repository root (ApiAggregator.sln).");
    }
}
