using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ApiAggregator.Admin.Application;
using ApiAggregator.Engine.Authoring;
using ApiAggregator.Engine.Model;

namespace ApiAggregator.Admin.Services;

/// <summary>"Add service" form: from an OpenAPI spec (recommended) or by hand.</summary>
public sealed class NewServiceForm
{
    public bool FromSpec { get; set; } = true;
    public string SpecUrl { get; set; } = "";
    public string Name { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public int TimeoutMs { get; set; } = 5000;
    public List<JsonNode> Operations { get; set; } = [];
    public HashSet<string> Selected { get; } = [];
    public string? SuggestedSignIn { get; set; }
    public SignInForm SignIn { get; } = new();

    /// <summary>The spec address's certificate, when loading failed because nothing trusts it (shown to check and trust).</summary>
    public ServiceCertificate? Untrusted { get; set; }

    /// <summary>A certificate the person checked and trusted for the spec's host (ADR 0018). Saved with the service when its base address is on that host.</summary>
    public string? TrustedCertificate { get; set; }
    public string? TrustedHost { get; set; }

    /// <summary>The trusted fingerprint, if it is for this address's host.</summary>
    public string? TrustFor(string? url) =>
        TrustedCertificate is not null && Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps
        && string.Equals(u.Host, TrustedHost, StringComparison.OrdinalIgnoreCase) ? TrustedCertificate : null;

    /// <summary>What the new service trusts: the checked certificate, when its base address is on the same https host.</summary>
    public ServiceCertificates? Certificates() => TrustFor(BaseUrl) is { } fingerprint ? new ServiceCertificates { Trusted = [fingerprint] } : null;

    public string Id => Slug.From(Name);

    /// <summary>Stage 1 (where it is): with a spec, it has to be loaded first.</summary>
    public IEnumerable<string> SourceProblems()
    {
        if (FromSpec && Operations.Count == 0) yield return "Load the spec first (choose Load), or choose Enter by hand.";
    }

    /// <summary>Stage 2 (name and endpoints).</summary>
    public IEnumerable<string> DetailProblems()
    {
        if (string.IsNullOrWhiteSpace(Name)) yield return "Give the service a name.";
        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https"))
            yield return "Enter the base address, e.g. https://orders.internal/api.";
        if (FromSpec && Selected.Count == 0) yield return "Pick at least one operation to import.";
    }

    public IEnumerable<string> Problems()
    {
        if (string.IsNullOrWhiteSpace(Name)) yield return "Give the service a name.";
        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https"))
            yield return "Enter the base address, e.g. https://orders.internal/api.";
        if (FromSpec && Selected.Count == 0) yield return "Load the spec and pick at least one operation, or switch to 'Enter by hand'.";
        foreach (var p in SignIn.Problems()) yield return p;
    }

    /// <summary>
    /// The base address a spec's first server stands for, or null when it isn't an http(s) address.
    /// A relative server ("/d") is relative to the spec's own address; with pasted spec text there is none.
    /// The scheme check matters: on macOS and Linux "/d" also parses as an absolute file:// address, and a spec
    /// could name file:, javascript: and so on. AdminService refuses those anyway, so this only keeps the form sane.
    /// </summary>
    public static string? ResolveServer(string server, string? specUrl)
    {
        if (Uri.TryCreate(server, UriKind.Absolute, out var abs) && abs.Scheme is "http" or "https") return server;
        if (!Uri.TryCreate(specUrl, UriKind.Absolute, out var spec) || spec.Scheme is not ("http" or "https")) return null;
        return Uri.TryCreate(spec, server, out var resolved) && resolved.Scheme is "http" or "https" ? resolved.ToString() : null;
    }

    /// <summary>Fills name, base address and the sign-in suggestion from an import preview.</summary>
    public void ApplyPreview(JsonNode preview)
    {
        if (string.IsNullOrWhiteSpace(Name)) Name = preview["title"]?.ToString() ?? "";
        var server = (preview["servers"] as JsonArray)?.FirstOrDefault()?.ToString();
        if (!string.IsNullOrEmpty(server) && string.IsNullOrWhiteSpace(BaseUrl))
            BaseUrl = ResolveServer(server, SpecUrl) ?? "";
        if (string.IsNullOrWhiteSpace(BaseUrl) && Uri.TryCreate(SpecUrl, UriKind.Absolute, out var spec))
            BaseUrl = spec.GetLeftPart(UriPartial.Authority);

        Operations = (preview["endpoints"] as JsonArray)?.Where(n => n is not null).Select(n => n!).ToList() ?? [];
        Selected.Clear();
        foreach (var op in Operations) Selected.Add(op["operationId"]!.ToString());

        var hint = (preview["authHints"] as JsonArray)?.FirstOrDefault();
        SuggestedSignIn = hint?["suggestedKind"]?.ToString();
        if (SuggestedSignIn is not null && SignIn.None)
        {
            SignIn.None = false;
            SignIn.Steps.Clear();
            var step = new SignInStepForm { Kind = SuggestedSignIn };
            // Plain values the spec states (an API key's header name, never a secret) are filled in for you.
            if (hint?["settings"] is JsonObject settings)
                foreach (var (key, value) in settings)
                    if (value?.ToString() is { Length: > 0 } text) step.Values[key] = text;
            SignIn.Steps.Add(step);
        }
    }
}

/// <summary>"Add endpoint" form for services without a spec.</summary>
public sealed class NewEndpointForm
{
    public string Name { get; set; } = "";
    public string Method { get; set; } = "GET";
    public string Path { get; set; } = "/";
    public List<ParamRow> Parameters { get; } = [];
    public string SampleResponse { get; set; } = "";

    /// <summary>"Send a test call": a value for each path value (by name), and a JSON body for POST, PUT and PATCH.</summary>
    public Dictionary<string, string> PathTestValues { get; } = new(StringComparer.Ordinal);
    public string TestBody { get; set; } = "";
    public bool HasBody => Method is "POST" or "PUT" or "PATCH";

    public IEnumerable<string> PathParams => AutoBinder.PathParams(Path);

    public IEnumerable<string> Problems(ApiService service)
    {
        if (string.IsNullOrWhiteSpace(Name)) yield return "Give the endpoint a name, e.g. Get orders.";
        if (!Path.StartsWith('/')) yield return "The path starts with /, e.g. /orders/{orderId}.";
        if (service.Endpoints.Any(e => e.Id.Equals(Id, StringComparison.OrdinalIgnoreCase))) yield return "This service already has an endpoint with that name.";
        if (SampleResponse.Trim().Length > 0 && TryParse(SampleResponse) is null) yield return "The sample response isn't valid JSON.";
    }

    public string Id => Slug.Camel(Name);

    /// <summary>What a test call needs before it can be sent, in plain words.</summary>
    public IEnumerable<string> TryProblems()
    {
        if (string.IsNullOrWhiteSpace(Name)) yield return "Give the endpoint a name, e.g. Get orders.";
        if (!Path.StartsWith('/')) yield return "The path starts with /, e.g. /orders/{orderId}.";
        if (HasBody && TestBody.Trim().Length > 0 && TryParse(TestBody) is null) yield return "The test body isn't valid JSON.";
    }

    /// <summary>The endpoint as it would be saved, with the typed test values ("path.orderId", "query.status"...).</summary>
    public EndpointTry ToTry()
    {
        var values = PathParams.ToDictionary(p => $"path.{p}", p => (string?)PathTestValues.GetValueOrDefault(p), StringComparer.Ordinal);
        foreach (var row in Parameters.Where(p => !string.IsNullOrWhiteSpace(p.Name) && !string.IsNullOrEmpty(p.TestValue)))
            values[$"{row.In}.{row.Name.Trim()}"] = row.TestValue;
        return new EndpointTry(ToEndpoint(), values, HasBody && TestBody.Trim().Length > 0 ? TryParse(TestBody) : null);
    }

    public ApiEndpoint ToEndpoint() => new()
    {
        Id = Id,
        Summary = Name.Trim(),
        Method = Method,
        Path = Path.Trim(),
        Parameters = PathParams.Select(p => new ParamDef { Name = p, In = "path", Required = true })
            .Concat(Parameters.Where(p => !string.IsNullOrWhiteSpace(p.Name)).Select(p => new ParamDef { Name = p.Name.Trim(), In = p.In, Required = p.Required }))
            .ToList(),
        ResponseSchema = TryParse(SampleResponse) is { } sample ? SourceCatalog.InferSchema(sample) : null,
        Source = "manual"
    };

    private static JsonNode? TryParse(string text)
    {
        try { return text.Trim().Length == 0 ? null : JsonNode.Parse(text); }
        catch (System.Text.Json.JsonException) { return null; }
    }
}

public sealed class ParamRow
{
    public string Name { get; set; } = "";
    public string In { get; set; } = "query";
    public bool Required { get; set; }

    /// <summary>Sent by "Send a test call" only; never saved.</summary>
    public string TestValue { get; set; } = "";
}

public static partial class Slug
{
    /// <summary>"Service D (billing)" -> "service-d-billing"</summary>
    public static string From(string text) => SlugRegex().Replace(text.ToLowerInvariant(), "-").Trim('-');

    /// <summary>"Get order lines" -> "getOrderLines"</summary>
    public static string Camel(string text)
    {
        var words = WordRegex().Matches(text).Select(m => m.Value).ToArray();
        if (words.Length == 0) return "endpoint";
        var camel = words[0].ToLowerInvariant() + string.Concat(words.Skip(1).Select(w => char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant()));
        return char.IsLetter(camel[0]) ? camel : "e" + camel;
    }

    /// <summary>"getOrderLines" -> "Get order lines"</summary>
    public static string Humanize(string id)
    {
        var spaced = CamelSplitRegex().Replace(id.Replace('_', ' ').Replace('-', ' '), " $1").Trim().ToLowerInvariant();
        return spaced.Length == 0 ? id : char.ToUpperInvariant(spaced[0]) + spaced[1..];
    }

    /// <summary>A copy with some parts changed. Every other setting is kept (add new ApiService properties here).</summary>
    public static ApiService With(this ApiService s, List<ApiEndpoint>? endpoints = null, string? authProfileId = null, bool clearAuth = false, string? name = null,
        ServiceCertificates? certificates = null, bool clearCertificates = false) => new()
    {
        Id = s.Id,
        Name = name ?? s.Name,
        BaseUrl = s.BaseUrl,
        AuthProfileId = clearAuth ? null : authProfileId ?? s.AuthProfileId,
        TimeoutMs = s.TimeoutMs,
        OpenApiSource = s.OpenApiSource,
        Certificates = clearCertificates ? null : certificates ?? s.Certificates,
        Endpoints = endpoints ?? s.Endpoints
    };

    [GeneratedRegex("[^a-z0-9]+")] private static partial Regex SlugRegex();
    [GeneratedRegex("[A-Za-z0-9]+")] private static partial Regex WordRegex();
    [GeneratedRegex("(?<=[a-z0-9])([A-Z])")] private static partial Regex CamelSplitRegex();
}
