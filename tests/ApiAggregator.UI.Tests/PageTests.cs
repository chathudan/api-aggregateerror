using System.Text.Json;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace ApiAggregator.UI.Tests;

/// <summary>Every page: accessibility basics, and the screenshots the guides use (docs/images).</summary>
[Collection(UiCollection.Name)]
public class PageTests(StackFixture stack)
{
    private static readonly string[] Pages = ["/", "/endpoints", "/endpoints/new", "/endpoints/new?guide=first", "/endpoints/customer-overview", "/services", "/logs", "/settings/browser-access", "/help", "/help/build", "/help/add-service", "/help/troubleshooting", "/signin"];

    private static readonly HashSet<string> Interactive = ["textbox", "searchbox", "combobox", "checkbox", "radio", "button", "link", "spinbutton", "menuitem"];

    [UiFact]
    public async Task Every_page_names_its_controls_and_has_one_main_heading_and_one_primary_button_at_most()
    {
        var problems = new List<string>();
        foreach (var path in Pages)
        {
            var page = await stack.OpenAsync(path);

            // Names as a screen reader announces them (Chrome's accessibility tree).
            var cdp = await page.Context.NewCDPSessionAsync(page);
            var tree = (await cdp.SendAsync("Accessibility.getFullAXTree"))!.Value;
            foreach (var node in tree.GetProperty("nodes").EnumerateArray())
            {
                var role = node.TryGetProperty("role", out var r) ? r.GetProperty("value").GetString() : null;
                var ignored = node.TryGetProperty("ignored", out var i) && i.GetBoolean();
                var name = node.TryGetProperty("name", out var n) && n.TryGetProperty("value", out var v) ? v.ToString() : "";
                if (role is not null && Interactive.Contains(role) && !ignored && string.IsNullOrWhiteSpace(name))
                    problems.Add($"{path}: a {role} has no name");
            }

            var levels = await page.EvalOnSelectorAllAsync<int[]>("h1,h2,h3,h4", "els => els.map(e => +e.tagName[1])");
            if (levels.Count(l => l == 1) != 1) problems.Add($"{path}: {levels.Count(l => l == 1)} h1 headings (expected 1)");
            for (var k = 1; k < levels.Length; k++)
                if (levels[k] > levels[k - 1] + 1) problems.Add($"{path}: heading jumps from h{levels[k - 1]} to h{levels[k]}");

            var primaries = await page.Locator(".button.primary:visible").CountAsync();
            if (primaries > 1) problems.Add($"{path}: {primaries} primary buttons (expected at most 1)");

            var overflow = await page.EvaluateAsync<int>("document.documentElement.scrollWidth - window.innerWidth");
            if (overflow > 0) problems.Add($"{path}: scrolls sideways by {overflow}px at 1280px wide");

            await page.Context.CloseAsync();
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [UiFact]
    public async Task The_first_tab_reaches_the_skip_link_and_focus_is_visible()
    {
        var page = await stack.OpenAsync("/");
        await page.Keyboard.PressAsync("Tab");

        Assert.Equal("Skip to content", await page.EvaluateAsync<string>("document.activeElement.textContent.trim()"));
        Assert.Equal("solid", await page.EvaluateAsync<string>("getComputedStyle(document.activeElement).outlineStyle"));
    }

    [UiFact]
    public async Task Screenshots_of_every_page()
    {
        // Home after checking every sign-in, so the pills show real states.
        var home = await stack.OpenAsync("/");
        await home.GetByRole(AriaRole.Button, new() { Name = "Check all sign-ins" }).ClickAsync();
        await Expect(home.Locator(".toast").First).ToBeVisibleAsync();
        await home.WaitForTimeoutAsync(500);
        await Save(home, "home", hideToasts: true);

        await Save(await stack.OpenAsync("/endpoints"), "endpoints");
        await Save(await stack.OpenAsync("/endpoints/new"), "endpoint-new", fullPage: false);

        var editor = await stack.OpenAsync("/endpoints/customer-overview");
        await Save(editor, "endpoint-editor", fullPage: false);
        await SaveElement(editor, "#step-3", "endpoint-steps");

        // The pickers: "Run this step" (once for each order's contact) and "Only run when" (premium customers only).
        var contacts = await stack.OpenAsync("/endpoints/contacts-unique");
        await SaveElement(contacts, ".step >> nth=2", "endpoint-loop-picker");
        await SaveElement(editor, ".step >> nth=3 >> fieldset.condition", "endpoint-condition-picker");

        // Try it, then the results and the history.
        var profile = await stack.OpenAsync("/endpoints/customer-profile");
        await profile.GetByLabel("Test value for Try it").First.FillAsync("C-10042");
        await profile.Locator(".sticky-footer").GetByRole(AriaRole.Button, new() { Name = "Try it" }).ClickAsync();
        await Expect(profile.Locator(".sticky-footer")).ToContainTextAsync("Test passed");
        await SaveElement(profile, "#step-5", "endpoint-test-result");
        await profile.Locator(".sticky-footer").GetByRole(AriaRole.Button, new() { Name = "Publish", Exact = true }).ClickAsync();
        await Expect(profile.Locator(".toast").First).ToContainTextAsync("Published");
        await profile.WaitForTimeoutAsync(500);
        await SaveElement(profile, "#history", "endpoint-history");

        var services = await stack.OpenAsync("/services");
        await Save(services, "services", fullPage: false);
        await services.GetByRole(AriaRole.Button, new() { Name = "Add service", Exact = true }).ClickAsync();
        await services.GetByLabel("Spec address").FillAsync($"http://localhost:{stack.MockPort}/d/openapi.json");
        await services.GetByRole(AriaRole.Button, new() { Name = "Load", Exact = true }).ClickAsync();
        await Expect(services.GetByText("Operations to import")).ToBeVisibleAsync();
        await SaveElement(services, "section.card.accent", "services-add-from-spec");

        var logs = await stack.OpenAsync("/logs");
        await Expect(logs.Locator(".log-row").First).ToBeVisibleAsync(new() { Timeout = 20_000 });
        await Save(logs, "logs", fullPage: false);

        var access = await stack.OpenAsync("/settings/browser-access");
        await access.GetByLabel("Origin to test").FillAsync("http://localhost:5173");
        await access.GetByRole(AriaRole.Button, new() { Name = "Test", Exact = true }).ClickAsync();
        await Expect(access.GetByRole(AriaRole.Status).Filter(new() { HasText = "Allowed" })).ToBeVisibleAsync();
        await Save(access, "browser-access", fullPage: false);

        await Save(await stack.OpenAsync("/help"), "help", fullPage: false);

        // The guided walkthrough, on the finished sample: every step ticked but Try it. And "How many" / "Without repeats".
        var guided = await stack.OpenAsync("/endpoints/contacts-unique?guide=first");
        await SaveElement(guided, ".card.guide", "walkthrough");
        await SaveElement(guided, "#step-4 table.response-fields", "response-shapes");

        var signIn = await stack.NewAnonymousPageAsync();
        await StackFixture.GotoAsync(signIn, "/signin");
        await Save(signIn, "signin", fullPage: false);
    }

    [UiFact]
    public async Task The_top_bar_shows_who_is_signed_in_and_signing_out_ends_it()
    {
        var page = await stack.OpenAsync("/endpoints");
        await Expect(page.Locator(".topbar .who")).ToContainTextAsync($"{StackFixture.TesterName} · Publisher");

        await page.GetByRole(AriaRole.Button, new() { Name = "Sign out" }).ClickAsync();
        await page.WaitForURLAsync(url => url.Contains("/signin"));
        await Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("You've signed out");

        // The cookie is gone: pages send you back to sign in.
        await StackFixture.GotoAsync(page, "/services");
        Assert.Contains("/signin", page.Url);
    }

    // ------------------------------------------------------------------ saving

    /// <summary>Always into the test output; into docs/images too when UPDATE_SCREENSHOTS=1 (scripts/screenshots.sh).</summary>
    private IEnumerable<string> Targets(string name)
    {
        yield return Path.Combine(AppContext.BaseDirectory, "screenshots", name + ".png");
        if (Environment.GetEnvironmentVariable("UPDATE_SCREENSHOTS") == "1")
            yield return Path.Combine(stack.RepoRoot, "docs", "images", name + ".png");
    }

    private async Task Save(IPage page, string name, bool fullPage = true, bool hideToasts = false)
    {
        var hide = hideToasts ? await page.AddStyleTagAsync(new() { Content = ".toasts { display: none !important; }" }) : null;
        foreach (var target in Targets(name))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await page.ScreenshotAsync(new() { Path = target, FullPage = fullPage });
        }
        if (hide is not null) await hide.EvaluateAsync("e => e.remove()");
    }

    /// <summary>One part of the page, without the toasts and the sticky footer over it (shown again afterwards).</summary>
    private async Task SaveElement(IPage page, string selector, string name)
    {
        var hide = await page.AddStyleTagAsync(new() { Content = ".toasts, .sticky-footer { display: none !important; }" });
        foreach (var target in Targets(name))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await page.Locator(selector).First.ScreenshotAsync(new() { Path = target });
        }
        await hide.EvaluateAsync("e => e.remove()");
    }

    [UiFact]
    public async Task Every_full_guide_link_on_the_help_page_points_at_a_guide_and_heading_that_exist()
    {
        // Every help page, as listed in the Help menu.
        var home = await stack.OpenAsync("/help");
        var pages = (await home.EvalOnSelectorAllAsync<string[]>(".toc a", "els => els.map(e => e.getAttribute('href'))")).Distinct().ToList();
        Assert.True(pages.Count >= 13, $"Expected every help page in the Help menu, found {pages.Count}.");

        var hrefs = new List<string>();
        foreach (var path in pages)
        {
            var page = await stack.OpenAsync(path);
            var links = await page.EvalOnSelectorAllAsync<string[]>("a[href*='/docs/guides/']", "els => els.map(e => e.getAttribute('href'))");
            if (path != "/help") Assert.True(links.Length >= 1, $"{path} has no Full guide link.");
            hrefs.AddRange(links);
            await page.Context.CloseAsync();
        }
        var problems = new List<string>();
        foreach (var href in hrefs.Distinct())
        {
            var (file, anchor) = (href[(href.IndexOf("/docs/guides/", StringComparison.Ordinal) + "/docs/guides/".Length)..].Split('#') is var parts ? (parts[0], parts.ElementAtOrDefault(1)) : default);
            var path = Path.Combine(stack.RepoRoot, "docs", "guides", file);
            if (!File.Exists(path)) { problems.Add($"{href}: there's no docs/guides/{file}"); continue; }
            if (anchor is not null && !File.ReadLines(path).Where(l => l.StartsWith('#')).Select(GitHubAnchor).Contains(anchor))
                problems.Add($"{href}: docs/guides/{file} has no heading for #{anchor}");
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    /// <summary>The anchor GitHub gives a markdown heading: lowercase, punctuation removed, spaces to dashes.</summary>
    private static string GitHubAnchor(string heading)
    {
        var text = heading.TrimStart('#').Trim().ToLowerInvariant().Replace("`", "").Replace("*", "");
        return new string(text.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or ' ').ToArray()).Replace(' ', '-');
    }
}
