using ApiAggregator.Engine.Config;

namespace ApiAggregator.Admin.Services;

/// <summary>
/// One step of the guide: what to do, where on the page ("Section", the editor's anchor), what it means underneath for
/// those who want to know, and when it's done. Done is worked out from the form, so nothing has to be clicked to tick it.
/// </summary>
public sealed record GuideStep(string Title, string Text, string Underneath, string Section, Func<RecipeForm, bool, bool> Done);

/// <summary>
/// "Build your first endpoint": the editor's guided walkthrough (/endpoints/new?guide=first). It builds the sample
/// contacts-unique with the pickers only: contacts, their orders, and the notes of each contact with an order, called
/// once per contact. Uses the sample CRM (service F); without it the panel says so.
/// </summary>
public static class FirstEndpointGuide
{
    public const string Id = "first";

    private const string Contacts = "service-f-crm/listContacts";
    private const string Orders = "service-f-crm/listOrders";
    private const string Notes = "service-f-crm/getContactNotes";

    /// <summary>The guide needs the three CRM endpoints of the sample data.</summary>
    public static bool Available(ConfigSnapshot config) =>
        new[] { Contacts, Orders, Notes }.All(e => config.FindEndpoint(e, out _) is not null);

    public static readonly IReadOnlyList<GuideStep> Steps =
    [
        new("Name it and give it an address",
            "Type a name, for example Contacts with notes, and the route contacts/with-notes. Apps will call GET /bff/contacts/with-notes.",
            "The route is the address after /bff/. Values in braces, like {id}, would become inputs; this one has none.",
            "step-1",
            (form, _) => !string.IsNullOrWhiteSpace(form.Name) && !string.IsNullOrWhiteSpace(form.Route)),

        new("Add the contacts",
            "Choose Add step, search for List contacts and choose Add. This step gets every contact from the CRM.",
            "Each step calls one service endpoint. Its answer can be used by later steps and in the response.",
            "step-3",
            (form, _) => StepFor(form, Contacts) is not null),

        new("Add the orders",
            "Add step again: List orders. It gets every order; each order has the contactId of the contact who placed it.",
            "The two steps don't need each other, so the gateway calls them at the same time.",
            "step-3",
            (form, _) => StepFor(form, Orders) is not null),

        new("Add the notes of one contact",
            "Add step: Notes for one contact. It needs a contactId, so it's still empty: the next step fills it in.",
            "This endpoint is GET /contacts/{contactId}/notes. A value in braces must be filled before the call.",
            "step-3",
            (form, _) => StepFor(form, Notes) is not null),

        new("Call it once for each order's contact",
            "On the notes step, under Run this step, choose Once for each item in a list, then pick List orders → each order's contactId. Leave Call once per unique item ticked.",
            "It's saved as {{listOrders.body.orders[*].contactId}}: [*] means every order. With Call once per unique item, 6 orders from 3 contacts make 3 calls.",
            "step-3",
            (form, _) => LoopsOverContactIds(form)),

        new("Send the current contact",
            "contactId now shows Current item, marked suggested: the contactId of the order being called for. Keep it.",
            "Current item is saved as item. Each call gets the next contactId from the list.",
            "step-3",
            (form, _) => SendsCurrentItem(form)),

        new("Choose the answer",
            "In Response, choose Add fields from List contacts and Add fields from Notes for one contact. Next to contacts, choose Without repeats by id: the CRM lists one contact twice.",
            "The notes come from every call (notes[*].body.notes). Without repeats is saved as | distinct('id').",
            "step-4",
            (form, _) => HasAnswer(form)),

        new("Try it",
            "Choose Try it in the footer. In the results, the notes step's call ends with ×3: three calls, one per contact. To make it live, a publisher chooses Publish.",
            "Try it runs the draft against the services, exactly as the gateway would, and shows each call.",
            "step-5",
            (_, testPassed) => testPassed)
    ];

    /// <summary>How many steps are done, and the first one that isn't (null when all are).</summary>
    public static (int Done, int? Current) Progress(RecipeForm form, bool testPassed)
    {
        var done = Steps.Select(s => s.Done(form, testPassed)).ToList();
        var current = done.IndexOf(false);
        return (done.Count(d => d), current < 0 ? null : current);
    }

    private static StepForm? StepFor(RecipeForm form, string endpoint) =>
        form.Steps.FirstOrDefault(s => string.Equals(s.Endpoint, endpoint, StringComparison.OrdinalIgnoreCase));

    private static bool LoopsOverContactIds(RecipeForm form) =>
        StepFor(form, Notes) is { } notes && StepFor(form, Orders) is { } orders
        && StepChoices.LoopExpression(notes.ForEach) == $"{orders.Alias}.body.orders[*].contactId";

    private static bool SendsCurrentItem(RecipeForm form) =>
        StepFor(form, Notes) is { } notes && notes.Bindings.TryGetValue("path.contactId", out var b)
        && b.Mode == BindingMode.FromSource && b.Expression == "item";

    private static bool HasAnswer(RecipeForm form)
    {
        if (StepFor(form, Contacts) is not { } contacts || StepFor(form, Notes) is not { } notes) return false;
        var values = form.Response.Select(f => ResponseShapes.Split(f.Expression)).OfType<ShapedValue>().ToList();
        return values.Any(v => v.Value == $"{contacts.Alias}.body.contacts" && v.Shape == ResponseShape.Distinct && v.By == "id")
            && values.Any(v => v.Value.StartsWith($"{notes.Alias}[*].body", StringComparison.Ordinal));
    }
}
