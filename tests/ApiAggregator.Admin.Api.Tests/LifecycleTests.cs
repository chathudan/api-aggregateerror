using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using ApiAggregator.Engine.Config;
using ApiAggregator.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ApiAggregator.Admin.Api.Tests;

/// <summary>History, roll back, deleting and discarding (ADR 0012). Each test uses its own endpoint and service ids.</summary>
public class LifecycleTests(AdminApp app) : IClassFixture<AdminApp>
{
    private const string Publisher = "aggregator.author aggregator.publisher";

    // ---------------------------------------------------------------- history

    [Fact]
    public async Task Publishing_records_who_published_and_when()
    {
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        await Publish("hist-who");

        var entry = Assert.Single(await History("hist-who"));
        Assert.Equal(1, entry["version"]!.GetValue<int>());
        Assert.Equal("published", entry["action"]!.GetValue<string>());
        Assert.Equal("test-user", entry["by"]!.GetValue<string>());
        Assert.InRange(entry["at"]!.GetValue<DateTimeOffset>(), before, DateTimeOffset.UtcNow.AddSeconds(1));
        Assert.True(entry["isLive"]!.GetValue<bool>());
    }

    [Fact]
    public async Task History_written_before_who_and_when_were_stored_is_still_listed()
    {
        Directory.CreateDirectory(Path.Combine(app.ConfigDir, "history"));
        await File.WriteAllTextAsync(Path.Combine(app.ConfigDir, "history", "hist-legacy.v1.json"), RecipeJson("hist-legacy", "Old endpoint"));

        var entry = Assert.Single(await History("hist-legacy"));
        Assert.Equal(1, entry["version"]!.GetValue<int>());
        Assert.Equal("published", entry["action"]!.GetValue<string>());
        Assert.Null(entry["by"]);
        Assert.Null(entry["at"]);
        Assert.False(entry["isLive"]!.GetValue<bool>());
    }

    [Fact]
    public async Task View_returns_the_content_of_one_version()
    {
        await Publish("hist-view", "First name");
        await Publish("hist-view", "Second name");

        var v1 = JsonNode.Parse(await Client(Publisher).GetStringAsync("/admin/recipes/hist-view/history/1"))!;
        Assert.Equal("First name", v1["name"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.NotFound, (await Client(Publisher).GetAsync("/admin/recipes/hist-view/history/9")).StatusCode);
    }

    // ---------------------------------------------------------------- roll back

    [Fact]
    public async Task Rolling_back_publishes_the_old_content_as_a_new_version_and_records_where_it_came_from()
    {
        await Publish("rb-basic", "First name");
        await Publish("rb-basic", "Second name");

        var response = await Client(Publisher).PostAsync("/admin/recipes/rb-basic/rollback/1", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, (await Json(response))["version"]!.GetValue<int>());
        Assert.Equal("First name", (await Live("rb-basic"))!.Name);
        var newest = (await History("rb-basic"))[0]!;
        Assert.Equal("rolledBack", newest["action"]!.GetValue<string>());
        Assert.Equal(1, newest["restoredFrom"]!.GetValue<int>());
        Assert.Equal("test-user", newest["by"]!.GetValue<string>());
    }

    [Fact]
    public async Task Publishing_a_draft_after_a_roll_back_never_reuses_a_version()
    {
        await Publish("rb-versions", "First name");
        await SaveDraft("rb-versions", "Draft name");                                    // saved as v2 at this point
        Assert.Equal(HttpStatusCode.OK, (await Client(Publisher).PostAsync("/admin/recipes/rb-versions/rollback/1", null)).StatusCode); // takes v2
        var rolledBack = await File.ReadAllTextAsync(HistoryFile("rb-versions.v2"));

        await TestAndPublish("rb-versions");

        Assert.Equal(3, (await Live("rb-versions"))!.Version);
        Assert.Equal([3, 2, 1], (await History("rb-versions")).Select(e => e!["version"]!.GetValue<int>()));
        Assert.Equal(rolledBack, await File.ReadAllTextAsync(HistoryFile("rb-versions.v2")));    // history entries never change
    }

    [Fact]
    public async Task A_version_that_no_longer_works_with_the_services_is_not_rolled_back()
    {
        await SaveService("rb-gone-svc", "Soon changed", withEndpoint: true);
        await Publish("rb-gone", endpoint: "rb-gone-svc/listPeople");
        await Publish("rb-gone");                                       // v2 uses the people service instead
        await SaveService("rb-gone-svc", "Soon changed", withEndpoint: false); // v1's endpoint no longer exists

        var response = await Client(Publisher).PostAsync("/admin/recipes/rb-gone/rollback/1", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.StartsWith("Version 1 can't go live as it is", (await Json(response))["title"]!.GetValue<string>());
        Assert.Equal(2, (await Live("rb-gone"))!.Version);
    }

    // ---------------------------------------------------------------- delete an endpoint (unpublish)

    [Fact]
    public async Task Deleting_an_endpoint_stops_the_gateway_serving_it_and_keeps_its_history()
    {
        await Publish("del-basic");
        var gateway = GatewayStore(); // a separate store on the same folder, like the gateway process
        Assert.NotNull((await gateway.GetSnapshotAsync()).GetRecipe("del-basic"));

        var response = await Client(Publisher).DeleteAsync("/admin/recipes/del-basic");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, (await Json(response))["unpublishedVersion"]!.GetValue<int>());
        Assert.False(File.Exists(Path.Combine(app.ConfigDir, "recipes", "del-basic.json")));
        Assert.Null((await GatewayStore().GetSnapshotAsync()).GetRecipe("del-basic"));
        Assert.Equal(["unpublished", "published"], (await History("del-basic")).Select(e => e!["action"]!.GetValue<string>()));
        Assert.Equal("test-user", (await History("del-basic"))[0]!["by"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.NotFound, (await Client(Publisher).GetAsync("/admin/recipes/del-basic")).StatusCode);
    }

    [Fact]
    public async Task A_deleted_endpoint_is_listed_as_deleted_and_can_be_restored_by_rolling_back()
    {
        await Publish("del-restore");
        await Client(Publisher).DeleteAsync("/admin/recipes/del-restore");
        Assert.True((await ListItem("del-restore"))["deleted"]!.GetValue<bool>());

        var response = await Client(Publisher).PostAsync("/admin/recipes/del-restore/rollback/1", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, (await Live("del-restore"))!.Version);
        Assert.False((await ListItem("del-restore"))["deleted"]!.GetValue<bool>());
    }

    [Fact]
    public async Task An_endpoint_added_straight_to_the_config_folder_can_be_deleted_and_restored()
    {
        await File.WriteAllTextAsync(Path.Combine(app.ConfigDir, "recipes", "by-hand.json"),
            RecipeJson("by-hand", "Added by hand").Replace("\"method\"", "\"status\": \"published\", \"method\""));
        Assert.NotNull(await Live("by-hand"));

        Assert.Equal(HttpStatusCode.OK, (await Client(Publisher).DeleteAsync("/admin/recipes/by-hand")).StatusCode);
        var restore = await Client(Publisher).PostAsync("/admin/recipes/by-hand/rollback/1", null);

        Assert.Equal(HttpStatusCode.OK, restore.StatusCode);
        Assert.Equal("Added by hand", (await Live("by-hand"))!.Name);
        Assert.Equal(2, (await Live("by-hand"))!.Version);
    }

    [Fact]
    public async Task Publishing_again_after_deleting_uses_a_new_version()
    {
        await Publish("del-again");
        var v1 = await File.ReadAllTextAsync(HistoryFile("del-again.v1"));
        await Client(Publisher).DeleteAsync("/admin/recipes/del-again");

        await Publish("del-again");

        Assert.Equal(2, (await Live("del-again"))!.Version);
        Assert.Equal(v1, await File.ReadAllTextAsync(HistoryFile("del-again.v1")));
    }

    [Fact]
    public async Task Deleting_an_endpoint_also_discards_its_draft()
    {
        await Publish("del-draft");
        await SaveDraft("del-draft", "Changed name");

        var body = await Json(await Client(Publisher).DeleteAsync("/admin/recipes/del-draft"));

        Assert.True(body["draftDiscarded"]!.GetValue<bool>());
        Assert.False(File.Exists(Path.Combine(app.ConfigDir, "drafts", "del-draft.json")));
    }

    [Fact]
    public async Task Deleting_an_endpoint_that_does_not_exist_gives_404()
    {
        var response = await Client(Publisher).DeleteAsync("/admin/recipes/never-existed");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---------------------------------------------------------------- discard a draft

    [Fact]
    public async Task Discarding_a_draft_keeps_the_live_version()
    {
        await Publish("discard-basic", "Live name");
        await SaveDraft("discard-basic", "Draft name");

        var response = await Client("aggregator.author").DeleteAsync("/admin/recipes/discard-basic/draft");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, (await Json(response))["liveVersion"]!.GetValue<int>());
        var shown = JsonNode.Parse(await Client(Publisher).GetStringAsync("/admin/recipes/discard-basic"))!;
        Assert.Equal("Live name", shown["name"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.NotFound, (await Client(Publisher).DeleteAsync("/admin/recipes/discard-basic/draft")).StatusCode);
    }

    [Fact]
    public async Task Publishing_leaves_no_unpublished_changes()
    {
        await Publish("discard-after-publish");

        Assert.False((await ListItem("discard-after-publish"))["hasDraft"]!.GetValue<bool>());
    }

    // ---------------------------------------------------------------- roles

    [Fact]
    public async Task Authors_can_read_history_but_not_roll_back_or_delete_endpoints()
    {
        await Publish("roles");
        var author = Client("aggregator.author");

        Assert.Equal(HttpStatusCode.OK, (await author.GetAsync("/admin/recipes/roles/history")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await author.GetAsync("/admin/recipes/roles/history/1")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await author.PostAsync("/admin/recipes/roles/rollback/1", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await author.DeleteAsync("/admin/recipes/roles")).StatusCode);
        Assert.NotNull(await Live("roles"));
    }

    [Theory]
    [InlineData("aggregator.author", false)]
    [InlineData(Publisher, true)]
    public async Task Capabilities_say_whether_the_caller_can_publish(string scope, bool canPublish)
    {
        var body = JsonNode.Parse(await Client(scope).GetStringAsync("/admin/capabilities"))!;

        Assert.Equal(canPublish, body["canPublish"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Sign_in_status_starts_as_not_checked_and_remembers_the_last_check()
    {
        await PutProfile("status-key", """[ { "kind": "ApiKey", "settings": { "name": "X-Api-Key", "value": "secret:status-missing" } } ]""");

        Assert.Equal("notChecked", (await Status("status-key"))["state"]!.GetValue<string>());

        await Client(Publisher).PostAsync("/admin/auth-profiles/status-key/check", null);

        var after = await Status("status-key");
        Assert.Equal("failed", after["state"]!.GetValue<string>());
        Assert.Contains("status-missing", after["message"]!.GetValue<string>()); // names the secret to add, never a value
        Assert.NotNull(after["checkedAt"]);
    }

    [Fact]
    public async Task Passing_on_the_callers_token_counts_as_complete_not_failed()
    {
        await PutProfile("status-caller", """[ { "kind": "PassThroughUserToken", "settings": {} } ]""");

        var all = JsonNode.Parse(await (await Client(Publisher).PostAsync("/admin/auth-profiles/check-all", null)).Content.ReadAsStringAsync())!.AsArray();

        Assert.Equal("settingsOk", all.Single(s => s!["profileId"]!.GetValue<string>() == "status-caller")!["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task Capabilities_say_when_the_config_is_not_sample_data()
    {
        var body = JsonNode.Parse(await Client(Publisher).GetStringAsync("/admin/capabilities"))!;

        Assert.False(body["sampleData"]!.GetValue<bool>()); // the test folder has no SAMPLE-DATA.md
    }

    [Fact]
    public async Task Deleting_needs_a_token()
    {
        var anonymous = app.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.DeleteAsync("/admin/recipes/anything")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.DeleteAsync("/admin/recipes/anything/draft")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.DeleteAsync("/admin/services/anything")).StatusCode);
    }

    [Theory]
    [InlineData("DELETE", "/admin/recipes/..%2Fservices")]
    [InlineData("DELETE", "/admin/recipes/Orders")]
    [InlineData("DELETE", "/admin/recipes/a*b/draft")]
    [InlineData("GET", "/admin/recipes/a*/history")]
    [InlineData("GET", "/admin/recipes/..%2F..%2Fetc/history/1")]
    [InlineData("POST", "/admin/recipes/x.v1/rollback/1")]
    [InlineData("DELETE", "/admin/services/..%2Frecipes")]
    [InlineData("GET", "/admin/services/a*/usage")]
    public async Task Ids_that_are_not_lowercase_slugs_are_refused_before_anything_is_touched(string method, string url)
    {
        var before = Directory.GetFiles(app.ConfigDir, "*", SearchOption.AllDirectories).Length;

        var response = await Client(Publisher).SendAsync(new HttpRequestMessage(new HttpMethod(method), url));

        Assert.True(response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound, $"{method} {url} gave {(int)response.StatusCode}");
        Assert.Equal(before, Directory.GetFiles(app.ConfigDir, "*", SearchOption.AllDirectories).Length);
    }

    // ---------------------------------------------------------------- services

    [Fact]
    public async Task A_service_used_by_a_published_endpoint_cannot_be_deleted_and_the_endpoints_are_named()
    {
        await SaveService("svc-in-use", "In use", withEndpoint: true);
        await Publish("uses-svc", "Uses the service", endpoint: "svc-in-use/listPeople");

        var response = await Client(Publisher).DeleteAsync("/admin/services/svc-in-use");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await Json(response);
        Assert.Contains("Uses the service", body["title"]!.GetValue<string>());
        Assert.Equal("uses-svc", Assert.Single(body["published"]!.AsArray())!["id"]!.GetValue<string>());
        Assert.NotNull((await GatewayStore().GetSnapshotAsync()).GetService("svc-in-use"));
    }

    [Fact]
    public async Task An_unused_service_is_deleted_and_drafts_that_used_it_are_listed()
    {
        await SaveService("svc-free", "Free", withEndpoint: true);
        await SaveDraft("draft-uses-free", "Draft using it", endpoint: "svc-free/listPeople");

        var usage = JsonNode.Parse(await Client(Publisher).GetStringAsync("/admin/services/svc-free/usage"))!;
        Assert.Empty(usage["published"]!.AsArray());
        Assert.Equal("draft-uses-free", Assert.Single(usage["drafts"]!.AsArray())!["id"]!.GetValue<string>());

        var response = await Client(Publisher).DeleteAsync("/admin/services/svc-free");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("draft-uses-free", Assert.Single((await Json(response))["draftsAffected"]!.AsArray())!["id"]!.GetValue<string>());
        Assert.Null((await GatewayStore().GetSnapshotAsync()).GetService("svc-free"));
        Assert.Equal(HttpStatusCode.NotFound, (await Client(Publisher).DeleteAsync("/admin/services/svc-free")).StatusCode);
    }

    [Fact]
    public async Task Renaming_a_service_keeps_its_id_so_published_endpoints_keep_working()
    {
        await SaveService("svc-rename", "Old name", withEndpoint: true);
        await Publish("uses-renamed", endpoint: "svc-rename/listPeople");

        await SaveService("svc-rename", "New name", withEndpoint: true);

        var snapshot = await GatewayStore().GetSnapshotAsync();
        Assert.Equal("New name", snapshot.GetService("svc-rename")!.Name);
        Assert.True(Engine.Validation.RecipeValidator.Validate(snapshot.GetRecipe("uses-renamed")!, snapshot).IsValid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Line\nbreak")]
    public async Task A_service_name_must_be_given_and_plain(string name)
    {
        var response = await PutService("svc-bad-name", name, withEndpoint: false);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Give the service a name of up to 100 characters.", (await Json(response))["title"]!.GetValue<string>());
    }

    // ---------------------------------------------------------------- helpers

    private static string RecipeJson(string id, string name = "Test endpoint", string endpoint = "people/listPeople") => $$$"""
        {
          "id": "{{{id}}}", "name": "{{{name}}}", "method": "GET", "route": "{{{id}}}",
          "requireAuthentication": true, "requiredScope": "people.read",
          "steps": [ { "alias": "crm", "endpoint": "{{{endpoint}}}" } ],
          "response": { "people": "{{crm.body.items}}" }
        }
        """;

    private HttpClient Client(string scope)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestAuth.Token(scope));
        return client;
    }

    private async Task SaveDraft(string id, string name = "Test endpoint", string endpoint = "people/listPeople")
    {
        var response = await Client(Publisher).PutAsync($"/admin/recipes/{id}", new StringContent(RecipeJson(id, name, endpoint), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task TestAndPublish(string id)
    {
        var test = await Json(await Client(Publisher).PostAsync($"/admin/recipes/{id}/test", new StringContent("{}", Encoding.UTF8, "application/json")));
        Assert.True(test["passed"]!.GetValue<bool>(), test.ToJsonString());
        var publish = await Client(Publisher).PostAsync($"/admin/recipes/{id}/publish", null);
        Assert.True(publish.IsSuccessStatusCode, await publish.Content.ReadAsStringAsync());
    }

    private async Task Publish(string id, string name = "Test endpoint", string endpoint = "people/listPeople")
    {
        await SaveDraft(id, name, endpoint);
        await TestAndPublish(id);
    }

    /// <summary>A service on the stubbed people.test host, optionally with its listPeople endpoint.</summary>
    private Task<HttpResponseMessage> PutService(string id, string name, bool withEndpoint)
    {
        var endpoints = withEndpoint ? """[ { "id": "listPeople", "method": "GET", "path": "/people" } ]""" : "[]";
        var json = $$"""{ "id": "{{id}}", "name": {{System.Text.Json.JsonSerializer.Serialize(name)}}, "baseUrl": "http://people.test/api", "endpoints": {{endpoints}} }""";
        return Client(Publisher).PutAsync($"/admin/services/{id}", new StringContent(json, Encoding.UTF8, "application/json"));
    }

    private async Task SaveService(string id, string name, bool withEndpoint) =>
        Assert.Equal(HttpStatusCode.OK, (await PutService(id, name, withEndpoint)).StatusCode);

    private async Task PutProfile(string id, string stepsJson)
    {
        var json = $$"""{ "id": "{{id}}", "name": "{{id}}", "steps": {{stepsJson}} }""";
        var response = await Client(Publisher).PutAsync($"/admin/auth-profiles/{id}", new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    private async Task<JsonNode> Status(string profileId) =>
        JsonNode.Parse(await Client(Publisher).GetStringAsync("/admin/auth-profiles/status"))!.AsArray()
            .Single(s => s!["profileId"]!.GetValue<string>() == profileId)!;

    private async Task<JsonArray> History(string id) =>
        JsonNode.Parse(await Client(Publisher).GetStringAsync($"/admin/recipes/{id}/history"))!.AsArray();

    private async Task<JsonNode> ListItem(string id) =>
        JsonNode.Parse(await Client(Publisher).GetStringAsync("/admin/recipes"))!.AsArray().Single(r => r!["id"]!.GetValue<string>() == id)!;

    private async Task<Engine.Model.Recipe?> Live(string id) => (await GatewayStore().GetSnapshotAsync()).GetRecipe(id);

    private string HistoryFile(string name) => Path.Combine(app.ConfigDir, "history", name + ".json");

    /// <summary>A fresh store on the same folder: what the gateway process sees.</summary>
    private FileConfigStore GatewayStore() =>
        new(Options.Create(new FileConfigOptions { Directory = app.ConfigDir }), NullLogger<FileConfigStore>.Instance);

    private static async Task<JsonNode> Json(HttpResponseMessage response) => JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
}
