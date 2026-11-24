using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using ApiAggregator.Admin.Application;
using ApiAggregator.Engine.Config;
using ApiAggregator.Engine.Model;
using Microsoft.Extensions.DependencyInjection;

namespace ApiAggregator.Admin.Tests;

/// <summary>Endpoint JSON in and out, checked without saving, and what a step can use (ADR 0019).</summary>
public sealed class AuthoringContractTests(AdminApp app) : IClassFixture<AdminApp>
{
    private static readonly AdminActor Author = new("author-1", "Ann Author", IsAuthor: true, IsPublisher: false);
    private static readonly AdminActor Nobody = new("nobody", null, IsAuthor: false, IsPublisher: false);

    private AdminService Service => app.Services.GetRequiredService<AdminService>();
    private IConfigStore Store => app.Services.GetRequiredService<IConfigStore>();

    private static Recipe People(string id, string route = "people/all", string endpoint = "people/listPeople") => new()
    {
        Id = id, Name = "Everyone", Route = route, RequireAuthentication = false,
        Steps = [new StepDef { Alias = "people", Endpoint = endpoint }],
        Response = JsonNode.Parse("""{ "people": "{{people.body.items}}" }""")
    };

    [Fact]
    public async Task Endpoint_json_is_checked_without_being_saved()
    {
        var result = await Service.ValidateRecipeAsync(Author, People("not-saved", endpoint: "people/noSuchEndpoint"));

        var issue = Assert.Single(result.Value!.Issues);
        Assert.Equal("steps.people", issue.Where);
        Assert.Contains("noSuchEndpoint", issue.Message);
        Assert.Null(await Store.GetDraftAsync("not-saved"));
    }

    [Fact]
    public async Task Imported_endpoint_json_is_a_draft_and_never_live()
    {
        var saved = await Service.ImportDraftAsync(Author, People("imported-people", "people/imported"));

        Assert.True(saved.Ok, saved.Failure?.Message);
        Assert.Equal(RecipeStatus.Draft, saved.Value!.Status);
        Assert.NotNull(await Store.GetDraftAsync("imported-people"));
        Assert.Null((await Store.GetSnapshotAsync()).GetRecipe("imported-people"));

        var exported = (await Service.ExportRecipeAsync(Author, "imported-people")).Value!;
        Assert.Equal("draft", exported.Source);
        Assert.Null(exported.LiveVersion);
        Assert.Equal("people/imported", exported.Recipe.Route);
    }

    [Fact]
    public async Task Importing_an_endpoint_on_an_address_another_one_answers_is_refused()
    {
        await Service.ImportDraftAsync(Author, People("first-on-address", "people/{id}/card"));

        var second = await Service.ImportDraftAsync(Author, People("second-on-address", "people/{personId}/card"));

        Assert.Equal(FailureKind.Conflict, second.Failure?.Kind);
        Assert.Equal("The endpoint 'first-on-address' already answers GET /bff/people/{personId}/card. Pick another route.", second.Failure?.Message);
    }

    [Theory]
    [InlineData("Has Spaces")]
    [InlineData("../escape")]
    [InlineData("")]
    public async Task An_id_that_is_not_a_lowercase_slug_is_refused(string id)
    {
        var result = await Service.ImportDraftAsync(Author, People(id, "people/odd"));

        Assert.Equal("Give the endpoint an id of lowercase letters, digits and dashes (at most 64), e.g. customer-overview.", result.Failure?.Message);
    }

    [Fact]
    public async Task What_a_step_can_use_comes_from_an_unsaved_endpoint()
    {
        var recipe = new Recipe
        {
            Id = "sources", Route = "people/{id}", RequireAuthentication = false,
            Inputs = new(StringComparer.OrdinalIgnoreCase) { ["route.id"] = new InputDef { Required = true } },
            Steps = [new StepDef { Alias = "people", Endpoint = "people/listPeople" }, new StepDef { Alias = "billing", Endpoint = "billing/listAccounts" }]
        };

        var view = (await Service.GetSourcesForAsync(Author, new SourcesRequest(recipe, "billing"))).Value!;

        Assert.Contains(view.Fields, f => f.Expression == "request.route.id");
        Assert.Contains(view.Fields, f => f.Expression == "people.body");
        Assert.DoesNotContain(view.Fields, f => f.Expression.StartsWith("billing.", StringComparison.Ordinal));

        var unknown = await Service.GetSourcesForAsync(Author, new SourcesRequest(recipe, "nope"));
        Assert.Equal("There is no step 'nope' in this endpoint. Use one of: people, billing.", unknown.Failure?.Message);
    }

    [Fact]
    public async Task Each_authoring_contract_action_needs_the_author_role()
    {
        Assert.Equal(FailureKind.Forbidden, Service.GetRecipeSchema(Nobody).Failure?.Kind);
        Assert.Equal(FailureKind.Forbidden, Service.GetPathLanguage(Nobody).Failure?.Kind);
        Assert.Equal(FailureKind.Forbidden, (await Service.ValidateRecipeAsync(Nobody, People("x"))).Failure?.Kind);
        Assert.Equal(FailureKind.Forbidden, (await Service.ImportDraftAsync(Nobody, People("x"))).Failure?.Kind);
        Assert.Equal(FailureKind.Forbidden, (await Service.ExportRecipeAsync(Nobody, "x")).Failure?.Kind);
        Assert.Equal(FailureKind.Forbidden, (await Service.GetSourcesForAsync(Nobody, new SourcesRequest(People("x"), null))).Failure?.Kind);
    }

    [Fact]
    public async Task The_schema_and_the_value_language_are_on_the_admin_api_and_llms_txt_is_open()
    {
        var client = app.ClientWithToken("aggregator.author");

        var schema = await client.GetFromJsonAsync<JsonNode>("/admin/schema/recipe");
        Assert.Equal("Aggregated endpoint (recipe)", schema!["title"]!.GetValue<string>());
        var language = await client.GetFromJsonAsync<JsonNode>("/admin/path-language");
        Assert.Contains(language!["transforms"]!.AsArray(), t => t!["name"]!.GetValue<string>() == "join");

        var llms = await app.CreateClient().GetAsync("/llms.txt");
        Assert.Equal(HttpStatusCode.OK, llms.StatusCode);
        Assert.StartsWith("# API Aggregator", await llms.Content.ReadAsStringAsync());
    }
}
