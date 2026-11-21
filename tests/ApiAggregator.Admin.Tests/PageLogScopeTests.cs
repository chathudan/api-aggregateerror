using ApiAggregator.Admin.Hosting;

namespace ApiAggregator.Admin.Tests;

/// <summary>Admin log lines name the page and the endpoint being edited, never the query (it can hold values).</summary>
public class PageLogScopeTests
{
    [Theory]
    [InlineData("endpoints/customer-summary", "/endpoints/customer-summary", "customer-summary")]
    [InlineData("endpoints/customer-summary?guide=first#step-3", "/endpoints/customer-summary", "customer-summary")]
    [InlineData("endpoints/new?guide=first", "/endpoints/new", null)]
    [InlineData("logs?correlation=abc", "/logs", null)]
    [InlineData("", "/", null)]
    [InlineData(null, "/", null)]
    public void The_page_is_the_path_only_and_the_endpoint_comes_from_the_editor_address(string? path, string page, string? recipe)
    {
        Assert.Equal(page, PageLogScope.PageOf(path));
        Assert.Equal(recipe, PageLogScope.RecipeOf(path));
    }
}
