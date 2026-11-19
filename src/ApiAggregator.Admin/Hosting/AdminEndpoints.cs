using System.Text.Json.Nodes;
using ApiAggregator.Admin.Application;
using ApiAggregator.Engine.Model;
using ApiAggregator.Hosting;

namespace ApiAggregator.Admin.Hosting;

/// <summary>
/// /admin/*: the HTTP API for scripts and tools. Same routes, bodies, status codes and roles as the separate admin
/// API had (docs/architecture/04-admin-and-openapi.md). Each endpoint only turns the bearer token into an
/// <see cref="AdminActor"/>, calls <see cref="AdminService"/> and writes the result: the rules live in the service.
/// </summary>
internal static class AdminEndpoints
{
    public static void MapAdminApi(this WebApplication app)
    {
        // Live logs of this process (Try it runs, sign-in checks, imports), for scripts. The Logs page reads them in-process.
        app.MapLiveLogs("/admin/logs", AdminAuth.ApiAuthor);

        var admin = app.MapGroup("/admin").RequireAuthorization(AdminAuth.ApiAuthor).DisableAntiforgery()
            .AddEndpointFilter(async (ctx, next) =>
            {
                // Ids become file names and cache keys: allow lowercase slugs only.
                var id = ctx.HttpContext.Request.RouteValues["id"]?.ToString();
                return id is null || (id.Length <= 64 && id.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-'))
                    ? await next(ctx)
                    : Results.BadRequest(new { title = "Ids use lowercase letters, digits and dashes only." });
            });

        static AdminActor Me(HttpContext http) => AdminActor.From(http.User);

        // ------------------------------------------------------------ services and endpoints
        admin.MapGet("/services", async (AdminService s, HttpContext h, CancellationToken ct) => (await s.GetServicesAsync(Me(h), ct)).ToHttp());
        admin.MapPut("/services/{id}", async (string id, ApiService body, AdminService s, HttpContext h, CancellationToken ct) => (await s.SaveServiceAsync(Me(h), id, body, ct)).ToHttp());
        admin.MapGet("/services/{id}/usage", async (string id, AdminService s, HttpContext h, CancellationToken ct) => (await s.GetServiceUsageAsync(Me(h), id, ct)).ToHttp());
        admin.MapDelete("/services/{id}", async (string id, AdminService s, HttpContext h, CancellationToken ct) => (await s.DeleteServiceAsync(Me(h), id, ct)).ToHttp());
        admin.MapPost("/services/{id}/openapi/preview", async (string id, OpenApiImportRequest body, AdminService s, HttpContext h, CancellationToken ct) => (await s.PreviewOpenApiAsync(Me(h), body, ct)).ToHttp());
        admin.MapPost("/services/{id}/openapi/import", async (string id, OpenApiImportRequest body, AdminService s, HttpContext h, CancellationToken ct) => (await s.ImportOpenApiAsync(Me(h), id, body, ct)).ToHttp());
        admin.MapPost("/schema/infer", (JsonNode sample, AdminService s, HttpContext h) => s.InferSchema(Me(h), sample).ToHttp());

        // ------------------------------------------------------------ installation, secrets, sign-ins
        admin.MapGet("/capabilities", (AdminService s, HttpContext h) => s.GetCapabilities(Me(h)).ToHttp());
        admin.MapPut("/secrets/{name}", async (string name, SecretValue body, AdminService s, HttpContext h, CancellationToken ct) => (await s.SaveSecretAsync(Me(h), name, body.Value, ct)).ToHttp());
        admin.MapGet("/secrets/{name}", async (string name, AdminService s, HttpContext h, CancellationToken ct) => (await s.SecretExistsAsync(Me(h), name, ct)).ToHttp());
        admin.MapGet("/auth-kinds", (AdminService s, HttpContext h) => s.GetAuthKinds(Me(h)).ToHttp());
        admin.MapPost("/auth-profiles/{id}/check", async (string id, AdminService s, HttpContext h, CancellationToken ct) => (await s.CheckSignInAsync(Me(h), id, ct)).ToHttp());
        admin.MapPost("/auth-profiles/check-all", async (AdminService s, HttpContext h, CancellationToken ct) => (await s.CheckAllSignInsAsync(Me(h), ct)).ToHttp());
        admin.MapGet("/auth-profiles/status", async (AdminService s, HttpContext h, CancellationToken ct) => (await s.GetSignInStatusAsync(Me(h), ct)).ToHttp());
        admin.MapGet("/auth-profiles", async (AdminService s, HttpContext h, CancellationToken ct) => (await s.GetAuthProfilesAsync(Me(h), ct)).ToHttp());
        admin.MapPut("/auth-profiles/{id}", async (string id, AuthProfile body, AdminService s, HttpContext h, CancellationToken ct) => (await s.SaveAuthProfileAsync(Me(h), id, body, ct)).ToHttp());

        // ------------------------------------------------------------ aggregated endpoints (recipes)
        admin.MapGet("/recipes", async (AdminService s, HttpContext h, CancellationToken ct) => (await s.GetRecipesAsync(Me(h), ct)).ToHttp());
        admin.MapGet("/recipes/{id}", async (string id, AdminService s, HttpContext h, CancellationToken ct) => (await s.GetRecipeAsync(Me(h), id, ct)).ToHttp());
        admin.MapPut("/recipes/{id}", async (string id, Recipe body, AdminService s, HttpContext h, CancellationToken ct) => (await s.SaveDraftAsync(Me(h), id, body, ct)).ToHttp());
        admin.MapDelete("/recipes/{id}/draft", async (string id, AdminService s, HttpContext h, CancellationToken ct) => (await s.DiscardDraftAsync(Me(h), id, ct)).ToHttp());
        admin.MapPost("/recipes/{id}/validate", async (string id, AdminService s, HttpContext h, CancellationToken ct) => (await s.ValidateAsync(Me(h), id, ct)).ToHttp());
        admin.MapGet("/recipes/{id}/sources", async (string id, string? beforeStep, AdminService s, HttpContext h, CancellationToken ct) => (await s.GetSourcesAsync(Me(h), id, beforeStep, ct)).ToHttp());
        admin.MapPost("/recipes/{id}/test", async (string id, TestRequest body, AdminService s, HttpContext h, CancellationToken ct) =>
            (await s.TestAsync(Me(h), id, body, InboundAuth.CallerFrom(h), ct)).ToHttp());
        admin.MapPost("/recipes/{id}/publish", async (string id, AdminService s, HttpContext h, CancellationToken ct) => (await s.PublishAsync(Me(h), id, ct)).ToHttp())
            .RequireAuthorization(AdminAuth.ApiPublisher);
        admin.MapGet("/recipes/{id}/history", async (string id, AdminService s, HttpContext h, CancellationToken ct) => (await s.GetHistoryAsync(Me(h), id, ct)).ToHttp());
        admin.MapGet("/recipes/{id}/history/{version:int}", async (string id, int version, AdminService s, HttpContext h, CancellationToken ct) => (await s.GetVersionAsync(Me(h), id, version, ct)).ToHttp());
        admin.MapPost("/recipes/{id}/rollback/{version:int}", async (string id, int version, AdminService s, HttpContext h, CancellationToken ct) => (await s.RollBackAsync(Me(h), id, version, ct)).ToHttp())
            .RequireAuthorization(AdminAuth.ApiPublisher);
        admin.MapDelete("/recipes/{id}", async (string id, AdminService s, HttpContext h, CancellationToken ct) => (await s.DeleteEndpointAsync(Me(h), id, ct)).ToHttp())
            .RequireAuthorization(AdminAuth.ApiPublisher);
    }

    /// <summary>
    /// A value is 200 with the value; a failure is its status with <c>{ "title": ..., extra fields }</c>, or no body
    /// when the title is null. The same bodies the separate admin API wrote before ADR 0014.
    /// </summary>
    public static IResult ToHttp<T>(this AdminResult<T> result)
    {
        if (result.Failure is not { } failure) return Results.Ok(result.Value);

        var status = failure.Kind switch
        {
            FailureKind.NotFound => StatusCodes.Status404NotFound,
            FailureKind.Conflict => StatusCodes.Status409Conflict,
            FailureKind.Forbidden => StatusCodes.Status403Forbidden,
            _ => StatusCodes.Status400BadRequest
        };
        if (failure.Title is null) return Results.StatusCode(status);

        var body = new Dictionary<string, object?> { ["title"] = failure.Title };
        foreach (var (key, value) in failure.Extra ?? []) body[key] = value;
        return Results.Json(body, statusCode: status);
    }
}
