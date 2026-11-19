using ApiAggregator.Engine.Model;

namespace ApiAggregator.Admin.Services;

/// <summary>One field on the sign-in form, described in plain words.</summary>
public sealed record SignInField(string Setting, string Label, string? Placeholder = null, bool Secret = false, bool Required = true, bool Advanced = false, string[]? Options = null);

/// <summary>A sign-in type the user can pick, mapped to an engine auth kind.</summary>
public sealed record SignInKind(string Kind, string Title, string Description, SignInField[] Fields);

/// <summary>The friendly catalogue behind the sign-in picker. Add a row here when the engine gets a new kind.</summary>
public static class SignInKinds
{
    public static readonly SignInKind[] All =
    [
        new("Basic", "Username and password", "The service asks for a username and password on every call (HTTP Basic).",
        [
            new("username", "Username", "svc-user"),
            new("password", "Password", Secret: true)
        ]),
        new("SessionLogin", "Log in first, then reuse the session", "The gateway logs in once, keeps the session cookie and/or token, and logs in again when it expires.",
        [
            new("loginUrl", "Login address", "https://service.example/auth/login"),
            new("username", "Username"),
            new("password", "Password", Secret: true),
            new("format", "Send login as", Options: ["json", "form"], Required: false),
            new("tokenPath", "Token in the login response (if any)", "token or data.accessToken", Required: false),
            new("usernameField", "Username field name", "username", Required: false, Advanced: true),
            new("passwordField", "Password field name", "password", Required: false, Advanced: true),
            new("loginUsesBasic", "Log in with Basic auth instead of a body", Options: ["false", "true"], Required: false, Advanced: true),
            new("tokenHeader", "Send the token in header", "Authorization", Required: false, Advanced: true),
            new("ttlSeconds", "Reuse the session for (seconds)", "900", Required: false, Advanced: true)
        ]),
        new("OAuthClientCredentials", "OAuth client (JWT token)", "The gateway gets an access token with a client id and secret, and refreshes it before it expires.",
        [
            new("tokenUrl", "Token address", "https://login.example/oauth2/token"),
            new("clientId", "Client id"),
            new("clientSecret", "Client secret", Secret: true),
            new("scope", "Scope", "api.read", Required: false),
            new("audience", "Audience", Required: false, Advanced: true)
        ]),
        new("ApiKey", "API key", "A fixed key sent with every call.",
        [
            new("name", "Key name", "X-Api-Key"),
            new("value", "Key value", Secret: true),
            new("in", "Send it in", Options: ["header", "query"], Required: false)
        ]),
        new("StaticBearer", "Fixed token", "A long-lived token or JWT issued to the gateway.",
        [
            new("token", "Token", Secret: true),
            new("header", "Header", "Authorization", Required: false, Advanced: true)
        ]),
        new("PassThroughUserToken", "Use the caller's own token", "Forward the app user's token. Only for services that trust the same sign-in provider.", [])
    ];

    public static SignInKind Get(string kind) => All.FirstOrDefault(k => k.Kind == kind) ?? All[0];
}

/// <summary>Editable sign-in for one service: one or more steps (most services need one).</summary>
public sealed class SignInForm
{
    public bool None { get; set; } = true;
    public List<SignInStepForm> Steps { get; } = [];

    public static SignInForm From(AuthProfile? profile)
    {
        var form = new SignInForm { None = profile is null || profile.Steps.Count == 0 };
        foreach (var step in profile?.Steps ?? [])
        {
            var s = new SignInStepForm { Kind = step.Kind };
            foreach (var (k, v) in step.Settings)
            {
                if (v.StartsWith("secret:", StringComparison.OrdinalIgnoreCase)) s.SecretRefs[k] = v;
                else s.Values[k] = v;
            }
            form.Steps.Add(s);
        }
        return form;
    }

    /// <summary>
    /// Builds the auth profile. Secret fields become "secret:&lt;serviceId&gt;-&lt;setting&gt;" references;
    /// the caller saves any newly typed values (returned in <paramref name="newSecrets"/>) separately.
    /// </summary>
    public AuthProfile? ToProfile(string profileId, string serviceId, string serviceName, out Dictionary<string, string> newSecrets)
    {
        newSecrets = [];
        if (None || Steps.Count == 0) return null;

        var profile = new AuthProfile { Id = profileId, Name = $"{serviceName} sign-in" };
        for (var i = 0; i < Steps.Count; i++)
        {
            var step = Steps[i];
            var def = new AuthStepDef { Kind = step.Kind };
            foreach (var field in SignInKinds.Get(step.Kind).Fields)
            {
                if (field.Secret)
                {
                    var name = Steps.Count == 1 ? $"{serviceId}-{field.Setting}" : $"{serviceId}-{i + 1}-{field.Setting}";
                    name = name.ToLowerInvariant();
                    if (step.SecretInputs.TryGetValue(field.Setting, out var typed) && !string.IsNullOrEmpty(typed))
                    {
                        newSecrets[name] = typed;
                        def.Settings[field.Setting] = $"secret:{name}";
                    }
                    else if (step.SecretRefs.TryGetValue(field.Setting, out var existing))
                        def.Settings[field.Setting] = existing;
                    else
                        def.Settings[field.Setting] = $"secret:{name}"; // value to be added in Key Vault
                }
                else if (step.Values.TryGetValue(field.Setting, out var value) && !string.IsNullOrWhiteSpace(value))
                    def.Settings[field.Setting] = value.Trim();
            }
            profile.Steps.Add(def);
        }
        return profile;
    }

    /// <summary>Plain-English problems, checked before saving.</summary>
    public IEnumerable<string> Problems()
    {
        if (None) yield break;
        foreach (var (step, i) in Steps.Select((s, i) => (s, i + 1)))
        foreach (var field in SignInKinds.Get(step.Kind).Fields.Where(f => f.Required))
        {
            var has = field.Secret
                ? step.SecretInputs.GetValueOrDefault(field.Setting) is { Length: > 0 } || step.SecretRefs.ContainsKey(field.Setting)
                : step.Values.GetValueOrDefault(field.Setting) is { Length: > 0 };
            if (!has) yield return Steps.Count == 1 ? $"Enter the {field.Label.ToLowerInvariant()}." : $"Sign-in step {i}: enter the {field.Label.ToLowerInvariant()}.";
        }
    }
}

public sealed class SignInStepForm
{
    public string Kind { get; set; } = "Basic";
    public bool ShowAdvanced { get; set; }
    public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Values typed in this session. Never loaded from the server.</summary>
    public Dictionary<string, string> SecretInputs { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Existing "secret:name" references, shown as "saved".</summary>
    public Dictionary<string, string> SecretRefs { get; } = new(StringComparer.OrdinalIgnoreCase);
}
