// Admin API: used by the admin UI (and by scripts) to register services, import OpenAPI specs,
// build recipes as drafts, test them and publish them. The gateway only ever sees published recipes.
//
// Roles (scopes in the caller's token):
//   aggregator.author     create and test drafts, register services
//   aggregator.publisher  everything an author can do, plus publish and roll back

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ApiAggregator.Admin.Api;
using ApiAggregator.Engine.Authoring;
using ApiAggregator.Engine;
using ApiAggregator.Engine.Auth;
using ApiAggregator.Engine.Config;
using ApiAggregator.Engine.Execution;
using ApiAggregator.Engine.Logging.Live;
using ApiAggregator.Engine.Model;
using ApiAggregator.Engine.Validation;
using ApiAggregator.Hosting;
using Microsoft.AspNetCore.Authentication.JwtBearer;

var builder = WebApplication.CreateBuilder(args);
builder.AddLocalSecretsFile(); // Development only: secrets saved from the admin UI

// Logs: readable single lines on a developer machine, one JSON object per line (with scopes) everywhere else.
if (builder.Environment.IsDevelopment())
    builder.Logging.AddSimpleConsole(o => { o.IncludeScopes = true; o.SingleLine = true; });
else
    builder.Logging.ClearProviders().AddJsonConsole(o =>
    {
        o.IncludeScopes = true;
        o.UseUtcTimestamp = true;
        o.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ ";
    });

// Keep the latest lines in memory for the admin UI's Logs page. After the console setup above: ClearProviders would remove it.
builder.Logging.AddLiveLogs(builder.Configuration.GetSection("LiveLogs"));

builder.Services.AddAggregatorEngine(builder.Configuration);
builder.Services.AddSingleton<OpenApiImporter>();
builder.Services.AddSingleton<SourceCatalog>();
builder.Services.AddHttpClient(OpenApiImporter.HttpClientName)
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false })
    .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(10))
    .RemoveAllLoggers(); // no default HttpClient logging: it would write full URLs (see ServiceCollectionExtensions)

InboundAuth.ValidateSettings(builder.Configuration.GetSection("Auth"), builder.Environment); // fail at startup, not on the first request
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o => InboundAuth.Configure(o, builder.Configuration.GetSection("Auth"), builder.Environment));
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("author", p => p.RequireAssertion(c => InboundAuth.HasScope(c.User, "aggregator.author") || InboundAuth.HasScope(c.User, "aggregator.publisher")))
    .AddPolicy("publisher", p => p.RequireAssertion(c => InboundAuth.HasScope(c.User, "aggregator.publisher")));

builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(builder.Configuration.GetSection("Admin:AllowedOrigins").Get<string[]>() ?? [])
    .AllowAnyHeader().AllowAnyMethod()));
builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
    o.SerializerOptions.ReadCommentHandling = JsonCommentHandling.Skip;
    o.SerializerOptions.AllowTrailingCommas = true;
});

var app = builder.Build();
app.UseExceptionHandler();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

var requireTestBeforePublish = builder.Configuration.GetValue("Admin:RequireTestBeforePublish", true);
var passedTests = new ConcurrentDictionary<string, string>(); // recipe id -> hash of the draft that last passed "Try it"
// Publish, roll back, delete endpoint and delete service run one at a time, so "no published endpoint uses this service"
// can't change between the check and the delete. (The file store is single-instance; see ADR 0007.)
var liveChanges = new SemaphoreSlim(1, 1);

// Live logs for the admin UI's Logs page (this process: Try it runs, sign-in checks, imports).
app.MapLiveLogs("/admin/logs", "author");

var admin = app.MapGroup("/admin").RequireAuthorization("author")
    .AddEndpointFilter(async (ctx, next) =>
    {
        // Ids become file names and cache keys: allow lowercase slugs only.
        var id = ctx.HttpContext.Request.RouteValues["id"]?.ToString();
        return id is null || (id.Length <= 64 && id.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-'))
            ? await next(ctx)
            : Results.BadRequest(new { title = "Ids use lowercase letters, digits and dashes only." });
    });

// ---------------------------------------------------------------- services and endpoints
admin.MapGet("/services", async (IConfigStore store, CancellationToken ct) => (await store.GetSnapshotAsync(ct)).Services.OrderBy(s => s.Id));

admin.MapPut("/services/{id}", async (string id, ApiService service, IConfigStore store, CancellationToken ct) =>
{
    if (service.Id != id) return Results.BadRequest(new { title = "The id in the URL and the body must match." });
    if (!Uri.TryCreate(service.BaseUrl, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https"))
        return Results.BadRequest(new { title = "Base URL must be an http or https address." });
    if (string.IsNullOrWhiteSpace(service.Name) || service.Name.Length > 100 || service.Name.Any(char.IsControl))
        return Results.BadRequest(new { title = "Give the service a name of up to 100 characters." });
    await store.SaveServiceAsync(service, ct);
    return Results.Ok(service);
});

// Which aggregated endpoints call this service. Shown before deleting it.
admin.MapGet("/services/{id}/usage", async (string id, IConfigStore store, CancellationToken ct) =>
{
    var (published, drafts) = await ServiceUsage(store, id, ct);
    return Results.Ok(new { id, published, drafts });
});

// Blocked while a published endpoint uses the service. Its sign-in is kept: other services may share it.
admin.MapDelete("/services/{id}", async (string id, IConfigStore store, HttpContext http, ILogger<Program> log, CancellationToken ct) =>
{
    if ((await store.GetSnapshotAsync(ct)).GetService(id) is null) return Results.NotFound(new { title = $"Service '{id}' not found." });

    await liveChanges.WaitAsync(ct);
    try
    {
        var (published, drafts) = await ServiceUsage(store, id, ct);
        if (published.Count > 0)
            return Results.Conflict(new
            {
                title = $"Published endpoints still use this service: {string.Join(", ", published.Select(r => r.Name))}. Change or delete those endpoints first.",
                published
            });

        await store.DeleteServiceAsync(id, ct);
        log.LogInformation("Service {ServiceId} deleted by {Subject}", id, Subject(http));
        return Results.Ok(new { id, draftsAffected = drafts });
    }
    finally { liveChanges.Release(); }
});

admin.MapPost("/services/{id}/openapi/preview", async (string id, OpenApiImportRequest request, OpenApiImporter importer, CancellationToken ct) =>
    Results.Ok(await importer.PreviewAsync(request, ct)));

admin.MapPost("/services/{id}/openapi/import", async (string id, OpenApiImportRequest request, OpenApiImporter importer, IConfigStore store, CancellationToken ct) =>
{
    var service = (await store.GetSnapshotAsync(ct)).GetService(id);
    if (service is null) return Results.NotFound(new { title = $"Service '{id}' not found. Create it first." });
    var updated = await importer.ImportAsync(service, request, ct);
    await store.SaveServiceAsync(updated, ct);
    return Results.Ok(updated);
});

// For endpoints added by hand: paste a sample response, get a schema back (drives the UI dropdowns).
admin.MapPost("/schema/infer", (System.Text.Json.Nodes.JsonNode sample) => Results.Ok(SourceCatalog.InferSchema(sample)));

// ---------------------------------------------------------------- what this installation allows
admin.MapGet("/capabilities", (IEnumerable<IAuthStep> steps, IWebHostEnvironment env, HttpContext http) => new
{
    // So the UI can hide publish, roll back and delete for authors. The API still checks the role on every call.
    canPublish = InboundAuth.HasScope(http.User, "aggregator.publisher"),
    // Development: the UI may store secret values (in config/secrets.local.json, git-ignored).
    // Elsewhere: the UI shows the secret NAME to put in Key Vault and never sends the value.
    canSaveSecrets = env.IsDevelopment(),
    authKinds = steps.Select(s => new { kind = s.Kind, requiredSettings = s.RequiredSettings })
});

// ---------------------------------------------------------------- secrets (values never come back)
admin.MapPut("/secrets/{name}", async (string name, SecretValue body, IWebHostEnvironment env, IConfiguration configuration, CancellationToken ct) =>
{
    if (!env.IsDevelopment())
        return Results.BadRequest(new { title = "Secrets are managed in Key Vault in this environment. Ask an admin to add it there." });
    if (!IsSecretName(name)) return Results.BadRequest(new { title = "Secret names use lowercase letters, digits and dashes." });
    if (string.IsNullOrEmpty(body.Value) || body.Value.Length > 4096) return Results.BadRequest(new { title = "Enter a value (up to 4096 characters)." });

    await LocalSecrets.SaveAsync(LocalSecrets.PathFor(configuration, env.ContentRootPath), name, body.Value, ct);
    return Results.Ok(new { name, reference = $"secret:{name}" });
});

admin.MapGet("/secrets/{name}", async (string name, ISecretResolver secrets, CancellationToken ct) =>
{
    if (!IsSecretName(name)) return Results.BadRequest();
    try { await secrets.GetAsync(name, ct); return Results.Ok(new { name, exists = true }); }
    catch (AuthFailedException) { return Results.Ok(new { name, exists = false }); }
});

// ---------------------------------------------------------------- auth profiles
admin.MapGet("/auth-kinds", (IEnumerable<IAuthStep> steps) =>
    steps.Select(s => new { kind = s.Kind, requiredSettings = s.RequiredSettings }));

// Signs in once with the profile (login / token call), so problems show up before anyone builds an endpoint.
admin.MapPost("/auth-profiles/{id}/check", async (string id, IConfigStore store, DownstreamAuthenticator auth, CancellationToken ct) =>
{
    var config = await store.GetSnapshotAsync(ct);
    var profile = config.GetAuthProfile(id);
    if (profile is null) return Results.NotFound(new { title = $"Sign-in '{id}' not found." });
    var service = config.Services.FirstOrDefault(s => s.AuthProfileId == id);

    using var probe = new HttpRequestMessage(HttpMethod.Get, service?.BaseUrl ?? "http://localhost/");
    try
    {
        auth.Invalidate(profile, "sign-in check requested");
        await auth.ApplyAsync(profile, probe, CallerContext.Anonymous(), ct);
        var signsInOnline = profile.Steps.Any(s => s.Kind is "SessionLogin" or "OAuthClientCredentials");
        return Results.Ok(new
        {
            ok = true,
            message = signsInOnline
                ? "Signed in."
                : "Settings and secrets are complete. This sign-in type is checked by the service on the first real call."
        });
    }
    catch (AuthFailedException ex) { return Results.Ok(new { ok = false, message = ex.Message }); }
    catch (HttpRequestException) { return Results.Ok(new { ok = false, message = "Couldn't reach the sign-in address. Check the URL and that the service is running." }); }
});

admin.MapGet("/auth-profiles", async (IConfigStore store, CancellationToken ct) => (await store.GetSnapshotAsync(ct)).AuthProfiles.OrderBy(p => p.Id));

admin.MapPut("/auth-profiles/{id}", async (string id, AuthProfile profile, DownstreamAuthenticator auth, IConfigStore store, CancellationToken ct) =>
{
    if (profile.Id != id) return Results.BadRequest(new { title = "The id in the URL and the body must match." });
    var problems = auth.Check(profile).ToList();
    if (problems.Count > 0) return Results.BadRequest(new { title = "The auth profile has problems.", errors = problems });
    await store.SaveAuthProfileAsync(profile, ct);
    return Results.Ok(profile);
});

// ---------------------------------------------------------------- aggregated endpoints (recipes)
admin.MapGet("/recipes", async (IConfigStore store, CancellationToken ct) =>
{
    var published = (await store.GetSnapshotAsync(ct)).Recipes;
    var drafts = await store.GetDraftsAsync(ct);
    var current = published.Select(r => r.Id).Union(drafts.Select(d => d.Id)).ToHashSet();

    var list = current.Select(id =>
    {
        var p = published.FirstOrDefault(r => r.Id == id);
        var d = drafts.FirstOrDefault(r => r.Id == id);
        var r = d ?? p!;
        return new RecipeListItem(id, r.Name, r.Method, "/bff/" + r.Route, p?.Version, d is not null, Deleted: false);
    }).ToList();

    // Deleted endpoints: not live and no draft, but their history is kept so they can be restored.
    foreach (var id in (await store.GetHistoryIdsAsync(ct)).Where(id => !current.Contains(id)))
    {
        var last = (await store.GetHistoryAsync(id, ct)).FirstOrDefault(e => e.Recipe is not null)?.Recipe;
        if (last is not null) list.Add(new RecipeListItem(id, last.Name, last.Method, "/bff/" + last.Route, null, false, Deleted: true));
    }
    return list.OrderBy(r => r.Id);
});

admin.MapGet("/recipes/{id}", async (string id, IConfigStore store, CancellationToken ct) =>
{
    var recipe = await store.GetDraftAsync(id, ct) ?? (await store.GetSnapshotAsync(ct)).GetRecipe(id);
    return recipe is null ? Results.NotFound() : Results.Ok(recipe);
});

admin.MapPut("/recipes/{id}", async (string id, Recipe draft, IConfigStore store, CancellationToken ct) =>
{
    if (draft.Id != id) return Results.BadRequest(new { title = "The id in the URL and the body must match." });
    var saved = Clone(draft, status: RecipeStatus.Draft, version: await NextVersion(store, id, ct));
    await store.SaveDraftAsync(saved, ct);
    return Results.Ok(saved);
});

// Discard changes: removes the draft only. The live version (if any) keeps serving.
admin.MapDelete("/recipes/{id}/draft", async (string id, IConfigStore store, HttpContext http, ILogger<Program> log, CancellationToken ct) =>
{
    if (!await store.DeleteDraftAsync(id, ct)) return Results.NotFound(new { title = "There are no unpublished changes to discard." });
    passedTests.TryRemove(id, out _);
    log.LogInformation("Draft {RecipeId} discarded by {Subject}", id, Subject(http));
    var live = (await store.GetSnapshotAsync(ct)).GetRecipe(id);
    return Results.Ok(new { id, liveVersion = live?.Version });
});

admin.MapPost("/recipes/{id}/validate", async (string id, IConfigStore store, CancellationToken ct) =>
{
    var draft = await store.GetDraftAsync(id, ct);
    if (draft is null) return Results.NotFound(new { title = "Save the draft first." });
    return Results.Ok(RecipeValidator.Validate(draft, await store.GetSnapshotAsync(ct)));
});

admin.MapGet("/recipes/{id}/sources", async (string id, string? beforeStep, IConfigStore store, SourceCatalog sources, CancellationToken ct) =>
{
    var draft = await store.GetDraftAsync(id, ct) ?? (await store.GetSnapshotAsync(ct)).GetRecipe(id);
    return draft is null ? Results.NotFound() : Results.Ok(sources.For(draft, beforeStep, await store.GetSnapshotAsync(ct)));
});

admin.MapPost("/recipes/{id}/test", async (string id, TestRequest test, IConfigStore store, CompositionExecutor executor, HttpContext http, CancellationToken ct) =>
{
    var draft = await store.GetDraftAsync(id, ct);
    if (draft is null) return Results.NotFound(new { title = "Save the draft first." });

    var validation = RecipeValidator.Validate(draft, await store.GetSnapshotAsync(ct));
    if (!validation.IsValid) return Results.Ok(new { passed = false, validation.Issues, validation.Warnings });

    var caller = InboundAuth.CallerFrom(http);
    var result = await executor.ExecuteAsync(draft, test.ToInbound(), caller, ExecutionMode.AdminTest, ct);
    var passed = result.StatusCode == 200 && !result.Partial;
    if (passed) passedTests[id] = Hash(draft);

    return Results.Ok(new { passed, result.StatusCode, result.DurationMs, result.Partial, result.Trace, body = result.Body, correlationId = caller.CorrelationId, validation.Warnings });
});

admin.MapPost("/recipes/{id}/publish", async (string id, IConfigStore store, HttpContext http, ILogger<Program> log, CancellationToken ct) =>
{
    var draft = await store.GetDraftAsync(id, ct);
    if (draft is null) return Results.NotFound(new { title = "There's no draft to publish." });

    await liveChanges.WaitAsync(ct);
    try
    {
        var validation = RecipeValidator.Validate(draft, await store.GetSnapshotAsync(ct));
        if (!validation.IsValid) return Results.BadRequest(new { title = "Fix the problems before publishing.", validation.Issues });
        if (requireTestBeforePublish && (!passedTests.TryGetValue(id, out var hash) || hash != Hash(draft)))
            return Results.BadRequest(new { title = "Run a successful test of this exact draft before publishing." });

        // The version comes from the history, not the draft: a roll back or a delete may have happened since the draft was saved.
        var live = Clone(draft, status: RecipeStatus.Published, version: await NextVersion(store, id, ct));
        try { await store.PublishAsync(live, Change(http), ct); }
        catch (HistoryConflictException) { return PublishedMeanwhile(); }

        await store.DeleteDraftAsync(id, ct); // it is the live version now, so there are no unpublished changes left
        passedTests.TryRemove(id, out _);
        log.LogInformation("Recipe {RecipeId} v{Version} published by {Subject}", id, live.Version, Subject(http));
        // Warnings never block publishing; they are returned so the caller can show them.
        return Results.Ok(new { id, live.Version, url = "/bff/" + live.Route, validation.Warnings });
    }
    finally { liveChanges.Release(); }
}).RequireAuthorization("publisher");

admin.MapGet("/recipes/{id}/history", async (string id, IConfigStore store, CancellationToken ct) =>
{
    var liveVersion = (await store.GetSnapshotAsync(ct)).GetRecipe(id)?.Version;
    return (await store.GetHistoryAsync(id, ct)).Select(e => new
    {
        e.Version, action = e.Action, e.At, e.By, e.RestoredFrom, name = e.Recipe?.Name, route = e.Recipe?.Route,
        isLive = e.Action != HistoryAction.Unpublished && e.Version == liveVersion
    });
});

// One version's content, for "View" in the History panel (read only).
admin.MapGet("/recipes/{id}/history/{version:int}", async (string id, int version, IConfigStore store, CancellationToken ct) =>
    (await store.GetHistoryAsync(id, ct)).FirstOrDefault(e => e.Version == version && e.Recipe is not null)?.Recipe is { } recipe
        ? Results.Ok(recipe)
        : Results.NotFound(new { title = $"Version {version} isn't in the history." }));

admin.MapPost("/recipes/{id}/rollback/{version:int}", async (string id, int version, IConfigStore store, HttpContext http, ILogger<Program> log, CancellationToken ct) =>
{
    var target = (await store.GetHistoryAsync(id, ct)).FirstOrDefault(e => e.Version == version && e.Recipe is not null)?.Recipe;
    if (target is null) return Results.NotFound(new { title = $"Version {version} isn't in the history." });

    await liveChanges.WaitAsync(ct);
    try
    {
        // Services may have changed since that version was live: never put a broken endpoint live.
        var validation = RecipeValidator.Validate(target, await store.GetSnapshotAsync(ct));
        if (!validation.IsValid)
            return Results.BadRequest(new { title = $"Version {version} can't go live as it is, because the services it uses have changed. Open it, fix the problems, then test and publish.", validation.Issues });

        // Rolling back publishes the old content as a NEW version, so history stays append-only.
        var live = Clone(target, status: RecipeStatus.Published, version: await NextVersion(store, id, ct));
        try { await store.PublishAsync(live, Change(http, restoredFrom: version), ct); }
        catch (HistoryConflictException) { return PublishedMeanwhile(); }

        log.LogInformation("Recipe {RecipeId} rolled back to v{From} as v{Version} by {Subject}", id, version, live.Version, Subject(http));
        return Results.Ok(new { id, live.Version, restoredFrom = version });
    }
    finally { liveChanges.Release(); }
}).RequireAuthorization("publisher");

// Delete an endpoint: the gateway stops serving it and its draft is discarded. Its history is kept, so it can be restored.
admin.MapDelete("/recipes/{id}", async (string id, IConfigStore store, HttpContext http, ILogger<Program> log, CancellationToken ct) =>
{
    await liveChanges.WaitAsync(ct);
    try
    {
        var liveVersion = (await store.GetSnapshotAsync(ct)).GetRecipe(id)?.Version;
        var unpublished = await store.UnpublishAsync(id, Change(http), ct);
        var draftDiscarded = await store.DeleteDraftAsync(id, ct);
        if (!unpublished && !draftDiscarded) return Results.NotFound(new { title = $"There's no endpoint '{id}' to delete." });

        passedTests.TryRemove(id, out _);
        log.LogInformation("Recipe {RecipeId} deleted (unpublished v{Version}, draft discarded: {DraftDiscarded}) by {Subject}", id, liveVersion, draftDiscarded, Subject(http));
        return Results.Ok(new { id, unpublishedVersion = unpublished ? liveVersion : null, draftDiscarded });
    }
    finally { liveChanges.Release(); }
}).RequireAuthorization("publisher");

if (app.Environment.IsDevelopment() && !string.IsNullOrEmpty(builder.Configuration["Auth:DevSigningKey"]))
{
    app.MapGet("/dev/token", (string? sub, string? scope) => Results.Ok(new
    {
        access_token = InboundAuth.CreateDevToken(builder.Configuration.GetSection("Auth"), sub ?? "dev-admin", scope ?? "aggregator.author aggregator.publisher customer.read")
    }));
}

app.Run();

static Recipe Clone(Recipe r, RecipeStatus status, int version) =>
    JsonSerializer.Deserialize<Recipe>(JsonSerializer.Serialize(r, AggregatorJson.Options), AggregatorJson.Options) is { } copy
        ? new Recipe
        {
            Id = copy.Id, Name = copy.Name, Version = version, Status = status, Method = copy.Method, Route = copy.Route,
            RequireAuthentication = copy.RequireAuthentication, RequiredScope = copy.RequiredScope, Inputs = copy.Inputs,
            Steps = copy.Steps, Combine = copy.Combine, Response = copy.Response, Cache = copy.Cache, TimeoutMs = copy.TimeoutMs
        }
        : throw new InvalidOperationException("Could not copy the recipe.");

static string Hash(Recipe r)
{
    var content = Clone(r, RecipeStatus.Draft, 0); // status/version don't change what the recipe does
    return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(content, AggregatorJson.Options))));
}

static string? Subject(HttpContext http) => http.User.FindFirst("sub")?.Value;

static ChangeInfo Change(HttpContext http, int? restoredFrom = null) => new(Subject(http), DateTimeOffset.UtcNow, restoredFrom);

/// <summary>One more than any version ever used, live or in the history: version numbers are never reused (ADR 0012).</summary>
static async Task<int> NextVersion(IConfigStore store, string id, CancellationToken ct)
{
    var live = (await store.GetSnapshotAsync(ct)).GetRecipe(id)?.Version ?? 0;
    var history = (await store.GetHistoryAsync(id, ct)).Select(e => e.Version).DefaultIfEmpty(0).Max();
    return Math.Max(live, history) + 1;
}

static IResult PublishedMeanwhile() => Results.Conflict(new { title = "Someone else published this endpoint at the same moment. Reload the page, check the history, and try again." });

/// <summary>Published and draft endpoints with a step that calls the service.</summary>
static async Task<(List<RecipeRef> Published, List<RecipeRef> Drafts)> ServiceUsage(IConfigStore store, string serviceId, CancellationToken ct)
{
    bool Uses(Recipe r) => r.Steps.Any(s => s.Endpoint.StartsWith(serviceId + "/", StringComparison.OrdinalIgnoreCase));
    RecipeRef Ref(Recipe r) => new(r.Id, string.IsNullOrEmpty(r.Name) ? r.Id : r.Name);
    var published = (await store.GetSnapshotAsync(ct)).Recipes.Where(Uses).Select(Ref).OrderBy(r => r.Id).ToList();
    var drafts = (await store.GetDraftsAsync(ct)).Where(Uses).Select(Ref).OrderBy(r => r.Id).ToList();
    return (published, drafts);
}

static bool IsSecretName(string name) =>
    name.Length is > 0 and <= 80 && name.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-');

public sealed record SecretValue(string? Value);

public sealed record RecipeRef(string Id, string Name);

public sealed record RecipeListItem(string Id, string Name, string Method, string Route, int? PublishedVersion, bool HasDraft, bool Deleted);

public sealed record TestRequest(Dictionary<string, string?>? Route, Dictionary<string, string?>? Query, Dictionary<string, string?>? Headers)
{
    public InboundRequest ToInbound() => new()
    {
        Route = Route ?? new Dictionary<string, string?>(),
        Query = Query ?? new Dictionary<string, string?>(),
        Headers = Headers ?? new Dictionary<string, string?>()
    };
}

public partial class Program;
