using System.Security.Claims;

namespace ApiAggregator.Admin.Application;

/// <summary>
/// Who is asking, and what they may do. Built from the signed-in person (pages, cookie) or from a bearer token
/// (/admin/*); the service never sees which. Roles:
///   author     add services and sign-ins, build and test drafts
///   publisher  everything an author can do, plus publish, roll back and delete endpoints
/// </summary>
public sealed record AdminActor(string? Subject, string? DisplayName, bool IsAuthor, bool IsPublisher)
{
    public const string AuthorRole = "aggregator.author";
    public const string PublisherRole = "aggregator.publisher";

    /// <summary>Nobody signed in: may do nothing.</summary>
    public static AdminActor Anonymous { get; } = new(null, null, false, false);

    /// <summary>
    /// Reads the roles from the scopes of a token (<c>scope</c> or <c>scp</c>, space separated) or from role claims
    /// (<c>roles</c>, <c>role</c> or the .NET role type), so access tokens and app roles from any identity provider work.
    /// A publisher is always an author too.
    /// </summary>
    public static AdminActor From(ClaimsPrincipal? user)
    {
        if (user?.Identity?.IsAuthenticated != true) return Anonymous;

        var granted = user.Claims
            .Where(c => c.Type is "scope" or "scp" or "roles" or "role" or ClaimTypes.Role)
            .SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .ToHashSet(StringComparer.Ordinal);
        var publisher = granted.Contains(PublisherRole);
        var author = publisher || granted.Contains(AuthorRole);

        var subject = user.FindFirst("sub")?.Value ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var name = user.FindFirst("name")?.Value ?? user.FindFirst(ClaimTypes.Name)?.Value;
        return new AdminActor(subject, string.IsNullOrWhiteSpace(name) ? null : name, author, publisher);
    }

    /// <summary>How the person shows up in the UI: their name, or their id when the sign-in has no name.</summary>
    public string Label => DisplayName ?? Subject ?? "unknown";
}
