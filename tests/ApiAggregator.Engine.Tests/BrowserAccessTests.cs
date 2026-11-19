using ApiAggregator.Engine.BrowserAccess;
using ApiAggregator.Engine.Config;
using ApiAggregator.Engine.Model;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ApiAggregator.Engine.Tests;

/// <summary>The origin rules, the policy built from them, and how the store keeps the admin entries (ADR 0015).</summary>
public sealed class BrowserAccessTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aggregator-cors-tests-" + Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private static readonly BrowserAccessSettings Limited = new()
    {
        AllowedOrigins = ["https://app.example.com"],
        AllowedOriginPatterns = ["https://*.example.com"]
    };

    // ------------------------------------------------------------------ one origin

    [Theory]
    [InlineData("https://app.example.com", "https://app.example.com")]
    [InlineData("  HTTPS://App.Example.COM ", "https://app.example.com")]
    [InlineData("https://app.example.com:8443", "https://app.example.com:8443")]
    [InlineData("http://localhost:5173", "http://localhost:5173")]
    [InlineData("https://[::1]:8443", "https://[::1]:8443")]
    [InlineData("https://xn--bcher-kva.example", "https://xn--bcher-kva.example")]
    public void An_exact_origin_is_accepted_in_the_form_browsers_send(string input, string canonical)
    {
        var check = OriginRules.CheckOrigin(input, isDevelopment: false);

        Assert.True(check.Ok, check.Problem);
        Assert.Equal(canonical, check.Value);
    }

    [Theory]
    [InlineData("*", "every website")]
    [InlineData("null", "sandboxed")]
    [InlineData("https://*.example.com", "Wildcards")]
    [InlineData("https://app.example.com/", "trailing slash")]
    [InlineData("https://app.example.com/path", "no path")]
    [InlineData("https://app.example.com?x=1", "no path")]
    [InlineData("https://app.example.com:443", "Leave out :443")]
    [InlineData("http://app.example.com:80", "Leave out :80")]
    [InlineData("https://user:pw@app.example.com", "user name")]
    [InlineData("ftp://app.example.com", "Start with https://")]
    [InlineData("app.example.com", "Start with https://")]
    [InlineData("https://bücher.example", "xn--")]
    [InlineData("https://app example.com", "spaces")]
    [InlineData("https://app.example.com:99999", "port")]
    [InlineData("https://-bad-.example.com", "host isn't valid")]
    [InlineData("", "Enter an origin")]
    public void Anything_but_an_exact_origin_is_refused_with_the_reason(string input, string reason)
    {
        var check = OriginRules.CheckOrigin(input, isDevelopment: true);

        Assert.False(check.Ok);
        Assert.Contains(reason, check.Problem);
    }

    [Fact]
    public void Plain_http_is_only_for_localhost_outside_development()
    {
        Assert.Contains("Use https://", OriginRules.CheckOrigin("http://app.example.com", isDevelopment: false).Problem);
        Assert.True(OriginRules.CheckOrigin("http://localhost:3000", isDevelopment: false).Ok);
        Assert.True(OriginRules.CheckOrigin("http://app.example.com", isDevelopment: true).Ok);
    }

    // ------------------------------------------------------------------ patterns

    [Theory]
    [InlineData("https://*.com")]
    [InlineData("https://*")]
    [InlineData("https://app.*.com")]
    [InlineData("https://*.example.com/")]
    [InlineData("*.example.com")]
    public void A_pattern_must_be_narrow_and_well_formed(string pattern)
    {
        Assert.False(OriginRules.CheckPattern(pattern).Ok);
    }

    [Theory]
    [InlineData("https://portal.example.com", true)]
    [InlineData("https://a.b.example.com", true)]
    [InlineData("https://example.com", false)]          // the domain itself isn't "under" it
    [InlineData("https://evilexample.com", false)]
    [InlineData("http://portal.example.com", false)]    // scheme must match
    [InlineData("https://portal.example.com:8443", false)] // port must match
    [InlineData("https://portal.example.com.evil.test", false)]
    public void A_pattern_matches_only_hosts_under_its_domain_with_the_same_scheme_and_port(string origin, bool matches)
    {
        Assert.Equal(matches, OriginRules.Matches(origin, "https://*.example.com"));
    }

    // ------------------------------------------------------------------ admin entries

    [Fact]
    public void An_admin_entry_must_fit_the_operations_patterns()
    {
        var check = OriginRules.CheckAdminEntry("https://portal.other.test", null, [], Limited, isDevelopment: false);

        Assert.Contains("https://*.example.com", check.Problem);
    }

    [Fact]
    public void An_admin_entry_can_not_repeat_an_origin_already_allowed()
    {
        Assert.Contains("Operations already allow", OriginRules.CheckAdminEntry("https://app.example.com", null, [], Limited, false).Problem);
        Assert.Contains("already on the list",
            OriginRules.CheckAdminEntry("https://p.example.com", null, [new BrowserOrigin { Origin = "https://p.example.com" }], Limited, false).Problem);
    }

    [Fact]
    public void At_most_fifty_origins_can_be_added()
    {
        var fifty = Enumerable.Range(1, 50).Select(i => new BrowserOrigin { Origin = $"https://p{i}.example.com" }).ToList();

        var check = OriginRules.CheckAdminEntry("https://p51.example.com", null, fifty, Limited, isDevelopment: false);

        Assert.Contains("50", check.Problem);
    }

    [Fact]
    public void A_note_stays_short_and_on_one_line()
    {
        Assert.NotNull(OriginRules.CheckAdminEntry("https://p.example.com", new string('x', 201), [], Limited, false).Problem);
        Assert.NotNull(OriginRules.CheckAdminEntry("https://p.example.com", "line\nbreak", [], Limited, false).Problem);
        Assert.True(OriginRules.CheckAdminEntry("https://p.example.com", "Patient portal", [], Limited, false).Ok);
    }

    [Fact]
    public void Invalid_settings_are_refused_naming_the_setting()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            OriginRules.ValidateSettings(new BrowserAccessSettings { AllowedOrigins = ["*"] }, isDevelopment: true));

        Assert.Contains("Gateway:Cors:AllowedOrigins", error.Message);
    }

    // ------------------------------------------------------------------ the policy

    [Fact]
    public void The_policy_allows_the_methods_and_header_inputs_of_published_recipes_and_never_credentials()
    {
        var recipe = new Recipe
        {
            Id = "r", Route = "r", Method = "POST",
            Inputs = new(StringComparer.OrdinalIgnoreCase) { ["header.X-Tenant"] = new InputDef(), ["query.q"] = new InputDef() }
        };
        var policy = BrowserAccessPolicy.From(new ConfigSnapshot([], [], [recipe], "v1"), Limited, isDevelopment: false);

        Assert.Equal(["OPTIONS", "POST"], policy.Methods);
        Assert.Equal(["Authorization", "Content-Type", "X-Correlation-Id", "X-Tenant"], policy.Headers);
        Assert.True(policy.Allows("https://app.example.com"));
    }

    [Fact]
    public void The_policy_skips_admin_entries_that_no_longer_fit_and_says_why()
    {
        var snapshot = new ConfigSnapshot([], [], [], "v1", [
            new BrowserOrigin { Origin = "https://portal.example.com", AddedByName = "Dana Tester", AddedAt = new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.Zero) },
            new BrowserOrigin { Origin = "https://portal.other.test" },
            new BrowserOrigin { Origin = "*" }
        ]);

        var policy = BrowserAccessPolicy.From(snapshot, Limited, isDevelopment: false);

        Assert.True(policy.Allows("https://portal.example.com"));
        Assert.False(policy.Allows("https://portal.other.test"));
        Assert.Equal(2, policy.Skipped.Count);
        Assert.Equal("Added here by Dana Tester on 20 Nov 2026.", policy.Test("https://portal.example.com", false).Reason);
        Assert.StartsWith("It's on the list but not used", policy.Test("https://portal.other.test", false).Reason);
        Assert.Equal("Operations set it in the app settings.", policy.Test("https://app.example.com", false).Reason);
        Assert.Equal("It isn't on the list. Someone with the admin role can add it.", policy.Test("https://new.example.com", false).Reason);
    }

    // ------------------------------------------------------------------ the store

    private FileConfigStore Store() =>
        new(Options.Create(new FileConfigOptions { Directory = _dir, ReloadCheckSeconds = 1 }), NullLogger<FileConfigStore>.Instance);

    private static BrowserAccessHistoryEntry Entry(int number, string origin) =>
        new() { Number = number, Action = BrowserAccessAction.Added, Origin = origin, At = DateTimeOffset.UtcNow, By = "admin-1", ByName = "Ada Admin" };

    [Fact]
    public async Task Saving_origins_changes_the_config_version_and_appends_to_the_history()
    {
        var store = Store();
        var before = (await store.GetSnapshotAsync()).Version;

        await store.SaveBrowserOriginsAsync([new BrowserOrigin { Origin = "https://p.example.com" }], Entry(1, "https://p.example.com"));

        var after = await store.GetSnapshotAsync();
        Assert.NotEqual(before, after.Version);
        Assert.Equal("https://p.example.com", Assert.Single(after.BrowserOrigins).Origin);
        var entry = Assert.Single(await store.GetBrowserAccessHistoryAsync());
        Assert.Equal(("admin-1", "Ada Admin"), (entry.By, entry.ByName));
        Assert.Empty(await store.GetHistoryIdsAsync()); // kept apart from recipe history
    }

    [Fact]
    public async Task A_history_number_is_never_reused_and_nothing_changes_when_it_is()
    {
        var store = Store();
        await store.SaveBrowserOriginsAsync([new BrowserOrigin { Origin = "https://a.example.com" }], Entry(1, "https://a.example.com"));

        await Assert.ThrowsAsync<HistoryConflictException>(() =>
            store.SaveBrowserOriginsAsync([new BrowserOrigin { Origin = "https://b.example.com" }], Entry(1, "https://b.example.com")));

        Assert.Equal("https://a.example.com", Assert.Single((await store.GetSnapshotAsync()).BrowserOrigins).Origin);
        Assert.Equal("https://a.example.com", Assert.Single(await store.GetBrowserAccessHistoryAsync()).Origin);
    }
}
