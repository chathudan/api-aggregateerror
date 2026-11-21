using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ApiAggregator.Admin.Application;
using ApiAggregator.Admin.Hosting;
using ApiAggregator.Engine.BrowserAccess;
using ApiAggregator.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace ApiAggregator.Admin.Tests;

/// <summary>
/// Browser access settings for the gateway (ADR 0015): anyone with a role may look, only the admin role may change,
/// both from a page (AdminService) and from /admin/cors; and the admin app itself answers no cross-origin calls.
/// </summary>
public sealed class BrowserAccessTests(AdminApp app) : IClassFixture<AdminApp>
{
    private static readonly AdminActor Author = new("author-1", "Ann Author", IsAuthor: true, IsPublisher: false);
    private static readonly AdminActor Publisher = new("publisher-1", "Pat Publisher", IsAuthor: true, IsPublisher: true);
    private static readonly AdminActor Admin = new("admin-1", "Ada Admin", IsAuthor: false, IsPublisher: false, IsAdmin: true);

    private AdminService Service => app.Services.GetRequiredService<AdminService>();

    private HttpClient Client(string scope, string subject = "script-1")
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestAuth.Token(scope, subject));
        return client;
    }

    [Theory]
    [InlineData("author")]
    [InlineData("publisher")]
    public async Task Authors_and_publishers_can_look_and_test_but_not_change_browser_access(string who)
    {
        var actor = who == "author" ? Author : Publisher;

        var view = await Service.GetBrowserAccessAsync(actor);
        Assert.True(view.Ok);
        Assert.False(view.Value!.CanChange);
        Assert.False(view.Value.AllowsCredentials);
        Assert.True((await Service.TestOriginAsync(actor, new OriginTestRequest("https://x.example.test"))).Ok);

        // From a page...
        var add = await Service.AddBrowserOriginAsync(actor, new AddOriginRequest("https://refused.example.test", null));
        Assert.Equal(FailureKind.Forbidden, add.Failure?.Kind);
        Assert.Equal(FailureKind.Forbidden, (await Service.RemoveBrowserOriginAsync(actor, "https://refused.example.test")).Failure?.Kind);

        // ...and from a script.
        var client = Client($"aggregator.{who}");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/admin/cors")).StatusCode);
        var fromApi = await client.PostAsJsonAsync("/admin/cors/origins", new { origin = "https://refused.example.test" });
        Assert.Equal(HttpStatusCode.Forbidden, fromApi.StatusCode);

        Assert.DoesNotContain((await Service.GetBrowserAccessAsync(actor)).Value!.Origins, o => o.Origin == "https://refused.example.test");
    }

    [Fact]
    public async Task An_admin_adds_and_removes_an_origin_and_the_history_records_who()
    {
        var added = await Service.AddBrowserOriginAsync(Admin, new AddOriginRequest(" https://Portal.Example.test ", "Patient portal"));
        Assert.True(added.Ok, added.Failure?.Message);
        Assert.Equal("https://portal.example.test", added.Value!.Origin); // stored exactly as browsers send it

        var view = (await Service.GetBrowserAccessAsync(Admin)).Value!;
        Assert.True(view.CanChange);
        var row = Assert.Single(view.Origins, o => o.Origin == "https://portal.example.test");
        Assert.Equal(OriginSource.Admin, row.Source);
        Assert.Equal("Patient portal", row.Note);
        Assert.True((await Service.TestOriginAsync(Author, new OriginTestRequest("https://portal.example.test"))).Value!.Allowed);

        Assert.True((await Service.RemoveBrowserOriginAsync(Admin, "https://portal.example.test")).Ok);

        var history = (await Service.GetBrowserAccessHistoryAsync(Author)).Value!.Where(h => h.Origin == "https://portal.example.test").ToList();
        Assert.Equal([BrowserAccessAction.Removed, BrowserAccessAction.Added], history.Select(h => h.Action));
        Assert.All(history, h => { Assert.Equal("admin-1", h.By); Assert.Equal("Ada Admin", h.ByName); });
        Assert.True(history[0].Number > history[1].Number);

        // Written once to the history folder (append-only).
        Assert.True(File.Exists(Path.Combine(app.ConfigDir, "history", "cors", $"{history[1].Number}.json")));
    }

    [Fact]
    public async Task An_admin_edits_an_origin_and_the_history_records_what_it_was()
    {
        Assert.True((await Service.AddBrowserOriginAsync(Admin, new AddOriginRequest("https://typo.example.test", "Portal"))).Ok);

        var changed = await Service.ChangeBrowserOriginAsync(Admin, new ChangeOriginRequest("https://typo.example.test", " https://Fixed.Example.test ", "Patient portal"));

        Assert.True(changed.Ok, changed.Failure?.Message);
        Assert.Equal("https://fixed.example.test", changed.Value!.Origin); // checked and written like a new one
        var origins = (await Service.GetBrowserAccessAsync(Admin)).Value!.Origins;
        Assert.DoesNotContain(origins, o => o.Origin == "https://typo.example.test");
        Assert.Equal("Patient portal", Assert.Single(origins, o => o.Origin == "https://fixed.example.test").Note);
        Assert.False((await Service.TestOriginAsync(Author, new OriginTestRequest("https://typo.example.test"))).Value!.Allowed);

        var entry = (await Service.GetBrowserAccessHistoryAsync(Author)).Value!.First(h => h.Origin == "https://fixed.example.test");
        Assert.Equal(BrowserAccessAction.Changed, entry.Action);
        Assert.Equal(("https://typo.example.test", "Portal"), (entry.PreviousOrigin, entry.PreviousNote));
        Assert.Equal("admin-1", entry.By);

        // Saving it unchanged records nothing.
        var before = (await Service.GetBrowserAccessHistoryAsync(Author)).Value!.Count;
        Assert.True((await Service.ChangeBrowserOriginAsync(Admin, new ChangeOriginRequest("https://fixed.example.test", "https://fixed.example.test", "Patient portal"))).Ok);
        Assert.Equal(before, (await Service.GetBrowserAccessHistoryAsync(Author)).Value!.Count);
    }

    [Theory]
    [InlineData("*", "every website")]
    [InlineData("https://edit-other.example.test", "already on the list")]
    [InlineData("http://edit-me.example.test", "Use https://")]
    public async Task An_edit_is_checked_like_a_new_origin(string newOrigin, string reason)
    {
        await Service.AddBrowserOriginAsync(Admin, new AddOriginRequest("https://edit-me.example.test", null));
        await Service.AddBrowserOriginAsync(Admin, new AddOriginRequest("https://edit-other.example.test", null));

        var result = await Service.ChangeBrowserOriginAsync(Admin, new ChangeOriginRequest("https://edit-me.example.test", newOrigin, null));

        Assert.Equal(FailureKind.BadRequest, result.Failure?.Kind);
        Assert.Contains(reason, result.Failure!.Message);
        Assert.Contains((await Service.GetBrowserAccessAsync(Admin)).Value!.Origins, o => o.Origin == "https://edit-me.example.test");
    }

    [Fact]
    public async Task Only_the_admin_role_can_edit_and_only_origins_added_here()
    {
        await Service.AddBrowserOriginAsync(Admin, new AddOriginRequest("https://not-yours.example.test", null));

        var byPublisher = await Service.ChangeBrowserOriginAsync(Publisher, new ChangeOriginRequest("https://not-yours.example.test", "https://mine.example.test", null));
        Assert.Equal(FailureKind.Forbidden, byPublisher.Failure?.Kind);
        var fromApi = await Client("aggregator.publisher").PutAsJsonAsync("/admin/cors/origins",
            new { origin = "https://not-yours.example.test", newOrigin = "https://mine.example.test" });
        Assert.Equal(HttpStatusCode.Forbidden, fromApi.StatusCode);

        var missing = await Service.ChangeBrowserOriginAsync(Admin, new ChangeOriginRequest("https://never-added.example.test", "https://x.example.test", null));
        Assert.Equal(FailureKind.NotFound, missing.Failure?.Kind);

        var byAdminApi = await Client("aggregator.admin").PutAsJsonAsync("/admin/cors/origins",
            new { origin = "https://not-yours.example.test", newOrigin = "https://renamed.example.test", note = "from a script" });
        Assert.Equal(HttpStatusCode.OK, byAdminApi.StatusCode);
    }

    [Fact]
    public async Task An_admin_token_can_change_browser_access_through_the_admin_api()
    {
        var client = Client("aggregator.admin", subject: "ops-script");

        var added = await client.PostAsJsonAsync("/admin/cors/origins", new { origin = "https://api-added.example.test", note = "from a script" });
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        var removed = await client.DeleteAsync("/admin/cors/origins?origin=" + Uri.EscapeDataString("https://api-added.example.test"));
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);

        var history = await client.GetFromJsonAsync<JsonElement>("/admin/cors/history");
        Assert.Contains(history.EnumerateArray(), h => h.GetProperty("by").GetString() == "ops-script" && h.GetProperty("action").GetString() == "removed");
    }

    [Theory]
    [InlineData("*", "every website")]
    [InlineData("null", "sandboxed")]
    [InlineData("https://portal.example.test/", "trailing slash")]
    [InlineData("http://portal.example.test", "Use https://")] // the tests run as Production
    public async Task Origins_that_would_open_access_too_wide_are_refused_with_the_reason(string origin, string reason)
    {
        var response = await Client("aggregator.admin").PostAsJsonAsync("/admin/cors/origins", new { origin });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(reason, body.GetProperty("title").GetString());
        Assert.Equal("origin", body.GetProperty("field").GetString());
    }

    [Fact]
    public async Task Operations_origins_and_limits_are_respected()
    {
        using var limited = new AdminApp
        {
            Settings =
            {
                ["Gateway:Cors:AllowedOrigins:0"] = "https://app.example.test",
                ["Gateway:Cors:AllowedOriginPatterns:0"] = "https://*.example.test"
            }
        };
        var service = limited.Services.GetRequiredService<AdminService>();

        var outside = await service.AddBrowserOriginAsync(Admin, new AddOriginRequest("https://portal.other.test", null));
        Assert.Contains("https://*.example.test", outside.Failure?.Title);

        var removeOperations = await service.RemoveBrowserOriginAsync(Admin, "https://app.example.test");
        Assert.Contains("Operations set this origin", removeOperations.Failure?.Title);
        var editOperations = await service.ChangeBrowserOriginAsync(Admin, new ChangeOriginRequest("https://app.example.test", "https://other.example.test", null));
        Assert.Contains("Operations set this origin", editOperations.Failure?.Title);

        var view = (await service.GetBrowserAccessAsync(Admin)).Value!;
        Assert.Equal(OriginSource.Operations, Assert.Single(view.Origins).Source);
        Assert.Equal(["https://*.example.test"], view.Patterns);
    }

    [Fact]
    public async Task The_admin_role_alone_gives_no_rights_on_endpoints()
    {
        Assert.Equal(FailureKind.Forbidden, (await Service.GetServicesAsync(Admin)).Failure?.Kind);
        Assert.Equal(HttpStatusCode.Forbidden, (await Client("aggregator.admin").GetAsync("/admin/recipes")).StatusCode);
    }

    [Fact]
    public async Task Without_a_role_or_a_token_browser_access_is_closed()
    {
        Assert.Equal(FailureKind.Forbidden, (await Service.GetBrowserAccessAsync(AdminActor.Anonymous)).Failure?.Kind);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.CreateClient().GetAsync("/admin/cors")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Client("customer.read").GetAsync("/admin/cors")).StatusCode);
    }

    [Fact]
    public void The_development_sign_in_can_add_the_admin_role()
    {
        Assert.True(AdminActor.From(DevSignIn.Principal("Ada", "author", admin: true)).IsAdmin);
        Assert.False(AdminActor.From(DevSignIn.Principal("Pat", "publisher")).IsAdmin); // publishers aren't admins
    }

    // ------------------------------------------------------------------ the admin app allows no cross-origin calls

    [Theory]
    [InlineData("/admin/recipes")]
    [InlineData("/admin/cors")]
    public async Task A_cross_origin_call_to_the_admin_api_gets_no_cors_headers(string path)
    {
        var client = Client("aggregator.author aggregator.admin");

        var preflight = new HttpRequestMessage(HttpMethod.Options, path);
        preflight.Headers.Add("Origin", "https://evil.test");
        preflight.Headers.Add("Access-Control-Request-Method", "POST");
        var get = new HttpRequestMessage(HttpMethod.Get, path);
        get.Headers.Add("Origin", "https://evil.test");

        foreach (var response in new[] { await client.SendAsync(preflight), await client.SendAsync(get) })
            Assert.DoesNotContain(response.Headers, h => h.Key.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase));
    }
}
