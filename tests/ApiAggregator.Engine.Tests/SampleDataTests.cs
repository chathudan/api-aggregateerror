using System.Text.Json;
using System.Text.Json.Nodes;
using ApiAggregator.Engine.Auth;
using ApiAggregator.Engine.Auth.Steps;
using ApiAggregator.Engine.Config;
using ApiAggregator.Engine.Execution;
using ApiAggregator.Engine.Model;
using ApiAggregator.Engine.Validation;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MockServices;

namespace ApiAggregator.Engine.Tests;

/// <summary>
/// The sample recipes in config/recipes (people-directory, people-newest, contacts-unique), run against the
/// mock data in samples/MockServices/PeopleSamples.cs, without the mocks running. These are the exact
/// results samples/README.md promises QA; if one changes, update the README in the same change.
/// </summary>
public class SampleDataTests
{
    // ---------------------------------------------------------------- people-directory (first list wins)

    [Fact]
    public async Task People_directory_gives_10_records_with_the_documented_counts()
    {
        var result = await Run("people-directory");

        Assert.Equal(200, result.StatusCode);
        Assert.False(result.Partial);
        Assert.Equal(10, result.Body!["count"]!.GetValue<int>());
        Assert.Equal("identity 6, crm 8, billing 6 → 10 records (10 matched, 1 unmapped, 1 ambiguous, 1 without a key)", CombineLine(result));
    }

    [Fact]
    public async Task People_directory_puts_every_record_where_the_readme_says()
    {
        var people = (await Run("people-directory")).Body!["people"]!.AsArray();

        Assert.Equal(
        [
            "u-1 | Ava Thompson | ava.thompson@example.com | +1 202 555 0101 | family | ACC-1 | identity,crm,billing",
            "u-2 | Ben Okafor | ben.okafor@example.com | - | single | ACC-2 | identity,crm,billing",
            "u-3 | Chloe Nguyen | chloe.nguyen@example.com | +1 202 555 0103 | - | - | identity,crm",
            "u-4 | Daniel Kowalski | daniel.kowalski@example.com | +1 202 555 0104 | premium | ACC-3 | identity,crm,billing",
            "u-5 | Emma Rossi | emma.rossi@example.com | - | - | - | identity",
            "u-6 | Farid Haddad | farid.haddad@example.com | - | - | - | identity",
            "c-105 | Grace Lee | grace.lee@example.com | - | - | - | crm",
            "c-106 | Hugo Martin | hugo.martin@example.com | +1 202 555 0106 | single | ACC-4 | crm,billing",
            "- | - | - | - | family | ACC-5 | billing",
            "- | - | - | - | premium | ACC-6 | billing"
        ], people.Select(Summary));
    }

    [Fact]
    public async Task Spacing_and_case_in_the_crm_link_still_match_with_trim_and_lower()
    {
        var ben = Person(await Run("people-directory"), "u-2");

        Assert.Equal(" U-2", ben["identityUserId"]!.GetValue<string>()); // the CRM's own value is kept as it was
        Assert.Equal("identity,crm,billing", Sources(ben));
    }

    [Fact]
    public async Task Without_lower_the_crm_contact_and_the_identity_user_stay_separate()
    {
        var result = await Run("people-directory", combine => combine["normalize"] = new JsonArray("trim"));

        // " U-2" trims to "U-2", which is not "u-2". Billing ACC-2 links to "U-2" too, so it joins the CRM record.
        Assert.Equal("identity 6, crm 8, billing 6 → 11 records (9 matched, 1 unmapped, 1 ambiguous, 1 without a key)", CombineLine(result));
        Assert.Equal("identity", Sources(Person(result, "u-2")));
        Assert.Equal("crm,billing", Sources(Person(result, "c-102")));
    }

    [Fact]
    public async Task An_account_linked_to_two_users_is_never_merged_into_either()
    {
        var result = await Run("people-directory");

        Assert.Equal("identity", Sources(Person(result, "u-5")));
        Assert.Equal("identity", Sources(Person(result, "u-6")));
        var acc6 = result.Body!["people"]!.AsArray().Single(p => p!["accountNo"]?.GetValue<string>() == "ACC-6")!;
        Assert.Null(acc6["id"]);
        Assert.Equal("billing", Sources(acc6));
    }

    [Fact]
    public async Task With_without_key_drop_the_unmatched_contact_and_both_unlinked_accounts_are_left_out()
    {
        var result = await Run("people-directory", combine => combine["withoutKey"] = "drop");

        // The counts still say what happened; only the records are left out.
        Assert.Equal("identity 6, crm 8, billing 6 → 7 records (10 matched, 1 unmapped, 1 ambiguous, 1 without a key)", CombineLine(result));
        var ids = result.Body!["people"]!.AsArray().Select(p => p!["id"]?.GetValue<string>()).ToList();
        Assert.Equal(["u-1", "u-2", "u-3", "u-4", "u-5", "u-6", "c-106"], ids);
    }

    // ---------------------------------------------------------------- people-newest (newest:updatedAt)

    [Fact]
    public async Task People_newest_has_the_same_records_and_counts_as_the_directory()
    {
        var result = await Run("people-newest");

        Assert.Equal("identity 6, crm 8, billing 6 → 10 records (10 matched, 1 unmapped, 1 ambiguous, 1 without a key)", CombineLine(result));
        Assert.Equal((await Run("people-directory")).Body!["people"]!.AsArray().Select(Sources), result.Body!["people"]!.AsArray().Select(Sources));
    }

    [Fact]
    public async Task People_newest_takes_the_name_from_the_newer_crm_contact()
    {
        var chloe = Person(await Run("people-newest"), "u-3");

        Assert.Equal("Chloe Nguyen-Smith", chloe["name"]!.GetValue<string>());
        Assert.Equal("2026-04-15T08:00:00Z", chloe["updatedAt"]!.GetValue<string>());
        Assert.Equal("u-3", chloe["id"]!.GetValue<string>()); // owners id: identity, so the CRM's c-103 never replaces it
    }

    [Fact]
    public async Task People_newest_keeps_the_identity_email_because_identity_owns_it()
    {
        var daniel = Person(await Run("people-newest"), "u-4");

        Assert.Equal("daniel.kowalski@example.com", daniel["email"]!.GetValue<string>()); // not the newer CRM "dan.k@example.com"
        Assert.Equal("2026-04-20T08:00:00Z", daniel["updatedAt"]!.GetValue<string>());
    }

    [Fact]
    public async Task People_newest_never_lets_a_newer_null_phone_erase_a_known_one()
    {
        var ava = Person(await Run("people-newest"), "u-1");

        Assert.Equal("2026-05-01T08:00:00Z", ava["updatedAt"]!.GetValue<string>()); // c-107 was the newest...
        Assert.Equal("+1 202 555 0101", ava["phone"]!.GetValue<string>());          // ...but its null phone didn't count
        Assert.Equal("family", ava["plan"]!.GetValue<string>());                    // plan is owned by billing
    }

    // ---------------------------------------------------------------- contacts-unique (distinct, forEach)

    [Fact]
    public async Task Contacts_unique_removes_the_exact_duplicate_contact()
    {
        var body = (await Run("contacts-unique")).Body!;

        Assert.Equal(8, body["contactRows"]!.GetValue<int>());
        Assert.Equal(7, body["uniqueContacts"]!.GetValue<int>());
        Assert.Equal(["c-101", "c-102", "c-103", "c-104", "c-105", "c-106", "c-107"],
            body["contacts"]!.AsArray().Select(c => c!["id"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Contacts_unique_calls_the_notes_endpoint_once_per_unique_contact()
    {
        var downstream = SampleDownstream();
        var result = await Run("contacts-unique", downstream: downstream);

        Assert.Equal(6, result.Body!["orderCount"]!.GetValue<int>());
        Assert.Equal(3, result.Body["notesCalls"]!.GetValue<int>());
        Assert.Equal(["c-101", "c-103", "c-104"], result.Body["notes"]!.AsArray().Select(n => n!["contactId"]!.GetValue<string>()));
        foreach (var id in PeopleSamples.ContactsWithNotes) Assert.Equal(1, downstream.CallsTo($"localhost/f/contacts/{id}/notes"));
    }

    // ---------------------------------------------------------------- the sample config itself

    [Theory]
    [InlineData("people-directory")]
    [InlineData("people-newest")]
    [InlineData("contacts-unique")]
    public void The_sample_recipe_is_valid(string id)
    {
        var (recipe, snapshot) = Load(id);

        var validation = RecipeValidator.Validate(recipe, snapshot);

        Assert.True(validation.IsValid, string.Join("; ", validation.Issues.Select(i => $"{i.Where}: {i.Message}")));
    }

    [Fact]
    public async Task Each_sample_service_gets_its_own_sign_in()
    {
        var downstream = SampleDownstream();
        await Run("people-directory", downstream: downstream);

        string? Header(string path, string name) =>
            downstream.Requests.Single(r => r.RequestUri!.AbsolutePath == path).Headers.TryGetValues(name, out var v) ? v.Single() : null;
        Assert.Equal("key-e", Header("/e/users", "X-Api-Key"));
        Assert.Equal("key-g", Header("/g/accounts", "X-Api-Key"));
        Assert.StartsWith("Basic ", Header("/f/contacts", "Authorization"));
        Assert.Null(Header("/h/links", "Authorization")); // the internal links service has no sign-in
        Assert.Null(Header("/h/links", "X-Api-Key"));
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Answers like samples/MockServices on localhost:5100.</summary>
    private static FakeDownstream SampleDownstream()
    {
        var fake = new FakeDownstream()
            .OnRaw("localhost/e/users", PeopleSamples.Users, "application/json")
            .OnRaw("localhost/f/contacts", PeopleSamples.Contacts, "application/json")
            .OnRaw("localhost/f/orders", PeopleSamples.Orders, "application/json")
            .OnRaw("localhost/g/accounts", PeopleSamples.Accounts, "application/json")
            .OnRaw("localhost/h/links", PeopleSamples.Links, "application/json");
        foreach (var id in PeopleSamples.ContactsWithNotes)
            fake.OnRaw($"localhost/f/contacts/{id}/notes", PeopleSamples.Notes(id), "application/json");
        return fake;
    }

    /// <summary>Runs a sample recipe from config/recipes. <paramref name="changeCombine"/> edits its "people" combine first.</summary>
    private static Task<CompositionResult> Run(string id, Action<JsonObject>? changeCombine = null, FakeDownstream? downstream = null)
    {
        var (recipe, snapshot) = Load(id, changeCombine);
        downstream ??= SampleDownstream();

        var options = Options.Create(new EngineOptions());
        var secrets = new ConfigurationSecretResolver(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Secrets:service-e-api-key"] = "key-e", ["Secrets:service-f-password"] = "pass-f", ["Secrets:service-g-api-key"] = "key-g"
        }).Build());
        var auth = new DownstreamAuthenticator([new BasicAuthStep(), new ApiKeyStep()], secrets,
            new DownstreamCredentialCache(new MemoryCache(new MemoryCacheOptions()), NullLogger<DownstreamCredentialCache>.Instance), downstream);
        var executor = new CompositionExecutor(new InMemoryConfigStore(snapshot), auth, downstream,
            new MemoryResponseCache(options), options, NullLogger<CompositionExecutor>.Instance);

        return executor.ExecuteAsync(recipe, new InboundRequest(), CallerContext.Anonymous("corr-sample"), ExecutionMode.AdminTest, default);
    }

    /// <summary>The recipe plus the services and sign-ins from the repo's config folder (the files QA uses).</summary>
    private static (Recipe Recipe, ConfigSnapshot Snapshot) Load(string id, Action<JsonObject>? changeCombine = null)
    {
        var config = Path.Combine(RepoRoot(), "config");
        var node = JsonNode.Parse(File.ReadAllText(Path.Combine(config, "recipes", id + ".json")))!.AsObject();
        if (changeCombine is not null) changeCombine(node["combine"]!["people"]!.AsObject());

        var recipe = node.Deserialize<Recipe>(AggregatorJson.Options)!;
        var services = JsonSerializer.Deserialize<List<ApiService>>(File.ReadAllText(Path.Combine(config, "services.json")), AggregatorJson.Options)!;
        var profiles = JsonSerializer.Deserialize<List<AuthProfile>>(File.ReadAllText(Path.Combine(config, "auth-profiles.json")), AggregatorJson.Options)!;
        return (recipe, new ConfigSnapshot(services, profiles, [recipe], "sample"));
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "ApiAggregator.sln"))) return dir.FullName;
        throw new InvalidOperationException("Couldn't find the repository root (ApiAggregator.sln).");
    }

    private static string CombineLine(CompositionResult result) => result.Trace.Single(t => t.Alias == "combine.people").Detail!;

    private static JsonNode Person(CompositionResult result, string id) =>
        result.Body!["people"]!.AsArray().Single(p => p!["id"]?.GetValue<string>() == id)!;

    private static string Sources(JsonNode? person) => string.Join(",", person!["_sources"]!.AsArray().Select(s => s!.GetValue<string>()));

    /// <summary>"id | name | email | phone | plan | accountNo | sources", with "-" for a missing value.</summary>
    private static string Summary(JsonNode? person)
    {
        string Field(string name) => person![name]?.GetValue<string>() ?? "-";
        return string.Join(" | ", Field("id"), Field("name"), Field("email"), Field("phone"), Field("plan"), Field("accountNo"), Sources(person));
    }
}
