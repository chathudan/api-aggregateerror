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

public sealed record RecipeSummary(string Id, string Name, string Method, string Route, int? PublishedVersion, bool HasDraft);

public sealed record TestOutcome(bool Passed, int StatusCode, long DurationMs, bool Partial, List<TraceRow>? Trace, JsonNode? Body, List<IssueRow>? Issues);

public sealed record TraceRow(string Alias, string? DisplayName, string Outcome, int? HttpStatus, long DurationMs, int Calls, bool FromCache, string? Message, string? Request);

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
