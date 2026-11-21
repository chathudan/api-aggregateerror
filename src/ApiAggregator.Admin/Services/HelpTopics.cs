using ApiAggregator.Admin.Components.Help;

namespace ApiAggregator.Admin.Services;

/// <summary>One short Help page: /help/{Id}. Task pages are what people come to do; the rest is reference.</summary>
public sealed record HelpTopic(string Id, string Title, string Summary, string Guide, Type Component, bool IsTask);

/// <summary>
/// The Help pages, in the order the Help menu lists them. Each page mirrors a guide in docs/guides (linked as its
/// "Full guide"), which keeps the long form and the checked examples.
/// </summary>
public static class HelpTopics
{
    public static readonly IReadOnlyList<HelpTopic> All =
    [
        new("add-service", "Add a service", "Connect a backend API once: its address, its endpoints and how the gateway signs in to it.", "adding-services", typeof(HelpAddService), true),
        new("build", "Build an endpoint", "Combine several service calls into one endpoint that apps call.", "building-endpoints", typeof(HelpBuild), true),
        new("publish", "Test and publish", "Try it, publish, look back at earlier versions, roll back, discard or delete.", "testing-and-publishing", typeof(HelpPublish), true),
        new("troubleshooting", "Fix a problem", "Each message you may see, what it means and what to do.", "troubleshooting", typeof(HelpTroubleshooting), true),

        new("start", "How it works and roles", "What the gateway does, and what authors, publishers and admins can do.", "getting-started", typeof(HelpStart), false),
        new("loops", "Run for each item, conditions and step names", "Call a service once per item of a list, skip a step, and rename steps safely.", "building-endpoints#run-for-each-item", typeof(HelpLoops), false),
        new("response", "The response", "Choose what apps get back: fields, how many, without repeats.", "building-endpoints#the-response", typeof(HelpResponse), false),
        new("combine", "Combine lists", "One list with each record once, from several services, without guessing.", "combine-lists", typeof(HelpCombine), false),
        new("app-errors", "Errors apps receive", "The codes apps get when something goes wrong, and who fixes them.", "for-app-developers#error-codes", typeof(HelpAppErrors), false),
        new("logs", "Live logs", "Follow what the gateway does, and find one request by its correlation id.", "troubleshooting#find-one-request", typeof(HelpLogs), false),
        new("browser-access", "Browser access (CORS)", "Which websites may call the gateway from a browser.", "for-app-developers#calling-from-a-browser", typeof(HelpBrowserAccess), false),
        new("api-docs", "API docs for testers", "Call published endpoints from the API docs, Postman or Insomnia.", "testing-and-publishing#test-through-the-api-docs-swagger", typeof(HelpApiDocs), false),
        new("words", "Words used here", "Service, step, sign-in, scope and the other words on these pages.", "glossary", typeof(HelpWords), false)
    ];

    /// <summary>Topics that became a part of another page: the old name still lands in the right place.</summary>
    private static readonly Dictionary<string, string> Moved = new(StringComparer.OrdinalIgnoreCase)
    {
        ["before"] = "add-service#before",
        ["sign-in"] = "add-service#sign-in",
        ["secrets"] = "add-service#secrets",
        ["add-endpoint"] = "add-service#by-hand",
        ["signing-in"] = "start#roles",
        ["history"] = "publish#history",
        ["delete"] = "publish#delete"
    };

    public static HelpTopic? Find(string? id) => All.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The address of a topic ("build", or a moved one such as "history"), or null when there's no such topic.</summary>
    public static string? Href(string? topic)
    {
        if (string.IsNullOrWhiteSpace(topic)) return "/help";
        if (Moved.TryGetValue(topic, out var moved)) return $"/help/{moved}";
        var hash = topic.IndexOf('#');
        var id = hash < 0 ? topic : topic[..hash];
        return Find(id) is null ? null : $"/help/{topic}";
    }
}
