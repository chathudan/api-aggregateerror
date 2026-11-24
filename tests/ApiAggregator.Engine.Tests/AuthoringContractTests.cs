using System.Text.Json.Nodes;
using ApiAggregator.Engine.Authoring;
using ApiAggregator.Engine.Expressions;

namespace ApiAggregator.Engine.Tests;

/// <summary>The authoring contract (ADR 0019): the recipe schema and the value language, as people and AI agents read them.</summary>
public sealed class AuthoringContractTests
{
    [Fact]
    public void Every_transform_is_described_and_nothing_else_is()
    {
        Assert.Equal(Transforms.Names.Order(), Transforms.Described.Select(t => t.Name).Order());
        Assert.All(Transforms.Described, t => Assert.False(string.IsNullOrWhiteSpace(t.Description)));
        Assert.Same(Transforms.Described, PathLanguage.Described.Transforms);
    }

    [Fact]
    public void The_schema_in_the_docs_is_the_one_the_app_serves()
    {
        var file = Path.Combine(RepoRoot(), "docs", "schemas", "recipe.schema.json");
        var inDocs = File.ReadAllText(file).ReplaceLineEndings().Trim();

        Assert.True(inDocs == RecipeSchema.Json.ReplaceLineEndings().Trim(),
            "docs/schemas/recipe.schema.json is out of date. Run: dotnet run --project tools/ApiAggregator.Cli -- schema > docs/schemas/recipe.schema.json");
    }

    [Fact]
    public void The_schema_describes_what_an_endpoint_needs_in_plain_words()
    {
        var schema = RecipeSchema.Get();

        Assert.Equal(["id", "route"], schema["required"]!.AsArray().Select(n => n!.GetValue<string>()).Order());
        Assert.Contains("draft", schema["description"]!.GetValue<string>());
        var step = schema["properties"]!["steps"]!["items"]!;
        Assert.Contains("<serviceId>/<endpointId>", step["properties"]!["endpoint"]!["description"]!.GetValue<string>());
        Assert.Equal(["alias", "endpoint"], step["required"]!.AsArray().Select(n => n!.GetValue<string>()).Order());
        Assert.NotNull(schema["properties"]!["response"]!["description"]);
    }

    [Fact]
    public void Each_copy_of_the_schema_is_separate()
    {
        var first = RecipeSchema.Get();
        first["title"] = "changed";

        Assert.Equal("Aggregated endpoint (recipe)", RecipeSchema.Get()["title"]!.GetValue<string>());
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "ApiAggregator.sln"))) return dir.FullName;
        throw new InvalidOperationException("Couldn't find the repository root.");
    }
}
