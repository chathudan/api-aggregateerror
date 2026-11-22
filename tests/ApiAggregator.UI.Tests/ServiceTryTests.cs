using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace ApiAggregator.UI.Tests;

/// <summary>Services → Add an endpoint: send a test call before saving, and use its answer as the sample response.</summary>
[Collection(UiCollection.Name)]
public class ServiceTryTests(StackFixture stack)
{
    [UiFact]
    public async Task A_new_endpoint_can_be_tried_before_it_is_added_and_its_answer_becomes_the_sample()
    {
        var page = await stack.OpenAsync("/services");
        var service = page.Locator("#service-service-a");
        await service.GetByRole(AriaRole.Button, new() { Name = "Add endpoint" }).ClickAsync();
        var panel = service.Locator(".panel");

        await panel.GetByLabel("Name", new() { Exact = true }).FillAsync("Customer by id (trying)");
        await panel.GetByLabel("Path").FillAsync("/customers/{customerId}");
        await panel.GetByLabel("Test value for customerId").FillAsync("C-10042");
        await panel.GetByLabel("Test value for customerId").PressAsync("Tab");

        await panel.GetByRole(AriaRole.Button, new() { Name = "Send a test call" }).ClickAsync();

        // The call went out with Service A's sign-in; the page shows what was sent and the answer.
        await Expect(panel.Locator(".try-result")).ToContainTextAsync("Call worked", new() { Timeout = 15_000 });
        await Expect(panel.Locator("details.call-details")).ToContainTextAsync("/a/customers/C-10042");
        await Expect(panel.Locator("details.call-details")).ToContainTextAsync("Alex Example");

        await panel.GetByRole(AriaRole.Button, new() { Name = "Use this answer as the sample response" }).ClickAsync();
        await Expect(panel.GetByLabel("Sample response (optional, lets the wizard offer its fields)")).ToHaveValueAsync(new System.Text.RegularExpressions.Regex("C-10042"));

        // Nothing was saved: the endpoint isn't in the service's list.
        await panel.GetByRole(AriaRole.Button, new() { Name = "Cancel" }).ClickAsync();
        await Expect(service).Not.ToContainTextAsync("Customer by id (trying)");
    }
}
