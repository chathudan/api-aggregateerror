namespace ApiAggregator.Admin.Application;

/// <summary>Why a use case didn't go ahead. The /admin/* wrapper turns each into its HTTP status.</summary>
public enum FailureKind { BadRequest, NotFound, Conflict, Forbidden }

/// <summary>
/// A refusal in plain English. <see cref="Title"/> is what people read; <see cref="Extra"/> holds additional
/// fields of the error body (for example the validation issues, or the endpoints that use a service), in order.
/// A null title means "no body", as some endpoints answer today.
/// </summary>
public sealed record AdminFailure(FailureKind Kind, string? Title, IReadOnlyList<KeyValuePair<string, object?>>? Extra = null)
{
    /// <summary>
    /// One line for a page: the title, then any messages in the extra fields (issues, errors). The same text the
    /// old HTTP client built from an error body.
    /// </summary>
    public string Message
    {
        get
        {
            var title = Title ?? Kind switch
            {
                FailureKind.NotFound => "That wasn't found.",
                FailureKind.Forbidden => "You don't have permission to do this.",
                _ => "The request was refused."
            };
            var details = (Extra ?? [])
                .Where(e => e.Key is "issues" or "errors")
                .SelectMany(e => (e.Value as System.Collections.IEnumerable ?? Array.Empty<object>()).Cast<object?>())
                .Select(i => i switch
                {
                    ApiAggregator.Engine.Validation.ValidationIssue v => v.Message,
                    null => null,
                    _ => i.ToString()
                })
                .Where(m => !string.IsNullOrEmpty(m))
                .ToList();
            return details.Count == 0 ? title : $"{title} {string.Join(" ", details)}";
        }
    }
}

/// <summary>The outcome of a use case: a value, or a failure. Never both.</summary>
public readonly record struct AdminResult<T>
{
    public T? Value { get; }
    public AdminFailure? Failure { get; }
    public bool Ok => Failure is null;

    private AdminResult(T? value, AdminFailure? failure) { Value = value; Failure = failure; }

    public static AdminResult<T> Success(T value) => new(value, null);
    public static implicit operator AdminResult<T>(T value) => new(value, null);
    public static implicit operator AdminResult<T>(AdminFailure failure) => new(default, failure);
}

/// <summary>Short ways to say no.</summary>
public static class Refuse
{
    public static AdminFailure BadRequest(string? title, params (string Key, object? Value)[] extra) => new(FailureKind.BadRequest, title, Pairs(extra));
    public static AdminFailure NotFound(string? title = null) => new(FailureKind.NotFound, title);
    public static AdminFailure Conflict(string title, params (string Key, object? Value)[] extra) => new(FailureKind.Conflict, title, Pairs(extra));
    public static AdminFailure Forbidden(string title) => new(FailureKind.Forbidden, title);

    private static IReadOnlyList<KeyValuePair<string, object?>>? Pairs((string Key, object? Value)[] extra) =>
        extra.Length == 0 ? null : extra.Select(e => new KeyValuePair<string, object?>(e.Key, e.Value)).ToList();
}
