using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using ApiAggregator.Engine.Config;
using ApiAggregator.Engine.Model;

namespace ApiAggregator.Admin.UI.Services;

/// <summary>
/// Gets a token for calling the Admin API.
/// Development: asks the Admin API's /dev/token endpoint.
/// Production: replace with OpenID Connect sign-in for the UI user (see docs/roadmap.md) so every
/// change is recorded against the real person.
/// </summary>
public sealed class AdminTokenProvider(IConfiguration configuration, IHttpClientFactory httpClientFactory, IWebHostEnvironment env)
{
    private string? _token;
    private DateTime _expires;

    public async Task<string?> GetAsync(CancellationToken ct)
    {
        if (!env.IsDevelopment()) return configuration["AdminApi:Token"];
        if (_token is not null && DateTime.UtcNow < _expires) return _token;

        var client = httpClientFactory.CreateClient();
        var baseUrl = configuration["AdminApi:BaseUrl"] ?? "http://localhost:5210/";
        var json = await client.GetFromJsonAsync<JsonObject>(new Uri(new Uri(baseUrl), "dev/token?sub=dev-admin"), ct);
        _token = json?["access_token"]?.GetValue<string>();
        _expires = DateTime.UtcNow.AddMinutes(50);
        return _token;
    }
}

public sealed record RecipeSummary(string Id, string Name, string Method, string Route, int? PublishedVersion, bool HasDraft, bool Deleted = false);

/// <summary>One row of an endpoint's history. <see cref="By"/> and <see cref="At"/> are empty for versions published before they were recorded.</summary>
public sealed record HistoryRow(int Version, HistoryAction Action, DateTimeOffset? At, string? By, int? RestoredFrom, string? Name, string? Route, bool IsLive);

public sealed record RecipeRef(string Id, string Name);

public sealed record ServiceUsage(List<RecipeRef> Published, List<RecipeRef> Drafts);

public sealed record Capabilities(bool CanSaveSecrets, bool CanPublish, bool SampleData = false, bool RequireTestBeforePublish = true);

/// <summary>
/// Last sign-in check of one auth profile. State: signedIn, settingsOk (complete; checked by the service on the first
/// real call), failed, or notChecked (since the Admin API started).
/// </summary>
public sealed record SignInStatusRow(string ProfileId, string State, string Message, DateTimeOffset? CheckedAt);

public sealed record TestOutcome(bool Passed, int StatusCode, long DurationMs, bool Partial, List<TraceRow>? Trace, JsonNode? Body, List<IssueRow>? Issues, string? CorrelationId = null, List<IssueRow>? Warnings = null);

public sealed record TraceRow(string Alias, string? DisplayName, string Outcome, int? HttpStatus, long DurationMs, int Calls, bool FromCache, string? Message, string? Request, string? ErrorCode, string? ServiceId, bool Retryable, string? Detail);

public sealed record IssueRow(string Where, string Message);

/// <summary>Thin typed client over the Admin API. Errors come back as plain-English messages.</summary>
public sealed class AdminApiClient(HttpClient http, AdminTokenProvider tokens)
{
    private static readonly JsonSerializerOptions Json = AggregatorJson.Options;

    public Task<List<RecipeSummary>?> GetRecipesAsync(CancellationToken ct = default) => GetAsync<List<RecipeSummary>>("admin/recipes", ct);
    public Task<Recipe?> GetRecipeAsync(string id, CancellationToken ct = default) => GetAsync<Recipe>($"admin/recipes/{id}", ct);
    public Task<List<ApiService>?> GetServicesAsync(CancellationToken ct = default) => GetAsync<List<ApiService>>("admin/services", ct);
    public Task<List<AuthProfile>?> GetAuthProfilesAsync(CancellationToken ct = default) => GetAsync<List<AuthProfile>>("admin/auth-profiles", ct);

    public Task<string?> SaveDraftAsync(Recipe recipe, CancellationToken ct = default) => SendAsync(HttpMethod.Put, $"admin/recipes/{recipe.Id}", recipe, ct);
    public Task<string?> PublishAsync(string id, CancellationToken ct = default) => SendAsync(HttpMethod.Post, $"admin/recipes/{id}/publish", null, ct);
    public Task<string?> SaveServiceAsync(ApiService service, CancellationToken ct = default) => SendAsync(HttpMethod.Put, $"admin/services/{service.Id}", service, ct);
    public Task<string?> SaveAuthProfileAsync(AuthProfile profile, CancellationToken ct = default) => SendAsync(HttpMethod.Put, $"admin/auth-profiles/{profile.Id}", profile, ct);
    public Task<string?> SaveSecretAsync(string name, string value, CancellationToken ct = default) => SendAsync(HttpMethod.Put, $"admin/secrets/{name}", new { value }, ct);

    public async Task<bool> CanSaveSecretsAsync(CancellationToken ct = default) =>
        (await GetAsync<JsonObject>("admin/capabilities", ct))?["canSaveSecrets"]?.GetValue<bool>() == true;

    /// <summary>What this user may do here. Only hides buttons: the Admin API checks the role on every call.</summary>
    public async Task<Capabilities> GetCapabilitiesAsync(CancellationToken ct = default) =>
        await GetAsync<Capabilities>("admin/capabilities", ct) ?? new Capabilities(false, false);

    public Task<List<SignInStatusRow>?> GetSignInStatusAsync(CancellationToken ct = default) => GetAsync<List<SignInStatusRow>>("admin/auth-profiles/status", ct);

    public async Task<List<SignInStatusRow>?> CheckAllSignInsAsync(CancellationToken ct = default)
    {
        using var response = await SendRawAsync(HttpMethod.Post, "admin/auth-profiles/check-all", null, ct);
        return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<List<SignInStatusRow>>(Json, ct) : null;
    }

    // ------------------------------------------------------------------ history, roll back, delete

    public Task<List<HistoryRow>?> GetHistoryAsync(string id, CancellationToken ct = default) => GetAsync<List<HistoryRow>>($"admin/recipes/{id}/history", ct);
    public Task<Recipe?> GetVersionAsync(string id, int version, CancellationToken ct = default) => GetAsync<Recipe>($"admin/recipes/{id}/history/{version}", ct);
    public Task<string?> RollBackAsync(string id, int version, CancellationToken ct = default) => SendAsync(HttpMethod.Post, $"admin/recipes/{id}/rollback/{version}", null, ct);
    public Task<string?> DeleteEndpointAsync(string id, CancellationToken ct = default) => SendAsync(HttpMethod.Delete, $"admin/recipes/{id}", null, ct);
    public Task<string?> DiscardDraftAsync(string id, CancellationToken ct = default) => SendAsync(HttpMethod.Delete, $"admin/recipes/{id}/draft", null, ct);

    public Task<ServiceUsage?> GetServiceUsageAsync(string id, CancellationToken ct = default) => GetAsync<ServiceUsage>($"admin/services/{id}/usage", ct);
    public Task<string?> DeleteServiceAsync(string id, CancellationToken ct = default) => SendAsync(HttpMethod.Delete, $"admin/services/{id}", null, ct);

    public async Task<(bool Ok, string Message)> CheckSignInAsync(string profileId, CancellationToken ct = default)
    {
        using var response = await SendRawAsync(HttpMethod.Post, $"admin/auth-profiles/{profileId}/check", null, ct);
        var json = await response.Content.ReadFromJsonAsync<JsonObject>(Json, ct);
        return (json?["ok"]?.GetValue<bool>() == true, json?["message"]?.ToString() ?? json?["title"]?.ToString() ?? "Couldn't check the sign-in.");
    }

    public async Task<TestOutcome?> TestAsync(string id, Dictionary<string, string?> route, Dictionary<string, string?> query, Dictionary<string, string?> headers, CancellationToken ct = default)
    {
        using var response = await SendRawAsync(HttpMethod.Post, $"admin/recipes/{id}/test", new { route, query, headers }, ct);
        return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<TestOutcome>(Json, ct) : null;
    }

    public async Task<JsonNode?> PreviewOpenApiAsync(string serviceId, string url, CancellationToken ct = default)
    {
        using var response = await SendRawAsync(HttpMethod.Post, $"admin/services/{serviceId}/openapi/preview", new { url }, ct);
        return await response.Content.ReadFromJsonAsync<JsonNode>(Json, ct);
    }

    public Task<string?> ImportOpenApiAsync(string serviceId, string url, string[] operationIds, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"admin/services/{serviceId}/openapi/import", new { url, operationIds }, ct);

    // ------------------------------------------------------------------ plumbing

    private async Task<T?> GetAsync<T>(string path, CancellationToken ct)
    {
        using var response = await SendRawAsync(HttpMethod.Get, path, null, ct);
        return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<T>(Json, ct) : default;
    }

    /// <summary>Returns null on success, or a readable error message.</summary>
    private async Task<string?> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var response = await SendRawAsync(method, path, body, ct);
        if (response.IsSuccessStatusCode) return null;

        var problem = await response.Content.ReadFromJsonAsync<JsonObject>(Json, ct);
        var title = problem?["title"]?.ToString() ?? $"The request failed ({(int)response.StatusCode}).";
        var details = (problem?["errors"] as JsonArray ?? problem?["issues"] as JsonArray)?.Select(e => e?["message"]?.ToString() ?? e?.ToString());
        return details is null ? title : $"{title} {string.Join(" ", details)}";
    }

    private async Task<HttpResponseMessage> SendRawAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        if (await tokens.GetAsync(ct) is { } token) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = JsonContent.Create(body, options: Json);
        return await http.SendAsync(request, ct);
    }
}
