using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ApiAggregator.Engine.Config;
using ApiAggregator.Engine.Model;

namespace ApiAggregator.Admin.Tests;

/// <summary>Save a draft, test it, publish it: what goes live must be exactly what was tested.</summary>
public class PublishRoundTripTests(AdminApp app) : IClassFixture<AdminApp>
{
    private const string Id = "people-with-billing";

    private const string DraftJson = """
        {
          "id": "people-with-billing",
          "name": "People with billing",
          "method": "GET",
          "route": "people-with-billing",
          "requireAuthentication": true,
          "requiredScope": "people.read",
          "steps": [
            { "alias": "crm", "endpoint": "people/listPeople" },
            { "alias": "billing", "endpoint": "billing/listAccounts" }
          ],
          "combine": {
            "people": {
              "sources": [
                { "from": "crm.body.items", "key": "id", "name": "crm" },
                { "from": "billing.body.items", "key": "id", "name": "billing" }
              ],
              "normalize": [ "trim", "lower" ],
              "owners": { "name": "crm" },
              "trackSources": true,
              "sortBy": "-id",
              "maxRecords": 50
            }
          },
          "response": { "people": "{{combine.people}}" }
        }
        """;

    [Fact]
    public async Task A_published_recipe_keeps_its_combine_block()
    {
        var client = app.ClientWithToken("aggregator.author aggregator.publisher");

        // 1. Save the draft.
        var save = await client.PutAsync($"/admin/recipes/{Id}", new StringContent(DraftJson, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);

        // 2. Test it: the combined list is built from both services.
        var test = await client.PostAsync($"/admin/recipes/{Id}/test", new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, test.StatusCode);
        var testResult = JsonNode.Parse(await test.Content.ReadAsStringAsync())!;
        Assert.True(testResult["passed"]!.GetValue<bool>(), testResult.ToJsonString());
        var people = testResult["body"]!["people"]!.AsArray();
        Assert.Equal(["U-2", "U-1"], people.Select(p => p!["id"]!.GetValue<string>()));   // sortBy "-id"
        Assert.Equal("Bo", people[0]!["name"]!.GetValue<string>());                         // owners: crm wins "name"
        Assert.Equal(10, people[1]!["balance"]!.GetValue<int>());                            // merged from billing

        // 3. Publish.
        var publish = await client.PostAsync($"/admin/recipes/{Id}/publish", null);
        Assert.Equal(HttpStatusCode.OK, publish.StatusCode);

        // 4. What the gateway will read: the published file and the history copy both keep the combine block.
        var expected = Deserialize(DraftJson).Combine["people"];
        var live = await File.ReadAllTextAsync(Path.Combine(app.ConfigDir, "recipes", $"{Id}.json"));
        var history = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(app.ConfigDir, "history", $"{Id}.v1.json")))!["recipe"]!.ToJsonString();
        foreach (var json in new[] { live, history })
        {
            var published = Deserialize(json);
            Assert.Equal(RecipeStatus.Published, published.Status);
            var combine = Assert.Single(published.Combine);
            Assert.Equal("people", combine.Key);
            Assert.Equal(JsonSerializer.Serialize(expected, AggregatorJson.Options), JsonSerializer.Serialize(combine.Value, AggregatorJson.Options));
        }
    }

    private static Recipe Deserialize(string json) => JsonSerializer.Deserialize<Recipe>(json, AggregatorJson.Options)!;
}
