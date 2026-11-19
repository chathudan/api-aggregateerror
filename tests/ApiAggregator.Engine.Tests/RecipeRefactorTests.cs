using System.Text.Json;
using System.Text.Json.Nodes;
using ApiAggregator.Engine.Authoring;
using ApiAggregator.Engine.Config;
using ApiAggregator.Engine.Execution;
using ApiAggregator.Engine.Model;
using ApiAggregator.Engine.Validation;
using static ApiAggregator.Engine.Tests.TestData;

namespace ApiAggregator.Engine.Tests;

/// <summary>Renaming steps, step names made from display names, "did you mean", loop options and simple conditions.</summary>
public class RecipeRefactorTests
{
    /// <summary>A recipe that uses step "orders" in every place an expression can be, plus things only spelled like it.</summary>
    private static Recipe UsesOrdersEverywhere() => new()
    {
        Id = "r",
        Route = "contacts",
        RequireAuthentication = false,
        Steps =
        [
            new StepDef { Alias = "orders", DisplayName = "Orders from CRM", Endpoint = "crm/listOrders" },
            new StepDef { Alias = "crm", Endpoint = "crm/listContacts" },
            new StepDef
            {
                Alias = "notes", Endpoint = "crm/getContactNotes",
                ForEach = "{{ orders.body.orders[*].contactId }}",
                When = "{{orders.body.orders | count}} != 0",
                Bind =
                [
                    new BindingDef { From = "item", To = "path.contactId" },
                    new BindingDef { From = "orders.body.total", To = "query.total" },
                    new BindingDef { Template = JsonNode.Parse("""{ "ids": "{{orders.body.orders[*].id}}", "note": "Orders: {{orders.status}}" }"""), To = "body" }
                ]
            }
        ],
        Combine = new(StringComparer.OrdinalIgnoreCase)
        {
            ["people"] = new CombineDef
            {
                Sources = [Source("orders.body.orders", "contactId"), Source("crm.body.contacts", "id", name: "contacts")],
                Owners = new(StringComparer.Ordinal) { ["total"] = "orders", ["name"] = "contacts" }
            },
            ["linked"] = new CombineDef
            {
                Sources = [new CombineSource { From = "crm.body.contacts", Key = "id", Via = new CombineVia { From = "orders.body.links", Match = "a", Use = "b" } }]
            }
        },
        Response = JsonNode.Parse("""
            {
              "orders": "{{orders.body.orders}}",
              "count": "{{orders.body.orders | count}}",
              "unique": "{{crm.body.orders | distinct('orders')}}",
              "people": "{{combine.people}}"
            }
            """)
    };

    private static RenameResult Rename(string to) => RecipeRefactor.RenameStep(UsesOrdersEverywhere(), "orders", to);

    private static string Json(Recipe r) => JsonSerializer.Serialize(r, AggregatorJson.Options);

    // ---------------------------------------------------------------- rename

    [Fact]
    public void Renaming_a_step_rewrites_every_expression_that_starts_with_it()
    {
        var result = Rename("crmOrders");

        Assert.True(result.Renamed, result.Problem);
        var r = result.Recipe!;
        var notes = r.Steps.Single(s => s.Alias == "notes");
        Assert.Equal("crmOrders", r.Steps[0].Alias);
        Assert.Equal("Orders from CRM", r.Steps[0].DisplayName);
        Assert.Equal("{{ crmOrders.body.orders[*].contactId }}", notes.ForEach); // spacing kept
        Assert.Equal("{{crmOrders.body.orders | count}} != 0", notes.When);
        Assert.Equal("crmOrders.body.total", notes.Bind[1].From);
        Assert.Equal("{{crmOrders.body.orders[*].id}}", notes.Bind[2].Template!["ids"]!.GetValue<string>());
        Assert.Equal("Orders: {{crmOrders.status}}", notes.Bind[2].Template!["note"]!.GetValue<string>()); // text around kept
        Assert.Equal("{{crmOrders.body.orders}}", r.Response!["orders"]!.GetValue<string>());
        Assert.Equal("{{crmOrders.body.orders | count}}", r.Response!["count"]!.GetValue<string>());
        Assert.Equal("crmOrders.body.orders", r.Combine["people"].Sources[0].From);
        Assert.Equal("crmOrders.body.links", r.Combine["linked"].Sources[0].Via!.From);
    }

    [Fact]
    public void Fields_and_transform_arguments_spelled_like_the_step_are_left_alone()
    {
        var r = Rename("crmOrders").Recipe!;

        Assert.Equal("{{crm.body.orders | distinct('orders')}}", r.Response!["unique"]!.GetValue<string>());
        Assert.Equal("item", r.Steps.Single(s => s.Alias == "notes").Bind[0].From);
        Assert.Equal("crm.body.contacts", r.Combine["people"].Sources[1].From);
        Assert.Equal("{{combine.people}}", r.Response!["people"]!.GetValue<string>());
    }

    [Fact]
    public void An_owner_that_names_the_step_follows_the_rename_but_a_named_list_does_not()
    {
        var owners = Rename("crmOrders").Recipe!.Combine["people"].Owners;

        Assert.Equal("crmOrders", owners["total"]); // the source had no name of its own, so it was called after the step
        Assert.Equal("contacts", owners["name"]);
    }

    [Fact]
    public void The_count_of_changes_matches_the_number_of_places_that_used_the_step()
    {
        var recipe = UsesOrdersEverywhere();

        var references = RecipeRefactor.References(recipe, "orders");

        // forEach, when, one binding, the body template, two response fields, two combine paths, one owner.
        Assert.Equal(9, references);
        Assert.Equal(references, Rename("crmOrders").Changed);
        Assert.Equal(0, RecipeRefactor.References(Rename("crmOrders").Recipe!, "orders"));
    }

    [Fact]
    public void A_renamed_recipe_is_still_valid_and_runs_in_the_same_order()
    {
        var recipe = CrmRecipe();
        Assert.True(RecipeValidator.Validate(recipe, CrmSnapshot()).IsValid);

        var renamed = RecipeRefactor.RenameStep(recipe, "orders", "crmOrders").Recipe!;

        Assert.True(RecipeValidator.Validate(renamed, CrmSnapshot()).IsValid);
        Assert.Equal("{{crmOrders.body.orders[*].contactId}}", renamed.Steps[2].ForEach);
    }

    [Fact]
    public void Settings_that_have_nothing_to_do_with_the_name_are_kept()
    {
        var recipe = CrmRecipe();
        var before = Json(recipe).Replace("orders.body", "X");

        var after = Json(RecipeRefactor.RenameStep(recipe, "orders", "crmOrders").Recipe!)
            .Replace("crmOrders.body", "X").Replace("\"alias\": \"crmOrders\"", "\"alias\": \"orders\"");

        Assert.Equal(before, after);
    }

    [Fact]
    public void Renaming_to_another_steps_name_is_refused()
    {
        var result = Rename("crm");

        Assert.False(result.Renamed);
        Assert.Equal("'crm' is already the name of another step. Pick a different name.", result.Problem);
    }

    [Theory]
    [InlineData("request")]
    [InlineData("item")]
    [InlineData("index")]
    [InlineData("ctx")]
    [InlineData("combine")]
    public void Renaming_to_a_reserved_word_is_refused(string reserved)
    {
        var result = Rename(reserved);

        Assert.False(result.Renamed);
        Assert.StartsWith($"'{reserved}' is a reserved word (", result.Problem);
    }

    [Theory]
    [InlineData("2orders")]
    [InlineData("orders-crm")]
    [InlineData("")]
    [InlineData("x}}{{request.route.id")] // can't smuggle an expression into a recipe through a step name
    [InlineData("a.body")]
    [InlineData("name\r\nX-Injected: 1")]
    public void Renaming_to_a_name_that_cant_be_used_in_expressions_is_refused(string bad) =>
        Assert.False(Rename(bad).Renamed);

    // ---------------------------------------------------------------- names from display names

    [Theory]
    [InlineData("Orders from CRM", "ordersFromCrm")]
    [InlineData("  get   customer ", "getCustomer")]
    [InlineData("3 latest orders", "s3LatestOrders")]
    [InlineData("Ünïcode & symbols!", "ncodeSymbols")]
    [InlineData("", "step")]
    public void A_display_name_becomes_a_camel_case_step_name(string display, string expected) =>
        Assert.Equal(expected, RecipeRefactor.AliasFor(display, []));

    [Fact]
    public void A_step_name_from_a_display_name_is_unique_and_never_reserved()
    {
        Assert.Equal("ordersFromCrm2", RecipeRefactor.AliasFor("Orders from CRM", ["ordersFromCrm"]));
        Assert.Equal("item2", RecipeRefactor.AliasFor("Item", []));
        Assert.Equal("request2", RecipeRefactor.AliasFor("request", []));
        Assert.True(RecipeRefactor.AliasFor("A very long display name for one step", []).Length <= 24);
    }

    // ---------------------------------------------------------------- did you mean

    private static Recipe ReferringTo(string expression, string alias = "getOrders", string? display = "Orders from CRM") => new()
    {
        Id = "r",
        Route = "contacts",
        RequireAuthentication = false,
        Steps =
        [
            new StepDef { Alias = alias, DisplayName = display, Endpoint = "crm/listOrders" },
            new StepDef { Alias = "notes", Endpoint = "crm/getContactNotes", ForEach = $"{{{{{expression}}}}}", Bind = [new BindingDef { From = "item", To = "path.contactId" }] }
        ]
    };

    private static IEnumerable<string> Messages(Recipe recipe) => RecipeValidator.Validate(recipe, CrmSnapshot()).Issues.Select(i => i.Message);

    [Fact]
    public void A_step_called_by_its_display_name_suggests_its_real_name() =>
        Assert.Contains("'ordersFromCrm' isn't a step here. Did you mean 'getOrders' (Orders from CRM)?",
            Messages(ReferringTo("ordersFromCrm.body.orders[*].contactId")));

    [Fact]
    public void A_misspelt_step_name_suggests_the_closest_one() =>
        Assert.Contains("'getOrder' isn't a step here. Did you mean 'getOrders' (Orders from CRM)?",
            Messages(ReferringTo("getOrder.body.orders[*].contactId")));

    [Fact]
    public void A_step_without_a_display_name_is_suggested_by_its_name_only() =>
        Assert.Contains("'getOrdrs' isn't a step here. Did you mean 'getOrders'?",
            Messages(ReferringTo("getOrdrs.body.orders[*].contactId", display: null)));

    [Fact]
    public void Nothing_close_says_to_pick_the_value_again() =>
        Assert.Contains("'billing' isn't a step here. Pick the value again from the list.",
            Messages(ReferringTo("billing.body.items[*]")));

    [Fact]
    public void Combined_lists_and_mappings_suggest_the_closest_step_too()
    {
        var recipe = new Recipe
        {
            Id = "r", Route = "contacts", RequireAuthentication = false,
            Steps = [new StepDef { Alias = "getOrders", DisplayName = "Orders from CRM", Endpoint = "crm/listOrders" }],
            Combine = new(StringComparer.OrdinalIgnoreCase)
            {
                ["people"] = new CombineDef
                {
                    Sources = [new CombineSource { From = "ordersFromCrm.body.orders", Key = "id", Via = new CombineVia { From = "getOrder.body.links", Match = "a", Use = "b" } }]
                }
            }
        };

        var messages = Messages(recipe).ToList();

        Assert.Contains("Source 1: 'ordersFromCrm' isn't a step here. Did you mean 'getOrders' (Orders from CRM)?", messages);
        Assert.Contains("Source 1 (mapping): 'getOrder' isn't a step here. Did you mean 'getOrders' (Orders from CRM)?", messages);
    }

    // ---------------------------------------------------------------- loop options

    [Fact]
    public void Loop_options_list_each_list_and_each_field_inside_it_in_plain_words()
    {
        var options = new SourceCatalog().LoopOptions(CrmRecipe(), "notes", CrmSnapshot()).ToDictionary(o => o.Label, o => o.Expression);

        Assert.Equal("orders.body.orders[*]", options["Orders from CRM → each order"]);
        Assert.Equal("orders.body.orders[*].contactId", options["Orders from CRM → each order's contactId"]);
        Assert.Equal("crm.body.contacts[*].id", options["Contacts from CRM → each contact's id"]);
        Assert.DoesNotContain(options.Values, e => e.StartsWith("notes.")); // not the step itself, nor later ones
    }

    [Fact]
    public void A_step_looping_over_whole_items_can_pick_the_current_items_fields()
    {
        var recipe = CrmRecipe(notesForEach: "{{orders.body.orders[*]}}", notesFrom: "item.contactId");

        var options = new SourceCatalog().For(recipe, "notes", CrmSnapshot()).Where(o => o.Group == "For each").Select(o => (o.Expression, o.Label));

        Assert.Contains(("item", "Current item (for each)"), options);
        Assert.Contains(("item.contactId", "Current item → contactId"), options);
    }

    [Fact]
    public void Current_item_is_offered_even_before_a_loop_is_chosen() =>
        Assert.Contains(new SourceCatalog().For(CrmRecipe(notesForEach: null), "notes", CrmSnapshot()), o => o.Expression == "item");

    [Fact]
    public void A_for_each_steps_values_are_taken_from_every_call()
    {
        var options = new SourceCatalog().For(CrmRecipe(), null, CrmSnapshot()).Where(o => o.Group == "Notes").Select(o => (o.Expression, o.Label)).ToList();

        Assert.Contains(("notes[*].body", "Notes → whole response (from every call)"), options);
        Assert.Contains(("notes[*].status", "Notes → status code (from every call)"), options);
    }

    // ---------------------------------------------------------------- simple conditions

    [Theory]
    [InlineData("{{a.body.isPremium}}", "a.body.isPremium", null, null)]
    [InlineData("{{ a.body.status }} == active", "a.body.status", "==", "active")]
    [InlineData("{{a.body.status}} != 'closed'", "a.body.status", "!=", "closed")]
    [InlineData("{{a.body.status}} ==", "a.body.status", "==", "")]
    public void Simple_conditions_are_split_into_their_parts(string when, string expression, string? op, string? value)
    {
        Assert.True(Conditions.TryParseSimple(when, out var e, out var o, out var v));
        Assert.Equal((expression, op, value), (e, o, v));
    }

    [Theory]
    [InlineData("Customer {{a.body.status}}")]
    [InlineData("{{a.body.status}} == {{b.body.status}}")]
    [InlineData("true")]
    [InlineData("")]
    [InlineData(null)]
    public void Other_conditions_are_not_simple(string? when) =>
        Assert.False(Conditions.TryParseSimple(when, out _, out _, out _));
}
