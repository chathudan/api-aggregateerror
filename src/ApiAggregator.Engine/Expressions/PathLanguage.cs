namespace ApiAggregator.Engine.Expressions;

/// <summary>
/// The whole value language of a recipe, as data (ADR 0002, ADR 0019): the roots a path starts from, the syntax, the
/// fixed transforms, templates and conditions. Shown to people and to AI agents so they write valid expressions the
/// first time instead of guessing. Nothing here can run code: it describes what <see cref="ValuePath"/>,
/// <see cref="Template"/> and the conditions already accept.
/// </summary>
public static class PathLanguage
{
    public sealed record Entry(string Example, string Meaning);

    public sealed record Guide(
        IReadOnlyList<Entry> Roots,
        IReadOnlyList<Entry> Syntax,
        IReadOnlyList<TransformInfo> Transforms,
        IReadOnlyList<Entry> Templates,
        IReadOnlyList<Entry> Conditions,
        IReadOnlyList<Entry> BindingTargets,
        IReadOnlyList<string> Rules);

    public static readonly Guide Described = new(
        Roots:
        [
            new("request.route.id", "A declared input of the endpoint: request.route.<name>, request.query.<name> or request.header.<name>. Only declared inputs exist."),
            new("a.body / a.status / a.headers['ETag']", "What step 'a' got back: its JSON body, its HTTP status, a response header. Steps are named by their alias."),
            new("item / index", "Inside a step that runs for each item: the current item and its position (from 0)."),
            new("combine.people", "A combined list (Combine lists), in the response only."),
            new("ctx.correlationId / ctx.subject", "The request's correlation id, and the caller's subject.")
        ],
        Syntax:
        [
            new("a.body.customer.name", "A property, then a property inside it."),
            new("a.body.items[0] / a.body.items[-1]", "An item by position; negative counts from the end."),
            new("a.body.items[0:5]", "A slice: items 0 to 4."),
            new("a.body.items[*].id", "The 'id' of every item, as a list."),
            new("a.headers['X-Trace-Id']", "A name with characters other than letters, digits and _, in quotes."),
            new("a.body.ids | join(',')", "A transform after |, from the fixed list. Several can follow each other.")
        ],
        Transforms: Expressions.Transforms.Described,
        Templates:
        [
            new("\"{{a.body}}\"", "A whole text that is one expression keeps the value's JSON type (object, list, number)."),
            new("\"Customer {{a.body.name}}\"", "Text around an expression makes a text."),
            new("{ \"customer\": \"{{a.body}}\", \"orders\": \"{{b.body.items}}\" }", "The response, and request bodies, are JSON templates: any JSON with {{ }} in its texts.")
        ],
        Conditions:
        [
            new("{{a.body.isPremium}}", "True when the value is true, a non-zero number or a non-empty text other than 'false' and '0'."),
            new("{{a.body.status}} == active", "Equal, comparing text without case."),
            new("{{a.body.status}} != closed", "Not equal.")
        ],
        BindingTargets:
        [
            new("path.customerId", "A value in the endpoint's path."),
            new("query.status", "A query string value."),
            new("header.X-Customer-Id", "A request header. Authorization, Cookie, Host and hop-by-hop headers can't be set: the sign-in does that."),
            new("cookie.name", "A cookie."),
            new("body / body.customer.id", "The whole JSON body, or one field of it (with a template).")
        ],
        Rules:
        [
            "There is no scripting: only these paths, transforms, templates and conditions.",
            "Steps run in parallel unless one uses another's value; the order comes from the values used.",
            "A value from a step that failed or was skipped is null (an empty list for [*] paths).",
            "Combine lists merge records by exact key only; never by similar names, emails or dates of birth."
        ]);
}
