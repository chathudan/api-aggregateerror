using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace ApiAggregator.UI.Tests;

/// <summary>
/// A technician builds and changes endpoints with the pickers only: no expression, no step name typed anywhere.
/// Everything typed goes through <see cref="Type"/>, and the tests check what was typed at the end.
/// </summary>
[Collection(UiCollection.Name)]
public class PickerJourneyTests(StackFixture stack)
{
    private readonly List<string> _typed = [];

    /// <summary>Types into a box and remembers the text, so a test can prove nothing like an expression was typed.</summary>
    private async Task Type(ILocator box, string text)
    {
        _typed.Add(text);
        await box.FillAsync(text);
        await box.PressAsync("Tab");
    }

    private void NothingLikeAnExpressionWasTyped(params string[] stepNames)
    {
        foreach (var text in _typed)
        {
            Assert.DoesNotContain("{{", text);
            Assert.DoesNotContain("[*]", text);
            Assert.DoesNotContain(".body", text);
            foreach (var alias in stepNames) Assert.False(text.Contains(alias, StringComparison.Ordinal), $"'{text}' contains the step name '{alias}'.");
        }
    }

    private static async Task<string> SelectedText(ILocator select) =>
        await select.EvaluateAsync<string>("e => e.options[e.selectedIndex].text");

    private static ILocator Step(IPage page, int index) => page.Locator(".step").Nth(index);

    [UiFact]
    public async Task Build_notes_for_each_contact_with_an_order_using_only_the_pickers()
    {
        var page = await stack.OpenAsync("/endpoints/new");
        await Type(page.GetByLabel("Name", new() { Exact = true }), "Notes for contacts with orders");
        await Type(page.GetByLabel("Route"), "contacts/with-orders");

        // Three steps from the CRM: contacts, orders, and the notes of one contact.
        foreach (var summary in new[] { "List contacts", "List orders", "Notes for one contact" })
        {
            await page.GetByRole(AriaRole.Button, new() { Name = "Add step" }).ClickAsync();
            await Type(page.GetByPlaceholder("Search endpoints"), summary);
            await page.GetByRole(AriaRole.Button, new() { Name = "Add", Exact = true }).First.ClickAsync();
        }
        var notes = Step(page, 2);
        await Expect(notes.Locator(".alias-line")).ToContainTextAsync("Name in expressions: notesForOneContact");

        // Run the notes step once for each order's contact.
        await notes.GetByLabel("Once for each item in a list").CheckAsync();
        var list = notes.GetByLabel("List that Notes for one contact goes through");
        await list.SelectOptionAsync(new SelectOptionValue { Label = "List orders → each order's contactId" });
        await Expect(notes.GetByLabel("Call once per unique item")).ToBeCheckedAsync(); // one call per contact, not per order

        // contactId is filled in with the current item, marked "suggested".
        var contactId = notes.GetByLabel("Value for contactId (path)");
        Assert.Equal("Current item (for each)", await SelectedText(contactId));
        await Expect(notes.Locator("table.bindings tbody tr", new() { HasText = "contactId" }).Locator(".tag.suggested")).ToBeVisibleAsync();

        // The answer: the contacts, and the notes from every call.
        await page.GetByRole(AriaRole.Button, new() { Name = "Add fields from List contacts" }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Add fields from Notes for one contact" }).ClickAsync();
        Assert.Equal("Notes for one contact → notes (from every call)", await SelectedText(page.GetByLabel("Value of notes")));

        var footer = page.Locator(".sticky-footer");
        await footer.GetByRole(AriaRole.Button, new() { Name = "Try it" }).ClickAsync();
        await Expect(footer).ToContainTextAsync("Test passed", new() { Timeout = 15_000 });
        await Expect(page.Locator(".field-issues li.problem")).ToHaveCountAsync(0);

        // Renaming the list the loop uses: asked first, then the loop follows it and the test still passes.
        var orders = Step(page, 1);
        await Type(orders.GetByLabel("Step name"), "Orders from CRM");
        await Expect(orders.Locator(".confirm")).ToContainTextAsync("Also rename it to ordersFromCrm in expressions? 1 place in this endpoint uses it");
        await orders.GetByRole(AriaRole.Button, new() { Name = "Rename everywhere" }).ClickAsync();
        await Expect(orders.Locator(".alias-line")).ToContainTextAsync("Name in expressions: ordersFromCrm");
        Assert.Equal("Orders from CRM → each order's contactId", await SelectedText(list));

        await footer.GetByRole(AriaRole.Button, new() { Name = "Try it" }).ClickAsync();
        await Expect(footer).ToContainTextAsync("Test passed", new() { Timeout = 15_000 });

        NothingLikeAnExpressionWasTyped("listContacts", "listOrders", "ordersFromCrm", "notesForOneContact");
    }

    [UiFact]
    public async Task Follow_the_guide_to_build_your_first_endpoint()
    {
        // From Help to a passing test, doing only what the guide says, with the pickers.
        var page = await stack.OpenAsync("/help");
        await page.GetByRole(AriaRole.Link, new() { Name = "Build your first endpoint with the guide" }).ClickAsync();
        var guide = page.Locator(".card.guide");
        await Expect(guide).ToContainTextAsync("(0 of 8 done)");
        await Expect(guide.Locator("li.current")).ToContainTextAsync("Name it and give it an address");

        await Type(page.GetByLabel("Name", new() { Exact = true }), "Contacts with notes");
        await Type(page.GetByLabel("Route"), "contacts/with-notes");
        await Expect(guide).ToContainTextAsync("(1 of 8 done)");

        foreach (var summary in new[] { "List contacts", "List orders", "Notes for one contact" })
        {
            await page.GetByRole(AriaRole.Button, new() { Name = "Add step" }).ClickAsync();
            await Type(page.GetByPlaceholder("Search endpoints"), summary);
            await page.GetByRole(AriaRole.Button, new() { Name = "Add", Exact = true }).First.ClickAsync();
        }
        await Expect(guide).ToContainTextAsync("(4 of 8 done)");
        await Expect(guide.Locator("li.current")).ToContainTextAsync("Call it once for each order's contact");

        var notes = Step(page, 2);
        await notes.GetByLabel("Once for each item in a list").CheckAsync();
        await notes.GetByLabel("List that Notes for one contact goes through").SelectOptionAsync(new SelectOptionValue { Label = "List orders → each order's contactId" });
        await Expect(guide).ToContainTextAsync("(6 of 8 done)"); // contactId ← Current item was suggested

        await page.GetByRole(AriaRole.Button, new() { Name = "Add fields from List contacts" }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Add fields from Notes for one contact" }).ClickAsync();
        await page.GetByLabel("What contacts returns of the list").SelectOptionAsync(new SelectOptionValue { Label = "Without repeats" });
        await Expect(guide).ToContainTextAsync("(7 of 8 done)"); // "by id" is picked for you

        var footer = page.Locator(".sticky-footer");
        await footer.GetByRole(AriaRole.Button, new() { Name = "Try it" }).ClickAsync();
        await Expect(footer).ToContainTextAsync("Test passed", new() { Timeout = 15_000 });
        await Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/endpoints/contacts-with-notes\\?guide=first"));
        await Expect(guide).ToContainTextAsync("(8 of 8 done)");
        await Expect(guide).ToContainTextAsync("All done");
        await Expect(page.Locator("#step-5 table")).ToContainTextAsync("×3"); // 6 orders, 3 contacts: 3 calls

        NothingLikeAnExpressionWasTyped("listContacts", "listOrders", "notesForOneContact");
    }

    [UiFact]
    public async Task Opening_a_saved_endpoint_shows_only_its_real_problems()
    {
        // The editor used to show the problems of the empty form it starts with (no id, no route, no steps) until the
        // next click. These endpoints have none. customer-summary sends a whole request body ("to": "body", no name
        // after it), which used to crash the page.
        foreach (var id in new[] { "customer-overview", "contacts-unique", "people-directory", "customer-summary" })
        {
            var page = await stack.OpenAsync($"/endpoints/{id}");
            await Expect(page.Locator(".stepper")).Not.ToContainTextAsync("to fix");
            await Expect(page.Locator(".field-issues li.problem")).ToHaveCountAsync(0);
            await Expect(page.Locator(".sticky-footer")).ToContainTextAsync("No problems found");
            if (id == "customer-summary") await Expect(page.Locator("table.bindings td", new() { HasTextString = "Whole body" })).ToHaveCountAsync(1);
            await page.CloseAsync();
        }
    }

    [UiFact]
    public async Task Renaming_a_step_nothing_uses_follows_its_display_name_without_asking()
    {
        var page = await stack.OpenAsync("/endpoints/new");
        await Type(page.GetByLabel("Name", new() { Exact = true }), "Rename check");
        await Type(page.GetByLabel("Route"), "rename/check");
        await page.GetByRole(AriaRole.Button, new() { Name = "Add step" }).ClickAsync();
        await Type(page.GetByPlaceholder("Search endpoints"), "List contacts");
        await page.GetByRole(AriaRole.Button, new() { Name = "Add", Exact = true }).First.ClickAsync();

        await Type(Step(page, 0).GetByLabel("Step name"), "Contacts from CRM");

        await Expect(Step(page, 0).Locator(".alias-line")).ToContainTextAsync("Name in expressions: contactsFromCrm");
        await Expect(page.Locator(".confirm")).ToHaveCountAsync(0);
    }

    [UiFact]
    public async Task Renaming_a_referenced_step_of_a_live_endpoint_updates_its_uses()
    {
        // people-directory is live; its "Account-to-user links" step (links) is the mapping of a combined list.
        var page = await stack.OpenAsync("/endpoints/people-directory");
        var links = Step(page, 3);
        await Expect(links.Locator(".alias-line")).ToContainTextAsync("Name in expressions: links");

        await Type(links.GetByLabel("Step name"), "Billing links");

        var confirm = links.Locator(".confirm");
        await Expect(confirm).ToContainTextAsync("Also rename it to billingLinks in expressions? 1 place in this endpoint uses it");
        await Expect(confirm).ToContainTextAsync("errors[].step"); // apps see step names in error details
        await links.GetByRole(AriaRole.Button, new() { Name = "Rename everywhere" }).ClickAsync();
        await Expect(links.Locator(".alias-line")).ToContainTextAsync("Name in expressions: billingLinks");
        await Expect(page.Locator(".field-issues li.problem")).ToHaveCountAsync(0); // the mapping now uses billingLinks

        var footer = page.Locator(".sticky-footer");
        await footer.GetByRole(AriaRole.Button, new() { Name = "Try it" }).ClickAsync();
        await Expect(footer).ToContainTextAsync("Test passed", new() { Timeout = 15_000 });
        await Expect(page.Locator("#step-5 table")).ToContainTextAsync("Billing links");

        // "Keep" leaves the name as it is.
        await Type(Step(page, 1).GetByLabel("Step name"), "CRM contacts");
        await Step(page, 1).GetByRole(AriaRole.Button, new() { Name = "Keep crm" }).ClickAsync();
        await Expect(Step(page, 1).Locator(".alias-line")).ToContainTextAsync("Name in expressions: crm");

        NothingLikeAnExpressionWasTyped("billingLinks", "crmContacts");
    }
}
