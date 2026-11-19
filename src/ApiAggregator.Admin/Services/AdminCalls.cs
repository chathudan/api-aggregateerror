using ApiAggregator.Admin.Application;

namespace ApiAggregator.Admin.Services;

/// <summary>Short forms for pages: the value of a use case, or its plain-English error.</summary>
public static class AdminCalls
{
    /// <summary>The value, or null when the use case refused (the page shows its empty or error state).</summary>
    public static async Task<T?> ValueAsync<T>(this Task<AdminResult<T>> call) => (await call).Value;

    /// <summary>Null when it worked, otherwise the message to show.</summary>
    public static async Task<string?> ErrorAsync<T>(this Task<AdminResult<T>> call) => (await call).Failure?.Message;
}
