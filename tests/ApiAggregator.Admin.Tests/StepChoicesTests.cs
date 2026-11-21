using System.Text.Json;
using ApiAggregator.Admin.Services;
using ApiAggregator.Engine.Authoring;
using ApiAggregator.Engine.Config;
using ApiAggregator.Engine.Model;
using ApiAggregator.Engine.Validation;

namespace ApiAggregator.Admin.Tests;

/// <summary>
/// Saved recipes open in the wizard's pickers ("Run this step", "Only run when"), or in Custom with their text kept;
/// renaming a step in the form gives exactly the engine's rename. Uses the real sample services and recipes.
/// </summary>
public class StepChoicesTests
{
    private static readonly string Root = RepoRoot();

    private static ConfigSnapshot Samples() => new(
        JsonSerializer.Deserialize<List<ApiService>>(File.ReadAllText(Path.Combine(Root, "samples/config/services.json")), AggregatorJson.Options)!,
        [], [], "test");

    private static Recipe Sample(string file) =>
        JsonSerializer.Deserialize<Recipe>(File.ReadAllText(Path.Combine(Root, "samples/config", file)), AggregatorJson.Options)!;

    /// <summary>The form as the wizard opens it: loaded, then both pickers set from the service list.</summary>
    private static RecipeForm Open(Recipe recipe)
    {
        var form = RecipeForm.FromRecipe(recipe);
        var snapshot = Samples();
        StepChoices.LoadAll(form,
            s => new SourceCatalog().LoopOptions(form.ToRecipe(), s.Alias, snapshot),
            s => new SourceCatalog().For(form.ToRecipe(), s.Alias, snapshot));
        return form;
    }

    /// <summary>
    /// The case from the bug report: "Orders from CRM" kept the alias getOrders, and the loop says ordersFromCrm.
    /// Written here, not read from samples/config/drafts: dev-run.sh edits those drafts as people use the app.
    /// </summary>
    private static Recipe RenamedStepNotFollowed() => new()
    {
        Id = "unique-customers-2", Route = "customer/unique2", RequireAuthentication = false,
        Steps =
        [
            new StepDef { Alias = "listContacts", DisplayName = "Contacts from CRM", Endpoint = "service-f-crm/listContacts" },
            new StepDef { Alias = "getOrders", DisplayName = "Orders from CRM", Endpoint = "service-f-crm/listOrders" },
            new StepDef
            {
                Alias = "notesForOneContact", DisplayName = "Notes for each contact with an order", Endpoint = "service-f-crm/getContactNotes",
                ForEach = "{{ordersFromCrm.body.orders[*].contactId}}",
                Bind = [new BindingDef { From = "item", To = "path.contactId" }]
            }
        ]
    };

    private static StepForm Step(RecipeForm form, string alias) => form.Steps.Single(s => s.Alias == alias);

    // ---------------------------------------------------------------- run this step

    [Fact]
    public void A_loop_over_a_listed_field_opens_as_that_list()
    {
        var notes = Step(Open(Sample("recipes/contacts-unique.json")), "notes");

        Assert.Equal(LoopMode.List, notes.LoopMode);
        var label = new SourceCatalog().LoopOptions(Sample("recipes/contacts-unique.json"), "notes", Samples())
            .Single(o => o.Expression == StepChoices.LoopExpression(notes.ForEach)).Label;
        Assert.Equal("Orders from CRM → each order's contactId", label);
    }

    [Fact]
    public void A_loop_the_list_does_not_offer_opens_as_custom_with_its_text_kept()
    {
        var lines = Step(Open(Sample("recipes/customer-overview.json")), "lines");

        Assert.Equal(LoopMode.Custom, lines.LoopMode); // only the first three orders: [0:3]
        Assert.Equal("{{b.body.items[0:3].id}}", lines.ForEach);
    }

    [Fact]
    public void A_step_without_a_loop_opens_as_once() =>
        Assert.Equal(LoopMode.Once, Step(Open(Sample("recipes/contacts-unique.json")), "crm").LoopMode);

    [Fact]
    public void A_loop_over_a_step_that_doesnt_exist_opens_as_custom() =>
        Assert.Equal(LoopMode.Custom, Step(Open(RenamedStepNotFollowed()), "notesForOneContact").LoopMode);

    [Theory]
    [InlineData(null, LoopMode.Once)]
    [InlineData("   ", LoopMode.Once)]
    [InlineData("{{ orders.body.orders[*].contactId }}", LoopMode.List)] // spacing inside the braces doesn't matter
    [InlineData("{{orders.body.orders[*].contactId | distinct}}", LoopMode.Custom)]
    [InlineData("orders.body.orders[*].contactId", LoopMode.Custom)]
    public void Loops_are_matched_on_the_value_inside_the_braces(string? forEach, LoopMode expected)
    {
        var options = new[] { new LoopOption("orders.body.orders[*].contactId", "Orders → each order's contactId", "Orders", null) };
        Assert.Equal(expected, StepChoices.LoadLoop(forEach, options));
    }

    // ---------------------------------------------------------------- only run when

    private static StepForm Condition(string? when, string field = "a.body.isPremium")
    {
        var step = new StepForm { Alias = "s", When = when };
        StepChoices.LoadCondition(step, [new SourceOption(field, "Get customer → isPremium", "Get customer")]);
        return step;
    }

    [Fact]
    public void A_saved_equality_opens_as_is()
    {
        var lines = Step(Open(Sample("recipes/customer-overview.json")), "lines");

        Assert.Equal((ConditionMode.Picked, "a.body.isPremium", ConditionOp.Is, "true"),
            (lines.ConditionMode, lines.ConditionField, lines.ConditionOp, lines.ConditionValue));
        Assert.True(lines.ShowAdvanced); // so the loaded condition is visible straight away
    }

    [Fact]
    public void A_saved_inequality_opens_as_is_not()
    {
        var step = Condition("{{a.body.isPremium}} != 'false'");
        Assert.Equal((ConditionMode.Picked, ConditionOp.IsNot, "false"), (step.ConditionMode, step.ConditionOp, step.ConditionValue));
    }

    [Fact]
    public void A_saved_truthy_check_opens_as_has_a_value()
    {
        var step = Condition("{{a.body.isPremium}}");
        Assert.Equal((ConditionMode.Picked, ConditionOp.HasValue), (step.ConditionMode, step.ConditionOp));
    }

    [Theory]
    [InlineData("{{a.body.isPremium}} == {{b.body.flag}}")] // two values compared
    [InlineData("Premium: {{a.body.isPremium}}")]          // text around the value
    [InlineData("{{x.body.unknown}} == 1")]                // a field that isn't in the list
    public void Other_conditions_open_as_custom_with_their_text_kept(string when)
    {
        var step = Condition(when);
        Assert.Equal(ConditionMode.Custom, step.ConditionMode);
        Assert.Equal(when, step.When);
    }

    [Fact]
    public void A_step_whose_cache_is_shared_by_every_caller_opens_with_advanced_shown()
    {
        var recipe = new Recipe
        {
            Id = "r", Route = "r",
            Steps =
            [
                new StepDef { Alias = "shared", Endpoint = "service-a/getCustomer", Cache = new CacheSettings { Enabled = true, PerCaller = false } },
                new StepDef { Alias = "own", Endpoint = "service-a/getCustomer", Cache = new CacheSettings { Enabled = true } }
            ]
        };

        var form = Open(recipe);

        Assert.True(Step(form, "shared").ShowAdvanced); // so its "shared by every caller" warning can't be missed
        Assert.False(Step(form, "own").ShowAdvanced);
    }

    [Fact]
    public void No_condition_opens_as_always_run() => Assert.Equal(ConditionMode.None, Condition(null).ConditionMode);

    [Theory]
    [InlineData(ConditionOp.Is, "active", "{{a.body.status}} == active")]
    [InlineData(ConditionOp.IsNot, " closed ", "{{a.body.status}} != closed")]
    [InlineData(ConditionOp.HasValue, "ignored", "{{a.body.status}}")]
    public void A_picked_condition_is_saved_in_the_engines_syntax(ConditionOp op, string value, string expected)
    {
        var step = new StepForm { ConditionMode = ConditionMode.Picked, ConditionField = "a.body.status", ConditionOp = op, ConditionValue = value };
        Assert.Equal(expected, StepChoices.ToWhen(step));
    }

    [Fact]
    public void A_picked_value_with_braces_is_refused_in_plain_words()
    {
        var step = new StepForm { ConditionMode = ConditionMode.Picked, ConditionField = "a.body.status", ConditionValue = "{{b.body.status}}" };
        Assert.Equal("Type a plain value here. To compare with another value, choose Custom condition…", StepChoices.ConditionProblem(step));
    }

    // ---------------------------------------------------------------- renaming in the form

    [Theory]
    [InlineData("recipes/contacts-unique.json", "orders", "crmOrders")]
    [InlineData("recipes/customer-overview.json", "a", "customer")]
    [InlineData("recipes/customer-overview.json", "b", "orderList")]
    [InlineData("recipes/people-directory.json", "crm", "contacts")]
    [InlineData("recipes/people-newest.json", "billing", "invoices")]
    [InlineData("recipes/customer-summary.json", "loyalty", "points")]
    public void Renaming_in_the_form_gives_the_engines_rename(string file, string oldAlias, string newAlias)
    {
        var recipe = Sample(file);
        Assert.Contains(recipe.Steps, s => s.Alias == oldAlias);
        var expected = RecipeRefactor.RenameStep(recipe, oldAlias, newAlias).Recipe!;

        var form = Open(recipe);
        Assert.Null(form.RenameStep(oldAlias, newAlias));

        Assert.Equal(JsonSerializer.Serialize(expected, AggregatorJson.Options), JsonSerializer.Serialize(form.ToRecipe(), AggregatorJson.Options));
    }

    [Fact]
    public void Renaming_keeps_the_loop_picker_on_the_same_list()
    {
        var form = Open(Sample("recipes/contacts-unique.json"));

        form.RenameStep("orders", "crmOrders");

        var notes = Step(form, "notes");
        Assert.Equal(LoopMode.List, notes.LoopMode);
        Assert.Equal(LoopMode.List, StepChoices.LoadLoop(notes.ForEach, new SourceCatalog().LoopOptions(form.ToRecipe(), "notes", Samples())));
    }

    [Fact]
    public void Renaming_to_a_name_another_step_has_is_refused_and_changes_nothing()
    {
        var form = Open(Sample("recipes/contacts-unique.json"));
        var before = JsonSerializer.Serialize(form.ToRecipe(), AggregatorJson.Options);

        Assert.Equal("'crm' is already the name of another step. Pick a different name.", form.RenameStep("orders", "crm"));
        Assert.Equal(before, JsonSerializer.Serialize(form.ToRecipe(), AggregatorJson.Options));
    }

    [Fact]
    public void A_loop_over_an_old_step_name_says_which_step_was_meant() =>
        Assert.Contains("'ordersFromCrm' isn't a step here. Did you mean 'getOrders' (Orders from CRM)?",
            RecipeValidator.Validate(RenamedStepNotFollowed(), Samples()).Issues.Select(i => i.Message));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CLAUDE.md"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Couldn't find the repository root (the folder with CLAUDE.md).");
    }
}
