using ApiAggregator.Engine.Config;
using ApiAggregator.Engine.Model;
using ApiAggregator.Engine.Routing;

namespace ApiAggregator.Engine.Tests;

/// <summary>Which published endpoint answers an address, also with thousands of them (the quick pre-check must never change the answer).</summary>
public class RouterTests
{
    private static Recipe Route(string id, string route, string method = "GET", RecipeStatus status = RecipeStatus.Published) =>
        new() { Id = id, Route = route, Method = method, Status = status };

    private static RecipeRouter Router(params Recipe[] recipes) => new(new InMemoryConfigStore(new ConfigSnapshot([], [], recipes, "v1")));

    private static async Task<string?> Match(RecipeRouter router, string path, string method = "GET") =>
        (await router.MatchAsync(method, path, default))?.Recipe.Id;

    [Fact]
    public async Task A_literal_word_wins_over_a_parameter_in_the_same_place()
    {
        var router = Router(Route("by-id", "customer/{id}/overview"), Route("mine", "customer/me/overview"));

        Assert.Equal("mine", await Match(router, "customer/me/overview"));
        Assert.Equal("by-id", await Match(router, "customer/C-1/overview"));
    }

    [Fact]
    public async Task Route_values_are_returned_and_literals_ignore_case()
    {
        var router = Router(Route("orders", "Customer/{id}/orders/{orderId}"));

        var match = await router.MatchAsync("GET", "/customer/C-1/ORDERS/42/", default);

        Assert.Equal("orders", match!.Recipe.Id);
        Assert.Equal("C-1", match.RouteValues["id"]);
        Assert.Equal("42", match.RouteValues["orderId"]);
    }

    [Fact]
    public async Task The_method_must_match_and_unknown_addresses_match_nothing()
    {
        var router = Router(Route("search", "orders/search", "POST"));

        Assert.Equal("search", await Match(router, "orders/search", "post"));
        Assert.Null(await Match(router, "orders/search", "GET"));
        Assert.Null(await Match(router, "orders/search/more", "POST"));
        Assert.Null(await Match(router, "nowhere", "DELETE"));
    }

    [Fact]
    public async Task Drafts_and_retired_endpoints_are_not_served()
    {
        var router = Router(Route("draft", "a/b", status: RecipeStatus.Draft), Route("old", "c/d", status: RecipeStatus.Retired));

        Assert.Null(await Match(router, "a/b"));
        Assert.Null(await Match(router, "c/d"));
    }

    [Fact]
    public async Task Optional_parameters_still_match_with_and_without_the_value()
    {
        var router = Router(Route("list", "orders/{page?}"));

        Assert.Equal("list", await Match(router, "orders"));
        Assert.Equal("list", await Match(router, "orders/2"));
    }

    [Fact]
    public async Task An_address_with_an_empty_segment_gets_the_same_answer_as_before()
    {
        var router = Router(Route("one", "a/{id}"));

        // The framework's matcher decides these unusual paths; the pre-check stays out of the way.
        Assert.Null(await Match(router, "a//"));
        Assert.Null(await Match(router, "a//b"));
    }

    [Fact]
    public async Task Each_of_two_thousand_endpoints_answers_only_its_own_address()
    {
        string[] areas = ["orders", "patients", "billing", "catalog"];
        var recipes = Enumerable.Range(1, 2000).Select(i => (i % 3) switch
        {
            0 => Route($"r{i}", $"{areas[i % 4]}/r{i}/{{id}}"),
            1 => Route($"r{i}", $"{areas[i % 4]}/{{id}}/r{i}/{{sub}}"),
            _ => Route($"r{i}", $"{areas[i % 4]}/r{i}/summary")
        }).ToArray();
        var router = Router(recipes);

        foreach (var recipe in recipes)
        {
            var path = recipe.Route.Replace("{id}", "X-1").Replace("{sub}", "y");
            Assert.Equal(recipe.Id, await Match(router, path));
        }
        Assert.Null(await Match(router, "orders/r99999/X-1"));
    }
}
