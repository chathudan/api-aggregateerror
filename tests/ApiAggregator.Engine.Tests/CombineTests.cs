using System.Text.Json.Nodes;
using ApiAggregator.Engine.Auth;
using ApiAggregator.Engine.Authoring;
using ApiAggregator.Engine.Config;
using ApiAggregator.Engine.Execution;
using ApiAggregator.Engine.Model;
using ApiAggregator.Engine.Validation;
using Microsoft.Extensions.Logging;
using static ApiAggregator.Engine.Tests.TestData;

namespace ApiAggregator.Engine.Tests;

/// <summary>
/// De-duplicated fan-out and "combine" (merging records from several steps by key).
/// Step "b" plays the CRM and step "c" plays billing: each returns { "items": [...] }.
/// </summary>
public class CombineTests
{
    private static InboundRequest Customer(string id) => new() { Route = new Dictionary<string, string?> { ["id"] = id } };

    private static JsonNode Items(string json) => JsonNode.Parse($$"""{ "items": {{json}} }""")!;

    private static FakeDownstream Lists(string crm, string billing) => new FakeDownstream()
        .On("b.test/api/orders", Items(crm))
        .On("c.test/api/recommendations", Items(billing));

    private static CombineDef Users(bool track = false, string onConflict = "priority", string nulls = "neverOverwrite", string withoutKey = "keep",
        string? sortBy = null, int maxRecords = 5000, bool required = true, Dictionary<string, string>? owners = null,
        string key = "id", List<string>? normalize = null) => new()
    {
        Sources = [Source("b.body.items", key, "crm"), Source("c.body.items", key, "billing")],
        TrackSources = track, OnConflict = onConflict, Nulls = nulls, WithoutKey = withoutKey, SortBy = sortBy,
        MaxRecords = maxRecords, Required = required, Owners = owners ?? new(), Normalize = normalize ?? ["trim"]
    };

    private static Recipe CombineRecipe(CombineDef users, string response = """{ "users": "{{combine.users}}" }""", bool cRequired = true) => new()
    {
        Id = "test",
        Route = "customer/{id}",
        RequireAuthentication = false,
        Inputs = new(StringComparer.OrdinalIgnoreCase) { ["route.id"] = new InputDef { Required = true } },
        Steps = [Step("b", "b/getOrders", true, ("request.route.id", "header.X-Customer-Id")), Step("c", "c/getRecs", cRequired)],
        Combine = new() { ["users"] = users },
        Response = JsonNode.Parse(response)
    };

    private static Task<CompositionResult> Run(Recipe recipe, FakeDownstream downstream, ExecutionMode? mode = null, ILogger<CompositionExecutor>? logger = null) =>
        Executor(Snapshot(recipe), downstream, logger).ExecuteAsync(recipe, Customer("C-1"), CallerContext.Anonymous(), mode ?? ExecutionMode.Gateway, default);

    private static async Task<JsonArray> Combined(CombineDef def, string crm, string billing)
    {
        var result = await Run(CombineRecipe(def), Lists(crm, billing));
        Assert.Equal(200, result.StatusCode);
        return result.Body!["users"]!.AsArray();
    }

    // ---------------------------------------------------------------- forEach without duplicates

    private static (Recipe Recipe, FakeDownstream Downstream) FanOut(bool distinct) => (
        new Recipe
        {
            Id = "test", Route = "customer/{id}", RequireAuthentication = false,
            Inputs = new(StringComparer.OrdinalIgnoreCase) { ["route.id"] = new InputDef { Required = true } },
            Steps =
            [
                Step("b", "b/getOrders", true, ("request.route.id", "header.X-Customer-Id")),
                ForEachStep("a", "a/getCustomer", "{{b.body.items[*].customerId}}", distinct)
            ],
            Response = JsonNode.Parse("""{ "customers": "{{a | count}}" }""")
        },
        new FakeDownstream()
            .On("b.test/api/orders", Items("""[{"customerId":"C-1"},{"customerId":"C-2"},{"customerId":"C-1"},{"customerId":"C-1"}]"""))
            .On("a.test/api/customers/C-1", new { id = "C-1" })
            .On("a.test/api/customers/C-2", new { id = "C-2" }));

    [Fact]
    public async Task ForEach_calls_each_unique_value_once_and_outputs_one_entry_per_unique_item()
    {
        var (recipe, downstream) = FanOut(distinct: true);
        var result = await Run(recipe, downstream);

        Assert.Equal(1, downstream.CallsTo("a.test/api/customers/C-1"));
        Assert.Equal(1, downstream.CallsTo("a.test/api/customers/C-2"));
        Assert.Equal(2, result.Body!["customers"]!.GetValue<int>());
        Assert.Equal(2, result.Trace.Single(t => t.Alias == "a").Calls);
    }

    [Fact]
    public async Task ForEach_with_distinct_off_calls_every_item()
    {
        var (recipe, downstream) = FanOut(distinct: false);
        var result = await Run(recipe, downstream);

        Assert.Equal(3, downstream.CallsTo("a.test/api/customers/C-1"));
        Assert.Equal(1, downstream.CallsTo("a.test/api/customers/C-2"));
        Assert.Equal(4, result.Body!["customers"]!.GetValue<int>());
    }

    // ---------------------------------------------------------------- merging

    [Fact]
    public async Task Two_sources_are_joined_by_key_with_matches_merged_and_the_rest_appended_in_first_seen_order()
    {
        var users = await Combined(Users(),
            crm: """[{"id":"u1","name":"Ann"},{"id":"u2","name":"Bob"}]""",
            billing: """[{"id":"u3","plan":"pro"},{"id":"u2","plan":"free"}]""");

        Assert.Equal(3, users.Count);
        Assert.Equal(new[] { "u1", "u2", "u3" }, users.Select(u => u!["id"]!.GetValue<string>()));
        Assert.Equal("""{"id":"u2","name":"Bob","plan":"free"}""", users[1]!.ToJsonString());
    }

    [Fact]
    public async Task Priority_keeps_the_first_sources_value_and_fills_missing_fields_from_later_sources()
    {
        var users = await Combined(Users(),
            crm: """[{"id":"u1","name":"Ann","city":null}]""",
            billing: """[{"id":"u1","name":"Annie","city":"Perth","plan":"pro"}]""");

        var user = users.Single()!;
        Assert.Equal("Ann", user["name"]!.GetValue<string>());
        Assert.Equal("Perth", user["city"]!.GetValue<string>()); // was null in the first source
        Assert.Equal("pro", user["plan"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_owner_wins_for_its_field_whichever_source_comes_first()
    {
        var owners = new Dictionary<string, string> { ["plan"] = "billing" };

        var billingSecond = await Combined(Users(owners: owners), """[{"id":"u1","plan":"crm-plan"}]""", """[{"id":"u1","plan":"billing-plan"}]""");
        var billingFirst = await Combined(new CombineDef
        {
            Sources = [Source("c.body.items", "id", "billing"), Source("b.body.items", "id", "crm")], Owners = owners
        }, """[{"id":"u1","plan":"crm-plan"}]""", """[{"id":"u1","plan":"billing-plan"}]""");

        Assert.Equal("billing-plan", billingSecond.Single()!["plan"]!.GetValue<string>());
        Assert.Equal("billing-plan", billingFirst.Single()!["plan"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_null_never_overwrites_a_value_by_default()
    {
        var users = await Combined(Users(onConflict: "newest:updatedAt"),
            crm: """[{"id":"u1","phone":"0400","updatedAt":"2024-01-01T00:00:00Z"}]""",
            billing: """[{"id":"u1","phone":null,"updatedAt":"2025-01-01T00:00:00Z"}]""");

        Assert.Equal("0400", users.Single()!["phone"]!.GetValue<string>());
    }

    [Fact]
    public async Task With_nulls_set_to_overwrite_a_newer_null_replaces_the_value()
    {
        var users = await Combined(Users(onConflict: "newest:updatedAt", nulls: "overwrite"),
            crm: """[{"id":"u1","phone":"0400","updatedAt":"2024-01-01T00:00:00Z"}]""",
            billing: """[{"id":"u1","phone":null,"updatedAt":"2025-01-01T00:00:00Z"}]""");

        Assert.Null(users.Single()!["phone"]);
    }

    [Fact]
    public async Task Newest_picks_the_record_with_the_later_timestamp_whichever_source_it_comes_from()
    {
        var billingNewer = await Combined(Users(onConflict: "newest:updatedAt"),
            crm: """[{"id":"u1","city":"Perth","updatedAt":"2024-01-01T00:00:00Z"}]""",
            billing: """[{"id":"u1","city":"Sydney","updatedAt":"2025-06-01T00:00:00Z"}]""");
        var crmNewer = await Combined(Users(onConflict: "newest:updatedAt"),
            crm: """[{"id":"u1","city":"Perth","updatedAt":"2025-06-01T00:00:00Z"}]""",
            billing: """[{"id":"u1","city":"Sydney","updatedAt":"2024-01-01T00:00:00Z"}]""");

        Assert.Equal("Sydney", billingNewer.Single()!["city"]!.GetValue<string>());
        Assert.Equal("Perth", crmNewer.Single()!["city"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_composite_key_needs_every_part_to_match()
    {
        var def = new CombineDef { Sources = [Source("b.body.items", "tenantId,id", "crm"), Source("c.body.items", "tenantId,id", "billing")] };
        var users = await Combined(def,
            crm: """[{"tenantId":"t1","id":7,"name":"Ann"},{"tenantId":"t2","id":7,"name":"Bob"}]""",
            billing: """[{"tenantId":"t1","id":7,"plan":"pro"}]""");

        Assert.Equal(2, users.Count); // same id in another tenant is a different record
        Assert.Equal("pro", users[0]!["plan"]!.GetValue<string>());
        Assert.Null(users[1]!["plan"]);
    }

    [Fact]
    public async Task Normalising_with_trim_and_lower_matches_keys_that_differ_only_in_spacing_and_case()
    {
        var users = await Combined(Users(normalize: ["trim", "lower"]), """[{"id":" U-1","name":"Ann"}]""", """[{"id":"u-1","plan":"pro"}]""");

        Assert.Equal(1, users.Count);
        Assert.Equal("pro", users.Single()!["plan"]!.GetValue<string>());
    }

    [Fact]
    public async Task Without_normalising_keys_that_differ_in_case_stay_separate()
    {
        var users = await Combined(Users(normalize: []), """[{"id":"U-1"}]""", """[{"id":"u-1"}]""");
        Assert.Equal(2, users.Count);
    }

    [Fact]
    public async Task Records_without_a_key_are_kept_as_their_own_records_by_default()
    {
        var users = await Combined(Users(), """[{"id":"u1"},{"name":"No key"},{"id":""}]""", """[{"name":"No key"}]""");
        Assert.Equal(4, users.Count); // u1 + three records that can't be matched (missing, empty, missing)
    }

    [Fact]
    public async Task Records_without_a_key_can_be_dropped()
    {
        var users = await Combined(Users(withoutKey: "drop"), """[{"id":"u1"},{"name":"No key"}]""", """[{"id":"u1"}]""");
        Assert.Equal(1, users.Count);
    }

    [Fact]
    public async Task TrackSources_lists_where_each_record_came_from()
    {
        var users = await Combined(Users(track: true), """[{"id":"u1"},{"id":"u2"}]""", """[{"id":"u2"},{"id":"u3"}]""");

        Assert.Equal("""["crm"]""", users[0]!["_sources"]!.ToJsonString());
        Assert.Equal("""["crm","billing"]""", users[1]!["_sources"]!.ToJsonString());
        Assert.Equal("""["billing"]""", users[2]!["_sources"]!.ToJsonString());
    }

    [Fact]
    public async Task SortBy_sorts_ascending_with_numbers_as_numbers_and_missing_values_last()
    {
        var users = await Combined(Users(sortBy: "age"), """[{"id":"a","age":10},{"id":"b"},{"id":"c","age":9}]""", """[{"id":"d","age":100}]""");
        Assert.Equal(new[] { "c", "a", "d", "b" }, users.Select(u => u!["id"]!.GetValue<string>()));
    }

    [Fact]
    public async Task SortBy_with_a_dash_sorts_descending_and_still_puts_missing_values_last()
    {
        var users = await Combined(Users(sortBy: "-age"), """[{"id":"a","age":10},{"id":"b"},{"id":"c","age":9}]""", """[{"id":"d","age":100}]""");
        Assert.Equal(new[] { "d", "a", "c", "b" }, users.Select(u => u!["id"]!.GetValue<string>()));
    }

    [Fact]
    public async Task SortBy_is_stable_for_equal_values()
    {
        var users = await Combined(Users(sortBy: "grade"), """[{"id":"1","grade":"x"},{"id":"2","grade":"x"}]""", """[{"id":"3","grade":"a"},{"id":"4","grade":"x"}]""");
        Assert.Equal(new[] { "3", "1", "2", "4" }, users.Select(u => u!["id"]!.GetValue<string>()));
    }

    [Fact]
    public async Task A_source_that_is_a_single_object_counts_as_a_list_of_one()
    {
        var def = new CombineDef { Sources = [Source("b.body", "id", "crm"), Source("c.body.items", "id", "billing")] };
        var downstream = new FakeDownstream()
            .On("b.test/api/orders", JsonNode.Parse("""{ "id": "u1", "name": "Ann" }""")!)
            .On("c.test/api/recommendations", Items("""[{"id":"u1","plan":"pro"}]"""));
        var result = await Run(CombineRecipe(def), downstream);

        Assert.Equal("""[{"id":"u1","name":"Ann","plan":"pro"}]""", result.Body!["users"]!.ToJsonString());
    }

    [Fact]
    public async Task Items_that_are_not_objects_are_ignored_and_counted()
    {
        var result = await Run(CombineRecipe(Users()), Lists("""["text",{"id":"u1"},5]""", "[]"), ExecutionMode.AdminTest);

        Assert.Equal(1, result.Body!["users"]!.AsArray().Count);
        Assert.Equal("crm 3, billing 0 → 1 records (0 matched, 2 ignored)", result.Trace.Single(t => t.Alias == "combine.users").Detail);
    }

    [Fact]
    public async Task Merged_records_are_copies_and_never_change_the_step_results()
    {
        var recipe = CombineRecipe(Users(), """{ "users": "{{combine.users}}", "crm": "{{b.body.items}}" }""");
        var result = await Run(recipe, Lists("""[{"id":"u1","name":"Ann"}]""", """[{"id":"u1","plan":"pro"}]"""));

        Assert.Equal("""[{"id":"u1","name":"Ann"}]""", result.Body!["crm"]!.ToJsonString()); // no "plan" added
    }

    [Fact]
    public async Task Combined_lists_work_with_transforms_in_the_response()
    {
        var recipe = CombineRecipe(Users(), """{ "total": "{{combine.users | count}}" }""");
        var result = await Run(recipe, Lists("""[{"id":"u1"},{"id":"u2"}]""", """[{"id":"u2"},{"id":"u3"}]"""));

        Assert.Equal(3, result.Body!["total"]!.GetValue<int>());
    }

    [Fact]
    public async Task An_optional_source_step_that_failed_is_left_out_and_the_rest_is_combined()
    {
        var downstream = new FakeDownstream()
            .On("b.test/api/orders", Items("""[{"id":"u1","name":"Ann"}]"""))
            .On("c.test/api/recommendations", new { }, System.Net.HttpStatusCode.InternalServerError);
        var result = await Run(CombineRecipe(Users(), cRequired: false), downstream);

        Assert.Equal(200, result.StatusCode);
        Assert.True(result.Partial);
        Assert.Equal(1, result.Body!["users"]!.AsArray().Count);
        var error = Assert.Single(result.Body["_errors"]!.AsArray()); // only the step's failure; the combine itself worked
        Assert.Equal("c", error!["step"]!.GetValue<string>());
    }

    // ---------------------------------------------------------------- failures

    [Fact]
    public async Task Too_many_records_fail_a_required_combine_with_502_combine_failed()
    {
        var result = await Run(CombineRecipe(Users(maxRecords: 2)), Lists("""[{"id":"1"},{"id":"2"}]""", """[{"id":"3"}]"""));

        Assert.Equal(502, result.StatusCode);
        Assert.Equal("combine_failed", result.ErrorCode);
        Assert.False(result.Retryable);
        Assert.Equal("combine_failed", result.Body!["code"]!.GetValue<string>());
        Assert.Equal("Records from several services couldn't be combined.", result.Body["title"]!.GetValue<string>());
        var error = Assert.Single(result.Body["errors"]!.AsArray())!;
        Assert.Equal("combine.users", error["step"]!.GetValue<string>());
        Assert.Equal("Combine users", error["name"]!.GetValue<string>());
        Assert.Null(error["service"]);
        Assert.False(error["retryable"]!.GetValue<bool>());
        Assert.Equal("The combined list 'users' has more than 2 records.", error["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task Too_many_records_in_an_optional_combine_give_a_partial_answer_with_the_field_null()
    {
        var result = await Run(CombineRecipe(Users(maxRecords: 2, required: false)), Lists("""[{"id":"1"},{"id":"2"}]""", """[{"id":"3"}]"""));

        Assert.Equal(200, result.StatusCode);
        Assert.True(result.Partial);
        Assert.Null(result.Body!["users"]);
        var error = Assert.Single(result.Body["_errors"]!.AsArray())!;
        Assert.Equal("combine_failed", error["code"]!.GetValue<string>());
        Assert.Equal("combine.users", error["step"]!.GetValue<string>());
    }

    // ---------------------------------------------------------------- trace

    [Fact]
    public async Task Admin_test_runs_show_source_names_and_counts_in_the_trace()
    {
        var result = await Run(CombineRecipe(Users()), Lists("""[{"id":"u1"},{"id":"u2"}]""", """[{"id":"u2"},{"id":"u3"}]"""), ExecutionMode.AdminTest);

        var trace = result.Trace.Single(t => t.Alias == "combine.users");
        Assert.Equal("Combine users", trace.DisplayName);
        Assert.Equal(StepOutcome.Succeeded, trace.Outcome);
        Assert.Equal(0, trace.Calls);
        Assert.Null(trace.HttpStatus);
        Assert.Equal("crm 2, billing 2 → 3 records (1 matched)", trace.Detail);
    }

    [Fact]
    public async Task Gateway_calls_get_a_combine_trace_without_any_detail()
    {
        var result = await Run(CombineRecipe(Users()), Lists("""[{"id":"u1"}]""", "[]"));

        var trace = result.Trace.Single(t => t.Alias == "combine.users");
        Assert.Equal(StepOutcome.Succeeded, trace.Outcome);
        Assert.Null(trace.Detail);
    }

    // ---------------------------------------------------------------- logging

    [Fact]
    public async Task A_combine_is_logged_with_counts_only_and_never_with_record_values()
    {
        var logger = new CapturingLogger<CompositionExecutor>();
        await Run(CombineRecipe(Users()), Lists("""[{"id":"secret-id-1","name":"Zelda Fitzgerald"}]""", """[{"id":"secret-id-1","plan":"gold"}]"""), logger: logger);

        var line = Assert.Single(logger.Entries.Where(e => e.Event.Id == 1500));
        Assert.Equal(LogLevel.Information, line.Level);
        Assert.True(line.Message.Contains("users") && line.Message.Contains("crm:1,billing:1") && line.Message.Contains("matched 1") && line.Message.Contains("output 1"));
        Assert.DoesNotContain(logger.Entries, e => e.Message.Contains("Zelda") || e.Message.Contains("secret-id-1") || e.Message.Contains("gold"));
    }

    [Fact]
    public async Task A_failed_combine_is_logged_as_a_warning()
    {
        var logger = new CapturingLogger<CompositionExecutor>();
        await Run(CombineRecipe(Users(maxRecords: 1)), Lists("""[{"id":"a"}]""", """[{"id":"b"}]"""), logger: logger);

        var line = Assert.Single(logger.Entries.Where(e => e.Event.Id == 1501));
        Assert.Equal(LogLevel.Warning, line.Level);
    }

    // ---------------------------------------------------------------- validation

    private static ValidationResult Validate(Recipe recipe) => RecipeValidator.Validate(recipe, Snapshot());

    [Fact] public void A_valid_combine_has_no_issues_and_no_warnings()
    {
        var result = Validate(CombineRecipe(Users()));
        Assert.True(result.IsValid);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void A_source_that_starts_with_an_unknown_step_is_reported()
    {
        var def = new CombineDef { Sources = [Source("zzz.body.items", "id")] };
        Assert.Contains(Validate(CombineRecipe(def)).Issues, i => i.Where == "combine.users" && i.Message.Contains("'zzz'"));
    }

    [Fact]
    public void A_source_that_starts_with_request_is_reported()
    {
        var def = new CombineDef { Sources = [Source("request.query.ids", "id")] };
        Assert.Contains(Validate(CombineRecipe(def)).Issues, i => i.Message.Contains("name of a step"));
    }

    [Fact]
    public void A_step_that_uses_a_combined_list_is_reported()
    {
        var recipe = CombineRecipe(Users());
        var steps = recipe.Steps.Append(Step("d", "a/getCustomer", true, ("combine.users[0].id", "path.customerId"))).ToList();
        var bad = new Recipe { Id = recipe.Id, Route = recipe.Route, RequireAuthentication = false, Inputs = recipe.Inputs, Steps = steps, Combine = recipe.Combine, Response = recipe.Response };

        Assert.Contains(Validate(bad).Issues, i => i.Message.Contains("Steps can't use combined lists"));
    }

    [Fact]
    public void A_step_cannot_be_called_combine()
    {
        var recipe = Recipe(Step("combine", "a/getCustomer", true, ("request.route.id", "path.customerId")));
        Assert.Contains(Validate(recipe).Issues, i => i.Message.Contains("reserved"));
    }

    [Fact]
    public void A_response_that_uses_an_unknown_combined_list_is_reported()
    {
        var recipe = CombineRecipe(Users(), """{ "x": "{{combine.nope}}" }""");
        Assert.Contains(Validate(recipe).Issues, i => i.Where == "response" && i.Message.Contains("isn't defined"));
    }

    [Fact]
    public void An_owner_that_is_not_a_source_is_reported()
    {
        var recipe = CombineRecipe(Users(owners: new() { ["plan"] = "billingg" }));
        Assert.Contains(Validate(recipe).Issues, i => i.Message.Contains("'billingg'") && i.Message.Contains("crm, billing"));
    }

    [Fact]
    public void Two_sources_with_the_same_name_are_reported()
    {
        var def = new CombineDef { Sources = [Source("b.body.items", "id", "crm"), Source("c.body.items", "id", "crm")] };
        Assert.Contains(Validate(CombineRecipe(def)).Issues, i => i.Message.Contains("Two sources are called 'crm'"));
    }

    [Fact]
    public void Unknown_settings_are_reported_in_plain_English()
    {
        var recipe = CombineRecipe(Users(onConflict: "latest", nulls: "sometimes", withoutKey: "maybe", maxRecords: 0, normalize: ["upper"]));
        var messages = Validate(recipe).Issues.Select(i => i.Message).ToList();

        Assert.Contains(messages, m => m.Contains("On conflict must be"));
        Assert.Contains(messages, m => m.Contains("Nulls must be"));
        Assert.Contains(messages, m => m.Contains("Without a key must be"));
        Assert.Contains(messages, m => m.Contains("Max records must be"));
        Assert.Contains(messages, m => m.Contains("Normalize can only be"));
    }

    [Fact]
    public void Combining_on_an_email_is_a_warning_that_does_not_block_publishing()
    {
        var result = Validate(CombineRecipe(Users(key: "email")));

        Assert.True(result.IsValid);
        var warning = Assert.Single(result.Warnings.Where(w => w.Where == "combine.users").GroupBy(w => w.Message).Select(g => g.First()));
        Assert.Equal("Matching on 'email' can merge different people's or customers' records. Use an id that both services share, or link them with a mapping step.", warning.Message);
    }

    [Fact]
    public void A_person_like_key_is_still_recognised_inside_a_dotted_or_composite_key()
    {
        var result = Validate(CombineRecipe(Users(key: "tenantId,contact.Date_Of_Birth")));
        Assert.Contains(result.Warnings, w => w.Message.Contains("'contact.Date_Of_Birth'"));
    }

    // ---------------------------------------------------------------- authoring: source options

    [Fact]
    public void The_response_can_pick_combined_lists_but_steps_cannot()
    {
        var recipe = CombineRecipe(Users());
        var forResponse = new SourceCatalog().For(recipe, null, Snapshot());
        var forStep = new SourceCatalog().For(recipe, "c", Snapshot());

        var option = Assert.Single(forResponse.Where(o => o.Group == "Combined lists"));
        Assert.Equal("combine.users", option.Expression);
        Assert.Equal("Combined → users", option.Label);
        Assert.DoesNotContain(forStep, o => o.Group == "Combined lists");
    }

    // ---------------------------------------------------------------- OpenAPI

    private static ConfigSnapshot DocSnapshot(Recipe recipe) => new(
    [
        Service("svc-x", new ApiEndpoint
        {
            Id = "getUsers", Path = "/users",
            ResponseSchema = JsonNode.Parse("""{ "type": "object", "properties": { "users": { "type": "array", "items": { "type": "object", "properties": { "id": { "type": "string" }, "name": { "type": "string" } } } } } }""")
        }),
        Service("svc-y", new ApiEndpoint
        {
            Id = "getContacts", Path = "/contacts",
            ResponseSchema = JsonNode.Parse("""{ "type": "object", "properties": { "contacts": { "type": "array", "items": { "type": "object", "properties": { "id": { "type": "integer" }, "phone": { "type": "string" } } } } } }""")
        })
    ], [], [recipe], "v1");

    private static JsonObject UsersDoc(bool track, string response = """{ "users": "{{combine.users}}" }""", bool required = true) =>
        OpenApiDocumentBuilder.Build(DocSnapshot(new Recipe
        {
            Id = "dir", Route = "directory", RequireAuthentication = false,
            Steps = [Step("x", "svc-x/getUsers"), Step("y", "svc-y/getContacts")],
            Combine = new()
            {
                ["users"] = new CombineDef { TrackSources = track, Required = required, Sources = [Source("x.body.users", "id"), Source("y.body.contacts", "id")] }
            },
            Response = JsonNode.Parse(response)
        }), new OpenApiBuildOptions());

    private static JsonNode ResponseField(JsonObject doc, string name) =>
        doc["paths"]!["/bff/directory"]!["get"]!["responses"]!["200"]!["content"]!["application/json"]!["schema"]!["properties"]![name]!;

    [Fact]
    public void A_combined_list_is_documented_as_an_array_with_the_union_of_its_sources_properties()
    {
        var field = ResponseField(UsersDoc(track: false), "users");

        Assert.Equal("array", field["type"]!.GetValue<string>());
        var properties = field["items"]!["properties"]!.AsObject();
        Assert.Equal(new[] { "id", "name", "phone" }, properties.Select(p => p.Key));
        Assert.Equal("string", properties["id"]!["type"]!.GetValue<string>()); // the first source wins a clash
    }

    [Fact]
    public void A_combined_list_with_TrackSources_documents_the_sources_field()
    {
        var properties = ResponseField(UsersDoc(track: true), "users")["items"]!["properties"]!;
        Assert.Equal("array", properties["_sources"]!["type"]!.GetValue<string>());
        Assert.Equal("string", properties["_sources"]!["items"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void Transforms_after_a_combined_list_follow_the_usual_rules()
    {
        var doc = UsersDoc(track: false, """{ "n": "{{combine.users | count}}" }""");
        Assert.Equal("integer", ResponseField(doc, "n")["type"]!.GetValue<string>());
    }

    [Fact]
    public void An_unknown_combined_list_is_documented_as_any_value()
    {
        var doc = UsersDoc(track: false, """{ "x": "{{combine.nope}}" }""");
        Assert.Equal(0, ResponseField(doc, "x").AsObject().Count);
    }

    [Fact]
    public void An_optional_combined_list_is_documented_as_nullable_and_adds_errors()
    {
        var doc = UsersDoc(track: false, required: false);
        Assert.True(ResponseField(doc, "users")["nullable"]!.GetValue<bool>());
        Assert.NotNull(ResponseField(doc, "_errors"));
    }

    [Fact]
    public void The_error_codes_include_combine_failed()
    {
        var codes = UsersDoc(track: false)["components"]!["schemas"]!["Problem"]!["properties"]!["code"]!["enum"]!.AsArray();
        Assert.Contains(codes, c => c!.GetValue<string>() == "combine_failed");
    }

    // ---------------------------------------------------------------- via: services that don't share an id

    /// <summary>Step "a" plays the mapping service: it returns the link rows. "b" is the CRM and "c" is billing.</summary>
    private static CombineDef ViaUsers(string withoutKey = "keep", string crmKey = "crmId", string match = "crmId", string use = "sharedId", string billingKey = "id") => new()
    {
        Sources =
        [
            new CombineSource { From = "b.body.items", Key = crmKey, Name = "crm", Via = new CombineVia { From = "a.body.items", Match = match, Use = use } },
            Source("c.body.items", billingKey, "billing")
        ],
        WithoutKey = withoutKey
    };

    private static Recipe ViaRecipe(CombineDef users, bool mappingRequired = true) => new()
    {
        Id = "test",
        Route = "customer/{id}",
        RequireAuthentication = false,
        Inputs = new(StringComparer.OrdinalIgnoreCase) { ["route.id"] = new InputDef { Required = true } },
        Steps =
        [
            Step("a", "a/getCustomer", mappingRequired, ("request.route.id", "path.customerId")),
            Step("b", "b/getOrders", true, ("request.route.id", "header.X-Customer-Id")),
            Step("c", "c/getRecs", true)
        ],
        Combine = new() { ["users"] = users },
        Response = JsonNode.Parse("""{ "users": "{{combine.users}}" }""")
    };

    private static FakeDownstream ViaLists(string links, string crm, string billing) => new FakeDownstream()
        .On("a.test/api/customers/C-1", Items(links))
        .On("b.test/api/orders", Items(crm))
        .On("c.test/api/recommendations", Items(billing));

    private static Task<CompositionResult> RunVia(CombineDef def, string links, string crm, string billing, bool mappingRequired = true,
        ExecutionMode? mode = null, ILogger<CompositionExecutor>? logger = null) =>
        Run(ViaRecipe(def, mappingRequired), ViaLists(links, crm, billing), mode, logger);

    [Fact]
    public async Task A_mapping_list_joins_records_whose_ids_differ()
    {
        var result = await RunVia(ViaUsers(),
            links: """[{"crmId":"7","sharedId":"X1"},{"crmId":"8","sharedId":"X2"}]""",
            crm: """[{"crmId":"7","name":"Ann"},{"crmId":"8","name":"Bob"}]""",
            billing: """[{"id":"X2","plan":"free"},{"id":"X1","plan":"pro"}]""");

        var users = result.Body!["users"]!.AsArray();
        Assert.Equal(2, users.Count);
        Assert.Equal("""{"crmId":"7","name":"Ann","id":"X1","plan":"pro"}""", users[0]!.ToJsonString());
        Assert.Equal("free", users[1]!["plan"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_record_missing_from_the_mapping_is_unmapped_and_kept_by_default()
    {
        var result = await RunVia(ViaUsers(), """[{"crmId":"7","sharedId":"X1"}]""", """[{"crmId":"7"},{"crmId":"99","name":"Nobody"}]""", """[{"id":"X1"}]""", mode: ExecutionMode.AdminTest);

        Assert.Equal(2, result.Body!["users"]!.AsArray().Count); // X1 merged, 99 kept on its own
        Assert.Equal("crm 2, billing 1 → 2 records (1 matched, 1 unmapped)", result.Trace.Single(t => t.Alias == "combine.users").Detail);
    }

    [Fact]
    public async Task A_record_missing_from_the_mapping_can_be_left_out()
    {
        var result = await RunVia(ViaUsers(withoutKey: "drop"), """[{"crmId":"7","sharedId":"X1"}]""", """[{"crmId":"7"},{"crmId":"99"}]""", """[{"id":"X1"}]""");
        Assert.Equal(1, result.Body!["users"]!.AsArray().Count);
    }

    [Fact]
    public async Task An_id_that_maps_to_two_different_shared_ids_is_never_merged_and_is_counted()
    {
        var result = await RunVia(ViaUsers(),
            links: """[{"crmId":"7","sharedId":"X1"},{"crmId":"7","sharedId":"X2"}]""",
            crm: """[{"crmId":"7","name":"Ann"}]""",
            billing: """[{"id":"X1","plan":"pro"},{"id":"X2","plan":"free"}]""",
            mode: ExecutionMode.AdminTest);

        var users = result.Body!["users"]!.AsArray();
        Assert.Equal(3, users.Count); // nothing was merged
        Assert.Null(users.Single(u => u!["crmId"] is not null)!["plan"]);
        Assert.Equal("crm 1, billing 2 → 3 records (0 matched, 1 ambiguous)", result.Trace.Single(t => t.Alias == "combine.users").Detail);
    }

    [Fact]
    public async Task The_same_link_listed_twice_is_not_ambiguous()
    {
        var result = await RunVia(ViaUsers(), """[{"crmId":"7","sharedId":"X1"},{"crmId":"7","sharedId":"X1"}]""", """[{"crmId":"7"}]""", """[{"id":"X1"}]""");
        Assert.Equal(1, result.Body!["users"]!.AsArray().Count);
    }

    [Fact]
    public async Task A_mapping_works_with_a_composite_key()
    {
        var def = ViaUsers(crmKey: "tenant,crmId", match: "tenant,crmId", use: "org,acct", billingKey: "org,acct");
        var result = await RunVia(def,
            links: """[{"tenant":"t1","crmId":"7","org":"o1","acct":"A9"}]""",
            crm: """[{"tenant":"t1","crmId":"7","name":"Ann"},{"tenant":"t2","crmId":"7","name":"Other tenant"}]""",
            billing: """[{"org":"o1","acct":"A9","plan":"pro"}]""");

        var users = result.Body!["users"]!.AsArray();
        Assert.Equal(2, users.Count);
        Assert.Equal("pro", users[0]!["plan"]!.GetValue<string>());
        Assert.Null(users[1]!["plan"]); // same crmId in another tenant has no link
    }

    [Fact]
    public async Task When_the_mapping_step_failed_every_record_is_unmapped_and_the_combine_still_succeeds()
    {
        var downstream = new FakeDownstream()
            .On("a.test/api/customers/C-1", new { }, System.Net.HttpStatusCode.InternalServerError)
            .On("b.test/api/orders", Items("""[{"crmId":"7"},{"crmId":"8"}]"""))
            .On("c.test/api/recommendations", Items("""[{"id":"X1"}]"""));
        var result = await Run(ViaRecipe(ViaUsers(), mappingRequired: false), downstream, ExecutionMode.AdminTest);

        Assert.Equal(200, result.StatusCode);
        Assert.True(result.Partial); // the failed mapping step is reported as usual
        Assert.Equal(3, result.Body!["users"]!.AsArray().Count);
        Assert.Equal("crm 2, billing 1 → 3 records (0 matched, 2 unmapped)", result.Trace.Single(t => t.Alias == "combine.users").Detail);
    }

    [Fact]
    public async Task Unmapped_and_ambiguous_counts_are_logged_without_values()
    {
        var logger = new CapturingLogger<CompositionExecutor>();
        await RunVia(ViaUsers(), """[{"crmId":"secret-7","sharedId":"X1"}]""", """[{"crmId":"secret-7"},{"crmId":"secret-8"}]""", """[{"id":"X1"}]""", logger: logger);

        var line = Assert.Single(logger.Entries.Where(e => e.Event.Id == 1500));
        Assert.True(line.Message.Contains("unmapped 1") && line.Message.Contains("ambiguous 0") && line.Message.Contains("matched 1"));
        Assert.DoesNotContain(logger.Entries, e => e.Message.Contains("secret-"));
    }

    [Fact]
    public void A_mapping_that_starts_with_an_unknown_step_is_reported()
    {
        var def = ViaUsers();
        var bad = new CombineDef { Sources = [new CombineSource { From = "b.body.items", Key = "crmId", Via = new CombineVia { From = "zzz.body.items", Match = "crmId", Use = "sharedId" } }, def.Sources[1]] };
        Assert.Contains(Validate(ViaRecipe(bad)).Issues, i => i.Message.Contains("(mapping)") && i.Message.Contains("'zzz'"));
    }

    [Fact]
    public void A_mapping_needs_both_fields_and_the_same_number_of_parts_as_the_key()
    {
        var empty = Validate(ViaRecipe(ViaUsers(match: "", use: "")));
        Assert.Contains(empty.Issues, i => i.Message.Contains("needs the field that matches"));
        Assert.Contains(empty.Issues, i => i.Message.Contains("needs the field to use"));

        var mismatch = Validate(ViaRecipe(ViaUsers(crmKey: "tenant,crmId", match: "crmId", use: "sharedId")));
        Assert.Contains(mismatch.Issues, i => i.Message.Contains("same number of fields as the key (2)"));
    }

    [Fact]
    public void A_mapping_field_that_is_not_a_valid_name_is_reported()
    {
        Assert.Contains(Validate(ViaRecipe(ViaUsers(use: "shared id!"))).Issues, i => i.Message.Contains("isn't a valid field"));
    }

    [Fact]
    public void A_valid_mapping_has_no_issues_and_no_warnings()
    {
        var result = Validate(ViaRecipe(ViaUsers()));
        Assert.True(result.IsValid);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void An_email_in_a_source_with_a_mapping_is_not_flagged_but_an_email_as_the_shared_field_is()
    {
        Assert.Empty(Validate(ViaRecipe(ViaUsers(crmKey: "email", match: "email"))).Warnings);
        Assert.Contains(Validate(ViaRecipe(ViaUsers(use: "email", billingKey: "email"))).Warnings, w => w.Message.Contains("'email'"));
    }

    // ---------------------------------------------------------------- unused combines and the default answer

    [Fact]
    public void A_combined_list_the_response_never_uses_is_a_warning_not_an_error()
    {
        var result = Validate(CombineRecipe(Users(), """{ "x": "{{b.body.items}}" }"""));
        Assert.True(result.IsValid);
        Assert.Contains(result.Warnings, w => w.Where == "combine.users" && w.Message == "The combined list 'users' isn't used in the response.");
    }

    [Fact]
    public void A_combined_list_used_only_through_a_transform_counts_as_used()
    {
        Assert.Empty(Validate(CombineRecipe(Users(), """{ "n": "{{combine.users | count}}" }""")).Warnings);
    }

    [Fact]
    public async Task Without_a_response_template_each_combined_list_is_returned_under_its_name()
    {
        var recipe = new Recipe
        {
            Id = "test", Route = "customer/{id}", RequireAuthentication = false,
            Inputs = new(StringComparer.OrdinalIgnoreCase) { ["route.id"] = new InputDef { Required = true } },
            Steps = CombineRecipe(Users()).Steps, Combine = new() { ["people"] = Users() }
        };
        var result = await Run(recipe, Lists("""[{"id":"u1"}]""", """[{"id":"u1","plan":"pro"}]"""));

        Assert.Equal("""[{"id":"u1","plan":"pro"}]""", result.Body!["people"]!.ToJsonString());
        Assert.NotNull(result.Body["b"]);
        Assert.True(Validate(recipe).IsValid);
    }

    [Fact]
    public void Without_a_response_template_a_combined_list_cannot_share_a_steps_name()
    {
        var recipe = new Recipe
        {
            Id = "test", Route = "customer/{id}", RequireAuthentication = false,
            Inputs = new(StringComparer.OrdinalIgnoreCase) { ["route.id"] = new InputDef { Required = true } },
            Steps = CombineRecipe(Users()).Steps, Combine = new() { ["b"] = Users() }
        };
        Assert.Contains(Validate(recipe).Issues, i => i.Message.Contains("also the name of a step"));
    }
}
