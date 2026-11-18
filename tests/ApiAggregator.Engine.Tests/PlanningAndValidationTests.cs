using ApiAggregator.Engine.Model;
using ApiAggregator.Engine.Planning;
using ApiAggregator.Engine.Validation;
using static ApiAggregator.Engine.Tests.TestData;

namespace ApiAggregator.Engine.Tests;

public class PlanningAndValidationTests
{
    private static Recipe AbcRecipe() => Recipe(
        Step("a", "a/getCustomer", true, ("request.route.id", "path.customerId")),
        Step("b", "b/getOrders", true, ("a.body.id", "header.X-Customer-Id")),
        Step("c", "c/getRecs", false, ("a.body.segment", "query.segment"), ("b.body.tier", "header.X-Tier")));

    [Fact]
    public void Dependencies_are_inferred_from_bindings()
    {
        var plan = ExecutionPlanner.Plan(AbcRecipe());

        Assert.Equal(3, plan.Levels.Count);
        Assert.Equal(new[] { "a" }, plan.Levels[0].Select(s => s.Alias));
        Assert.Equal(new[] { "b" }, plan.Levels[1].Select(s => s.Alias));
        Assert.Equal(new[] { "c" }, plan.Levels[2].Select(s => s.Alias));
        Assert.True(plan.DependsOn["c"].SetEquals(new[] { "a", "b" }));
    }

    [Fact]
    public void Independent_steps_share_a_level()
    {
        var plan = ExecutionPlanner.Plan(Recipe(
            Step("a", "a/getCustomer", true, ("request.route.id", "path.customerId")),
            Step("c", "c/getRecs", true, ("request.route.id", "query.customer"))));
        Assert.Single(plan.Levels);
        Assert.Equal(2, plan.Levels[0].Count);
    }

    [Fact]
    public void Loops_are_rejected()
    {
        var recipe = Recipe(
            Step("a", "a/getCustomer", true, ("b.body.id", "path.customerId")),
            Step("b", "b/getOrders", true, ("a.body.id", "header.X-Customer-Id")));
        Assert.Throws<InvalidOperationException>(() => ExecutionPlanner.Plan(recipe));
        Assert.Contains(RecipeValidator.Validate(recipe, Snapshot()).Issues, i => i.Message.Contains("loop"));
    }

    [Fact]
    public void Valid_recipe_has_no_issues() => Assert.True(RecipeValidator.Validate(AbcRecipe(), Snapshot()).IsValid);

    [Fact]
    public void Unknown_step_reference_is_reported()
    {
        var recipe = Recipe(Step("a", "a/getCustomer", true, ("request.route.id", "path.customerId")),
                            Step("b", "b/getOrders", true, ("zzz.body.id", "header.X-Customer-Id")));
        Assert.Contains(RecipeValidator.Validate(recipe, Snapshot()).Issues, i => i.Message.Contains("'zzz'"));
    }

    [Fact]
    public void Undeclared_request_input_is_reported()
    {
        var recipe = Recipe(Step("a", "a/getCustomer", true, ("request.query.secret", "path.customerId")));
        Assert.Contains(RecipeValidator.Validate(recipe, Snapshot()).Issues, i => i.Message.Contains("isn't declared"));
    }

    [Fact]
    public void Missing_required_parameter_is_reported()
    {
        var recipe = Recipe(Step("b", "b/getOrders", true)); // X-Customer-Id header is required by the endpoint
        Assert.Contains(RecipeValidator.Validate(recipe, Snapshot()).Issues, i => i.Message.Contains("X-Customer-Id"));
    }

    [Theory]
    [InlineData("header.Authorization")]
    [InlineData("header.Cookie")]
    [InlineData("header.Host")]
    public void Security_headers_cannot_be_bound(string target)
    {
        var recipe = Recipe(Step("a", "a/getCustomer", true, ("request.route.id", "path.customerId"), ("request.route.id", target)));
        Assert.Contains(RecipeValidator.Validate(recipe, Snapshot()).Issues, i => i.Message.Contains("can't be set"));
    }

    [Fact]
    public void Unknown_endpoint_is_reported()
    {
        var recipe = Recipe(Step("a", "a/doesNotExist", true, ("request.route.id", "path.customerId")));
        Assert.Contains(RecipeValidator.Validate(recipe, Snapshot()).Issues, i => i.Message.Contains("was not found"));
    }
}
