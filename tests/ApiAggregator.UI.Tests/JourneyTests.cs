using System.Net;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace ApiAggregator.UI.Tests;

/// <summary>The main things technicians and QA do, start to finish, in a real browser against the real apps.</summary>
[Collection(UiCollection.Name)]
public class JourneyTests(StackFixture stack)
{
    [UiFact]
    public async Task Add_a_service_from_its_OpenAPI_spec()
    {
        var page = await stack.OpenAsync("/services");

        await page.GetByRole(AriaRole.Button, new() { Name = "Add service", Exact = true }).ClickAsync();
        await page.GetByLabel("Spec address").FillAsync($"http://localhost:{stack.MockPort}/d/openapi.json");
        await page.GetByRole(AriaRole.Button, new() { Name = "Load", Exact = true }).ClickAsync();

        // Stage 2: filled in from the spec.
        await Expect(page.GetByText("Operations to import")).ToBeVisibleAsync();
        Assert.Equal("Service D (billing)", await page.GetByLabel("Name", new() { Exact = true }).InputValueAsync());
        Assert.Equal($"http://localhost:{stack.MockPort}/d", await page.GetByLabel("Base address").InputValueAsync());

        // Stage 3: the spec says "API key in the X-Api-Key header"; only the key itself is left to type.
        await page.GetByRole(AriaRole.Button, new() { Name = "Next" }).ClickAsync();
        Assert.Equal("X-Api-Key", await page.GetByLabel("Key name").InputValueAsync());
        await page.GetByLabel("Key value").First.FillAsync("key-d");
        await page.GetByRole(AriaRole.Button, new() { Name = "Save and check sign-in" }).ClickAsync();

        await Expect(page.Locator(".toast").First).ToContainTextAsync("Service D (billing) added with 2 endpoint(s)");
        var card = page.Locator("section.card.service", new() { HasText = "Service D (billing)" });
        await Expect(card.Locator(".pill").First).ToContainTextAsync("Ready");
        await Expect(card.GetByRole(AriaRole.Cell, new() { Name = "List invoices for a customer", Exact = true })).ToBeVisibleAsync(); // its endpoints are shown straight away
    }

    [UiFact]
    public async Task Build_test_and_publish_an_endpoint()
    {
        var page = await stack.OpenAsync("/endpoints/new");
        Assert.Equal(0, await page.Locator(".field-issues li").CountAsync()); // no red text before typing

        // 1. Endpoint: the id and the route input are filled in for you.
        await page.GetByLabel("Name", new() { Exact = true }).FillAsync("Loyalty check");
        await page.GetByLabel("Name", new() { Exact = true }).PressAsync("Tab");
        await page.GetByLabel("Route").FillAsync("customer/{id}/loyalty-check");
        await page.GetByLabel("Route").PressAsync("Tab");
        await Expect(page.GetByLabel("Id")).ToHaveValueAsync("loyalty-check");
        await Expect(page.GetByLabel("Input name")).ToHaveValueAsync("id");
        await page.GetByLabel("Test value for Try it").FillAsync("C-10042");

        // 3. Steps: pick an endpoint; its customerId is pre-filled from the route.
        await page.GetByRole(AriaRole.Button, new() { Name = "Add step" }).ClickAsync();
        await page.GetByPlaceholder("Search endpoints").FillAsync("loyalty");
        await page.GetByRole(AriaRole.Button, new() { Name = "Add", Exact = true }).First.ClickAsync();
        Assert.Equal("Request → id", await page.GetByLabel("Value for customerId (path)").EvaluateAsync<string>("e => e.options[e.selectedIndex].text"));

        // 4. Response, then 5. Try it: the primary button moves from Try it to Publish.
        await page.GetByRole(AriaRole.Button, new() { Name = "Add fields from Loyalty points for a customer" }).ClickAsync();
        var footer = page.Locator(".sticky-footer");
        await footer.GetByRole(AriaRole.Button, new() { Name = "Try it" }).ClickAsync();
        await Expect(footer).ToContainTextAsync("Test passed");
        await Expect(footer.Locator(".button.primary")).ToHaveTextAsync("Publish");

        await footer.GetByRole(AriaRole.Button, new() { Name = "Publish", Exact = true }).ClickAsync();
        await Expect(page.Locator(".toast").First).ToContainTextAsync("Published. Callers can use GET /bff/customer/{id}/loyalty-check now.");
        await Expect(page.Locator(".stepper li").Nth(4)).ToContainTextAsync("live v1");

        // The gateway serves it within its config check (2 seconds).
        var (status, body) = await Poll(() => stack.CallGatewayAsync("/bff/customer/C-10042/loyalty-check", "customer.read"), r => r.Status == HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1250, body!["points"]!.GetValue<int>());
    }

    [UiFact]
    public async Task See_the_logs_for_a_test_run()
    {
        var page = await stack.OpenAsync("/endpoints/customer-profile");
        await page.GetByLabel("Test value for Try it").First.FillAsync("C-10042");
        await page.Locator(".sticky-footer").GetByRole(AriaRole.Button, new() { Name = "Try it" }).ClickAsync();
        await Expect(page.Locator(".sticky-footer")).ToContainTextAsync("Test passed");

        await page.GetByRole(AriaRole.Link, new() { Name = "See logs for this test" }).ClickAsync();

        await Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/logs\\?correlation="));
        await Expect(page.Locator(".tag.chip")).ToContainTextAsync("Correlation:");
        await Expect(page.Locator(".log-row").First).ToBeVisibleAsync(new() { Timeout = 20_000 });
        await Expect(page.Locator(".log-table")).ToContainTextAsync("customer-profile");
    }

    [UiFact]
    public async Task Let_a_website_call_the_gateway_from_a_browser_then_take_it_away()
    {
        const string origin = "http://portal.localhost.test:4200"; // plain http is fine in Development
        var page = await stack.OpenAsync("/settings/browser-access");

        await page.GetByRole(AriaRole.Button, new() { Name = "Add origin", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Textbox, new() { Name = "Origin", Exact = true }).FillAsync(origin);
        await page.GetByLabel("Note").FillAsync("UI test portal");
        await page.GetByRole(AriaRole.Button, new() { Name = "Add origin", Exact = true }).ClickAsync();
        await Expect(page.Locator(".toast").First).ToContainTextAsync($"Added {origin}");
        var list = page.GetByRole(AriaRole.Region, new() { Name = "Allowed origins" });
        await Expect(list.GetByRole(AriaRole.Row, new() { Name = origin })).ToContainTextAsync("Added here");

        // The gateway answers the browser's preflight for it, without a restart.
        var allowed = await Poll(() => PreflightAllowsAsync(origin), ok => ok);
        Assert.True(allowed, "The gateway should allow the new origin within a few seconds.\n" + stack.LogOf("ApiAggregator.Gateway"));

        await page.GetByLabel("Origin to test").FillAsync(origin);
        await page.GetByRole(AriaRole.Button, new() { Name = "Test", Exact = true }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Status).Filter(new() { HasText = "Allowed" })).ToContainTextAsync($"Added here by {StackFixture.TesterName}");

        // Remove asks first, and the history keeps both changes.
        await list.GetByRole(AriaRole.Row, new() { Name = origin }).GetByRole(AriaRole.Button, new() { Name = "Remove" }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Remove origin" }).ClickAsync();
        await Expect(page.Locator(".toast", new() { HasText = $"Removed {origin}" })).ToBeVisibleAsync();
        var history = page.Locator("section", new() { Has = page.GetByRole(AriaRole.Heading, new() { Name = "History" }) });
        await Expect(history).ToContainTextAsync($"Removed {origin}");
        await Expect(history).ToContainTextAsync($"Added {origin}");

        Assert.False(await Poll(() => PreflightAllowsAsync(origin), ok => !ok), "The gateway should stop allowing the origin.");
    }

    private async Task<bool> PreflightAllowsAsync(string origin)
    {
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Options, stack.GatewayUrl + "/bff/customer/C-10042/profile");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "GET");
        using var response = await http.SendAsync(request);
        return response.Headers.TryGetValues("Access-Control-Allow-Origin", out var values) && values.Single() == origin;
    }

    private static async Task<T> Poll<T>(Func<Task<T>> call, Func<T, bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (true)
        {
            var result = await call();
            if (done(result) || DateTime.UtcNow > deadline) return result;
            await Task.Delay(300);
        }
    }
}
