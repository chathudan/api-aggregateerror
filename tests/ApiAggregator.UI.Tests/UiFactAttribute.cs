using System.Text.Json.Nodes;

namespace ApiAggregator.UI.Tests;

/// <summary>
/// A UI test. Skipped (with how to fix it) when Playwright's Chromium isn't installed, so a plain `dotnet test`
/// still passes on machines and CI agents without browsers.
/// </summary>
public sealed class UiFactAttribute : FactAttribute
{
    public UiFactAttribute()
    {
        if (!BrowsersInstalled) Skip = "Playwright's Chromium isn't installed. Run scripts/install-playwright.sh once, then run the UI tests again.";
    }

    public static bool BrowsersInstalled { get; } = FindBrowser();

    /// <summary>
    /// The Chromium revision this Playwright version needs (from its browsers.json, next to the test assembly),
    /// looked up in Playwright's browser folder (PLAYWRIGHT_BROWSERS_PATH, or the default for this OS).
    /// </summary>
    private static bool FindBrowser()
    {
        var manifest = Path.Combine(AppContext.BaseDirectory, ".playwright", "package", "browsers.json");
        if (!File.Exists(manifest)) return false;

        var browsers = JsonNode.Parse(File.ReadAllText(manifest))?["browsers"]?.AsArray() ?? [];
        var folder = Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH") is { Length: > 0 } custom ? custom : DefaultFolder();
        // Headless runs use the headless shell when this version has one, otherwise full Chromium.
        foreach (var name in new[] { "chromium-headless-shell", "chromium" })
        {
            var revision = browsers.FirstOrDefault(b => b?["name"]?.GetValue<string>() == name)?["revision"]?.GetValue<string>();
            if (revision is null) continue;
            return Directory.Exists(Path.Combine(folder, $"{name.Replace('-', '_')}-{revision}"));
        }
        return false;
    }

    private static string DefaultFolder()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows()) return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ms-playwright");
        if (OperatingSystem.IsMacOS()) return Path.Combine(home, "Library", "Caches", "ms-playwright");
        return Path.Combine(home, ".cache", "ms-playwright");
    }
}
