using ApiAggregator.Engine.Logging;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Server.Circuits;

namespace ApiAggregator.Admin.Hosting;

/// <summary>
/// Tags every log line written while a page handles a click, a navigation or its first render with the page and, in
/// the endpoint editor, the endpoint being edited. So an error on the Logs page says "/endpoints/customer-summary"
/// instead of only "IndexOutOfRangeException". Path only: the query and fragment are dropped. Admin paths hold
/// configuration ids (endpoints, services), never caller data.
/// </summary>
public sealed class PageLogScope(NavigationManager nav, ILogger<PageLogScope> logger) : CircuitHandler
{
    public override Func<CircuitInboundActivityContext, Task> CreateInboundActivityHandler(Func<CircuitInboundActivityContext, Task> next) =>
        async context =>
        {
            // Read when the line is written, not now: a navigation changes the page while it is handled.
            var page = new Lazy(() => PageOf(CurrentPath()));
            var recipe = new Lazy(() => RecipeOf(CurrentPath()));
            using var scope = logger.BeginScope(new LogScope(("Page", page), ("RecipeId", recipe)));
            await next(context);
        };

    /// <summary>"endpoints/customer-summary?guide=first#step-3" → "/endpoints/customer-summary".</summary>
    public static string PageOf(string? relativePath)
    {
        var path = relativePath ?? "";
        var cut = path.IndexOfAny(['?', '#']);
        return "/" + (cut < 0 ? path : path[..cut]).Trim('/');
    }

    /// <summary>The endpoint the editor has open ("/endpoints/customer-summary" → customer-summary), or null.</summary>
    public static string? RecipeOf(string? relativePath) =>
        PageOf(relativePath).Split('/', StringSplitOptions.RemoveEmptyEntries) is ["endpoints", var id] && id != "new" ? id : null;

    private string? CurrentPath()
    {
        try { return nav.ToBaseRelativePath(nav.Uri); }
        catch (InvalidOperationException) { return null; } // not set up yet (the circuit is still starting)
    }

    /// <summary>A value worked out when the log line asks for it (the live log reads scope values with ToString).</summary>
    private sealed class Lazy(Func<string?> read)
    {
        public override string? ToString() => read();
    }
}
