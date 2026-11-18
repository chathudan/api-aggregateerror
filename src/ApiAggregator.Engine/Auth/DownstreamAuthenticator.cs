using ApiAggregator.Engine.Model;

namespace ApiAggregator.Engine.Auth;

/// <summary>Runs every step of an auth profile, in order, against an outgoing request.</summary>
public sealed class DownstreamAuthenticator(
    IEnumerable<IAuthStep> steps,
    ISecretResolver secrets,
    DownstreamCredentialCache cache,
    IHttpClientFactory httpClientFactory)
{
    private readonly Dictionary<string, IAuthStep> _steps = steps.ToDictionary(s => s.Kind, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<string> Kinds => _steps.Keys;

    public async ValueTask ApplyAsync(AuthProfile? profile, HttpRequestMessage request, CallerContext caller, CancellationToken ct)
    {
        if (profile is null) return;

        for (var i = 0; i < profile.Steps.Count; i++)
        {
            var def = profile.Steps[i];
            if (!_steps.TryGetValue(def.Kind, out var step))
                throw new AuthFailedException($"Auth profile '{profile.Id}' uses unknown kind '{def.Kind}'.");

            await step.ApplyAsync(new AuthStepContext
            {
                Request = request,
                Definition = def,
                ProfileId = profile.Id,
                StepIndex = i,
                Caller = caller,
                Secrets = secrets,
                Cache = cache,
                HttpClientFactory = httpClientFactory
            }, ct);
        }
    }

    public void Invalidate(AuthProfile? profile)
    {
        if (profile is not null) cache.InvalidateProfile(profile.Id, profile.Steps.Count);
    }

    /// <summary>Plain-English problems with a profile (unknown kind, missing setting). Used by the admin API.</summary>
    public IEnumerable<string> Check(AuthProfile profile)
    {
        foreach (var (def, i) in profile.Steps.Select((d, i) => (d, i)))
        {
            if (!_steps.TryGetValue(def.Kind, out var step)) { yield return $"Step {i + 1}: unknown kind '{def.Kind}'. Use one of {string.Join(", ", Kinds)}."; continue; }
            foreach (var name in step.RequiredSettings.Where(n => !def.Settings.ContainsKey(n)))
                yield return $"Step {i + 1} ({def.Kind}): setting '{name}' is required.";
            foreach (var (name, value) in def.Settings)
                if (LooksLikeSecretName(name) && !value.StartsWith("secret:", StringComparison.OrdinalIgnoreCase))
                    yield return $"Step {i + 1} ({def.Kind}): '{name}' must be a secret reference like secret:my-service-password, not the value itself.";
        }
    }

    private static bool LooksLikeSecretName(string name) =>
        name.Contains("password", StringComparison.OrdinalIgnoreCase)
        || name.Contains("secret", StringComparison.OrdinalIgnoreCase)
        || name.Equals("token", StringComparison.OrdinalIgnoreCase)
        || name.Equals("value", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Adds cookies to an outgoing request without losing ones already set by bindings.</summary>
public static class RequestCookies
{
    public static void Add(HttpRequestMessage request, string name, string value)
    {
        var existing = request.Headers.TryGetValues("Cookie", out var values) ? string.Join("; ", values) : null;
        request.Headers.Remove("Cookie");
        request.Headers.TryAddWithoutValidation("Cookie", string.IsNullOrEmpty(existing) ? $"{name}={value}" : $"{existing}; {name}={value}");
    }
}
