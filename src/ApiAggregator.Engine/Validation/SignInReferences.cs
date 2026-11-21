using ApiAggregator.Engine.Config;
using ApiAggregator.Engine.Model;

namespace ApiAggregator.Engine.Validation;

/// <summary>
/// Services and endpoints name their sign-in by id. One that names a sign-in that doesn't exist must never be called
/// without credentials as if it needed none: it's reported here, refused when publishing, and fails the step at run time.
/// </summary>
public static class SignInReferences
{
    /// <summary>The sign-in a call uses: the endpoint's own, else the service's. Null when neither names one.</summary>
    public static string? SignInId(ApiService service, ApiEndpoint endpoint) => endpoint.AuthProfileId ?? service.AuthProfileId;

    /// <summary>"Service A (customers) uses the sign-in 'x', which doesn't exist…", or null when the sign-in exists or none is named.</summary>
    public static string? Problem(ApiService service, ApiEndpoint endpoint, ConfigSnapshot config)
    {
        var id = SignInId(service, endpoint);
        if (id is null || config.GetAuthProfile(id) is not null) return null;
        var who = endpoint.AuthProfileId is null ? Name(service) : $"{Name(service)}, endpoint {endpoint.Id},";
        return $"{who} uses the sign-in '{id}', which doesn't exist. Set its sign-in on the Services page.";
    }

    /// <summary>Every service and endpoint that names a missing sign-in, as (where, message).</summary>
    public static IEnumerable<(string Where, string Message)> All(ConfigSnapshot config)
    {
        foreach (var service in config.Services.OrderBy(s => s.Id))
        {
            if (service.AuthProfileId is { } id && config.GetAuthProfile(id) is null)
                yield return ($"services.{service.Id}", $"{Name(service)} uses the sign-in '{id}', which doesn't exist. Set its sign-in on the Services page.");
            foreach (var endpoint in service.Endpoints.Where(e => e.AuthProfileId is not null))
                if (config.GetAuthProfile(endpoint.AuthProfileId) is null)
                    yield return ($"services.{service.Id}.{endpoint.Id}", Problem(service, endpoint, config)!);
        }
    }

    private static string Name(ApiService service) => string.IsNullOrWhiteSpace(service.Name) ? service.Id : service.Name;
}
