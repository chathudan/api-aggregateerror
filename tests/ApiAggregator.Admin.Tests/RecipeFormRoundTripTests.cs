using System.Text.Json;
using ApiAggregator.Admin.Services;
using ApiAggregator.Engine.Config;
using ApiAggregator.Engine.Model;

namespace ApiAggregator.Admin.Tests;

/// <summary>
/// Opening a recipe in the wizard and saving it again must not change anything the wizard doesn't show
/// (concurrency, timeouts, defaults, input limits, body templates...). Every recipe in samples/config and config/ is checked.
/// </summary>
public class RecipeFormRoundTripTests
{
    public static TheoryData<string> RecipeFiles()
    {
        var data = new TheoryData<string>();
        var root = RepoRoot();
        foreach (var folder in new[] { "samples/config/recipes", "samples/config/drafts", "config/recipes", "config/drafts" })
        {
            var path = Path.Combine(root, folder);
            if (!Directory.Exists(path)) continue;
            foreach (var file in Directory.GetFiles(path, "*.json").Order(StringComparer.Ordinal))
                data.Add(Path.GetRelativePath(root, file));
        }
        return data;
    }

    [Fact]
    public void There_are_recipes_to_check() => Assert.NotEmpty(RecipeFiles());

    [Theory]
    [MemberData(nameof(RecipeFiles))]
    public void Loading_a_recipe_into_the_wizard_and_saving_it_gives_the_same_json(string file)
    {
        var recipe = JsonSerializer.Deserialize<Recipe>(File.ReadAllText(Path.Combine(RepoRoot(), file)), AggregatorJson.Options)!;
        var original = JsonSerializer.Serialize(recipe, AggregatorJson.Options);

        var roundTripped = JsonSerializer.Serialize(RecipeForm.FromRecipe(recipe).ToRecipe(), AggregatorJson.Options);

        Assert.Equal(original, roundTripped);
    }

    [Fact]
    public void Settings_the_wizard_does_not_show_survive_a_round_trip()
    {
        var json = """
            {
              "id": "everything", "route": "everything/{id}", "version": 7, "status": "published",
              "cache": { "enabled": true, "ttlSeconds": 60, "perCaller": false },
              "inputs": {
                "route.id": { "type": "string", "required": true, "pattern": "^C-[0-9]+$", "maxLength": 12 },
                "query.size": { "type": "int", "min": 1, "max": 50, "default": "10" },
                "query.kind": { "type": "string", "enum": ["a", "b"] }
              },
              "steps": [
                { "alias": "a", "endpoint": "s/a",
                  "bind": [ { "from": "request.route.id", "to": "path.id", "default": "C-1" },
                            { "template": { "id": "{{request.route.id}}", "size": 5, "tags": ["x"] }, "to": "body" },
                            { "template": 3, "to": "query.n" } ],
                  "timeoutMs": 1500, "maxConcurrency": 2, "cache": { "enabled": true, "ttlSeconds": 5, "perCaller": false } },
                { "alias": "b", "endpoint": "s/b", "forEach": "{{a.body.items[*].id}}", "maxConcurrency": 7, "distinct": false,
                  "bind": [ { "from": "item", "to": "path.id" } ] }
              ],
              "response": { "who": "{{a.body.name}}", "label": "Customer {{a.body.name}}", "fixed": 1, "nested": { "x": "{{b[*].body}}" } }
            }
            """;
        var recipe = JsonSerializer.Deserialize<Recipe>(json, AggregatorJson.Options)!;
        var original = JsonSerializer.Serialize(recipe, AggregatorJson.Options);

        Assert.Equal(original, JsonSerializer.Serialize(RecipeForm.FromRecipe(recipe).ToRecipe(), AggregatorJson.Options));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CLAUDE.md"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Couldn't find the repository root (the folder with CLAUDE.md).");
    }
}
