using System.Security.Claims;

namespace ApiAggregator.Admin.Application;

/// <summary>
/// Who is asking, and what they may do. Built from the signed-in person (pages, cookie) or from a bearer token
/// (/admin/*); the service never sees which. Roles:
///   author     add services and sign-ins, build and test drafts
///   publisher  everything an author can do, plus publish, roll back and delete endpoints
///   admin      change installation settings such as browser access (CORS, ADR 0015). A separate role: it doesn't
///              make someone an author or publisher, and authors and publishers don't get it
/// </summary>
public sealed record AdminActor(string? Subject, string? DisplayName, bool IsAuthor, bool IsPublisher, bool IsAdmin = false)
{
    /// <summary>How the person is working (ADR 0020): a page (the default), the /admin/* API, or MCP. The host sets it.</summary>
    public ApiAggregator.Engine.Config.ChangeChannel Via { get; init; } = ApiAggregator.Engine.Config.ChangeChannel.Page;

    /// <summary>The name an MCP client gives itself, e.g. "Claude Code". A label for the activity log; never used to decide anything.</summary>
    public string? Client { get; init; }

    /// <summary>Who and how, for the activity log and history.</summary>
    public ApiAggregator.Engine.Config.ChangeSource Source => new(Subject, DisplayName, Via, Client);

    public const string AuthorRole = "aggregator.author";
    public const string PublisherRole = "aggregator.publisher";
    public const string AdminRole = "aggregator.admin";

    /// <summary>
    /// Lets a token use the MCP server at /mcp (ADR 0021). Not a role: an administrator grants it to the tokens an AI
    /// agent may use, and the roles above still decide what the agent can do.
    /// </summary>
    public const string McpScope = "aggregator.mcp";

    /// <summary>Nobody signed in: may do nothing.</summary>
    public static AdminActor Anonymous { get; } = new(null, null, false, false);

    /// <summary>Has any role at all, so may look at installation settings.</summary>
    public bool HasAnyRole => IsAuthor || IsAdmin;

    /// <summary>
    /// Reads the roles from the scopes of a token (<c>scope</c> or <c>scp</c>, space separated) or from role claims
    /// (<c>roles</c>, <c>role</c> or the .NET role type), so access tokens and app roles from any identity provider work.
    /// A publisher is always an author too. The admin role stands on its own.
    /// </summary>
    public static AdminActor From(ClaimsPrincipal? user)
    {
        if (user?.Identity?.IsAuthenticated != true) return Anonymous;

        var granted = Granted(user);
        var publisher = granted.Contains(PublisherRole);
        var author = publisher || granted.Contains(AuthorRole);

        var subject = user.FindFirst("sub")?.Value ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var name = user.FindFirst("name")?.Value ?? user.FindFirst(ClaimTypes.Name)?.Value;
        return new AdminActor(subject, string.IsNullOrWhiteSpace(name) ? null : name, author, publisher, granted.Contains(AdminRole));
    }

    /// <summary>Whether the signed-in token or person has this scope or role.</summary>
    public static bool Has(ClaimsPrincipal? user, string scopeOrRole) =>
        user?.Identity?.IsAuthenticated == true && Granted(user).Contains(scopeOrRole);

    private static HashSet<string> Granted(ClaimsPrincipal user) => user.Claims
        .Where(c => c.Type is "scope" or "scp" or "roles" or "role" or ClaimTypes.Role)
        .SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        .ToHashSet(StringComparer.Ordinal);

    /// <summary>How the person shows up in the UI: their name, or their id when the sign-in has no name.</summary>
    public string Label => DisplayName ?? Subject ?? "unknown";
}
