using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Microsoft.Playwright;

namespace ApiAggregator.UI.Tests;

[CollectionDefinition(Name)]
public sealed class UiCollection : ICollectionFixture<StackFixture>
{
    public const string Name = "ui";
}

/// <summary>
/// The whole product, running for real: mocks, gateway, admin API and admin UI (the built apps, each its own process,
/// Development settings) on free ports, over a temporary copy of samples/config. Plus one headless Chromium.
/// Started once for all UI tests. Does nothing when the browsers aren't installed (the tests are skipped then).
/// </summary>
public sealed class StackFixture : IAsyncLifetime
{
    private readonly List<(string Name, Process Process)> _apps = [];
    private IPlaywright? _playwright;

    public string RepoRoot { get; } = FindRepoRoot();
    public string ConfigDir { get; } = Path.Combine(Path.GetTempPath(), "aggregator-ui-tests-" + Guid.NewGuid().ToString("n"));
    public string LogDir => Path.Combine(ConfigDir, "_logs");
    public int MockPort { get; private set; }
    public string GatewayUrl { get; private set; } = "";
    public string AdminApiUrl { get; private set; } = "";
    public string UiUrl { get; private set; } = "";
    public IBrowser Browser { get; private set; } = default!;

    public async Task InitializeAsync()
    {
        if (!UiFactAttribute.BrowsersInstalled) return;

        // Screenshots for the guides use the usual ports (5100...), so the addresses in them match what readers see.
        var standard = Environment.GetEnvironmentVariable("UI_TESTS_STANDARD_PORTS") == "1";
        var (mock, gateway, admin, ui) = standard ? (5100, 5200, 5210, 5300) : (FreePort(), FreePort(), FreePort(), FreePort());
        MockPort = mock;
        CopySampleConfig();
        GatewayUrl = $"http://localhost:{gateway}";
        AdminApiUrl = $"http://localhost:{admin}";
        UiUrl = $"http://localhost:{ui}";

        Start("MockServices", "samples/MockServices", mock, []);
        Start("ApiAggregator.Gateway", "src/ApiAggregator.Gateway", gateway, []);
        Start("ApiAggregator.Admin.Api", "src/ApiAggregator.Admin.Api", admin, []);
        Start("ApiAggregator.Admin.UI", "src/ApiAggregator.Admin.UI", ui, new()
        {
            ["AdminApi__BaseUrl"] = AdminApiUrl + "/",
            ["Gateway__BaseUrl"] = GatewayUrl + "/"
        });

        await WaitUntilUp($"http://localhost:{mock}/_stats", $"{GatewayUrl}/health/live", $"{AdminApiUrl}/dev/token", UiUrl);

        _playwright = await Playwright.CreateAsync();
        Browser = await _playwright.Chromium.LaunchAsync(new() { Headless = true });
    }

    public async Task DisposeAsync()
    {
        if (_playwright is not null) { await Browser.DisposeAsync(); _playwright.Dispose(); }
        foreach (var (_, process) in _apps)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* already gone */ }
            process.Dispose();
        }
        try { Directory.Delete(ConfigDir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    /// <summary>A new page at laptop size, in its own browser context (no shared state between tests).</summary>
    public async Task<IPage> NewPageAsync()
    {
        var context = await Browser.NewContextAsync(new() { ViewportSize = new() { Width = 1280, Height = 800 }, BaseURL = UiUrl });
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(15_000);
        return page;
    }

    /// <summary>Opens a page and waits until Blazor is connected and the page has loaded its data.</summary>
    public async Task<IPage> OpenAsync(string path)
    {
        var page = await NewPageAsync();
        await GotoAsync(page, path);
        return page;
    }

    public static async Task GotoAsync(IPage page, string path)
    {
        await page.GotoAsync(path, new() { WaitUntil = WaitUntilState.NetworkIdle });
        await page.WaitForTimeoutAsync(1200); // the interactive circuit connects after the first paint
    }

    /// <summary>Calls an aggregated endpoint through the gateway with a dev token, as an app would.</summary>
    public async Task<(HttpStatusCode Status, JsonNode? Body)> CallGatewayAsync(string path, string scope)
    {
        using var http = new HttpClient();
        var token = (await http.GetFromJsonAsync<JsonObject>($"{GatewayUrl}/dev/token?scope={Uri.EscapeDataString(scope)}"))!["access_token"]!.GetValue<string>();
        using var request = new HttpRequestMessage(HttpMethod.Get, GatewayUrl + path);
        request.Headers.Authorization = new("Bearer", token);
        using var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? null : JsonNode.Parse(text));
    }

    /// <summary>The console output of one app, for failure messages.</summary>
    public string LogOf(string app) => File.Exists(Path.Combine(LogDir, app + ".log")) ? File.ReadAllText(Path.Combine(LogDir, app + ".log")) : "";

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
            if (file.EndsWith(".json")) File.WriteAllText(target, File.ReadAllText(file).Replace("localhost:5100", $"localhost:{MockPort}"));
            else File.Copy(file, target);
        }
        Directory.CreateDirectory(LogDir);
    }

    private void Start(string name, string projectDir, int port, Dictionary<string, string> extraEnvironment)
    {
        var dll = Path.Combine(RepoRoot, projectDir, "bin", BuildConfiguration(), "net10.0", name + ".dll");
        if (!File.Exists(dll)) throw new InvalidOperationException($"{dll} isn't built. Run dotnet build first.");

        var info = new ProcessStartInfo("dotnet", [dll])
        {
            WorkingDirectory = Path.Combine(RepoRoot, projectDir), // like dotnet run: appsettings and static files are found
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        info.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        info.Environment["ASPNETCORE_URLS"] = $"http://localhost:{port}";
        info.Environment["Aggregator__Config__Directory"] = ConfigDir;
        info.Environment["Logging__LogLevel__Default"] = "Warning";
        foreach (var (key, value) in extraEnvironment) info.Environment[key] = value;

        var process = Process.Start(info)!;
        var log = new StreamWriter(Path.Combine(LogDir, name + ".log")) { AutoFlush = true };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (log) log.WriteLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (log) log.WriteLine(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _apps.Add((name, process));
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
                if (DateTime.UtcNow > deadline)
                    throw new InvalidOperationException($"{url} didn't start within 60 s.\n" + string.Join("\n", _apps.Select(a => $"--- {a.Name}\n{LogOf(a.Name)}")));
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
