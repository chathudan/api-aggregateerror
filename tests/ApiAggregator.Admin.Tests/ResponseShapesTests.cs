using System.Text.Json;
using ApiAggregator.Admin.Services;
using ApiAggregator.Engine.Config;
using ApiAggregator.Engine.Model;

namespace ApiAggregator.Admin.Tests;

/// <summary>"How many" and "Without repeats" on response fields: saved values open in the pickers, and nothing else is rewritten.</summary>
public class ResponseShapesTests
{
    private static readonly string Root = RepoRoot();

    private static ConfigSnapshot Samples() => new(
        JsonSerializer.Deserialize<List<ApiService>>(File.ReadAllText(Path.Combine(Root, "samples/config/services.json")), AggregatorJson.Options)!,
        [], [], "test");

    private static Recipe ContactsUnique() =>
        JsonSerializer.Deserialize<Recipe>(File.ReadAllText(Path.Combine(Root, "samples/config/recipes/contacts-unique.json")), AggregatorJson.Options)!;

    [Theory]
    [InlineData("contactRows", "crm.body.contacts", ResponseShape.Count, null)]
    [InlineData("uniqueContacts", "crm.body.contacts", ResponseShape.DistinctCount, "id")]
    [InlineData("contacts", "crm.body.contacts", ResponseShape.Distinct, "id")]
    [InlineData("orderCount", "orders.body.orders", ResponseShape.Count, null)]
    [InlineData("notesCalls", "notes", ResponseShape.Count, null)]
    [InlineData("notes", "notes[*].body", ResponseShape.All, null)]
    public void Every_response_field_of_contacts_unique_opens_in_the_pickers(string field, string value, ResponseShape shape, string? by)
    {
        var form = RecipeForm.FromRecipe(ContactsUnique());

        var shaped = ResponseShapes.Split(form.Response.Single(f => f.Name == field).Expression);

        Assert.Equal(new ShapedValue(value, shape, by), shaped);
    }

    [Theory]
    [InlineData("crm.body.contacts", ResponseShape.All, null, "crm.body.contacts")]
    [InlineData("crm.body.contacts", ResponseShape.Count, null, "crm.body.contacts | count")]
    [InlineData("crm.body.contacts", ResponseShape.Distinct, "id", "crm.body.contacts | distinct('id')")]
    [InlineData("crm.body.contacts", ResponseShape.DistinctCount, "id", "crm.body.contacts | distinct('id') | count")]
    public void A_picked_shape_is_saved_as_the_engines_transforms(string value, ResponseShape shape, string? by, string expected) =>
        Assert.Equal(expected, ResponseShapes.Compose(new ShapedValue(value, shape, by)));

    [Theory]
    [InlineData("crm.body.contacts | join(',')")]          // another transform
    [InlineData("crm.body.contacts|count")]                // other spacing: kept exactly as written
    [InlineData("crm.body.contacts | count | count")]
    [InlineData("crm.body.contacts | distinct")]           // without a field
    public void Other_saved_values_are_left_as_written(string expression) => Assert.Null(ResponseShapes.Split(expression));

    [Theory]
    [InlineData("id')|join('x")]
    [InlineData("na me")]
    [InlineData("")]
    public void A_field_that_could_break_out_of_the_quotes_is_never_written(string by)
    {
        Assert.False(ResponseShapes.IsPlainField(by));
        Assert.Equal("crm.body.contacts", ResponseShapes.Compose(new ShapedValue("crm.body.contacts", ResponseShape.Distinct, by)));
    }

    [Fact]
    public void Shapes_are_offered_for_lists_only_with_the_fields_of_one_item()
    {
        var recipe = ContactsUnique();
        var config = Samples();

        Assert.True(ResponseShapes.IsList(recipe, "crm.body.contacts", config));
        Assert.True(ResponseShapes.IsList(recipe, "notes[*].body", config)); // one answer per call
        Assert.False(ResponseShapes.IsList(recipe, "crm.status", config));
        Assert.Contains("id", ResponseShapes.ItemFields(recipe, "crm.body.contacts", config));
        Assert.Contains("notes", ResponseShapes.ItemFields(recipe, "notes[*].body", config));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CLAUDE.md"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Couldn't find the repository root (the folder with CLAUDE.md).");
    }
}
