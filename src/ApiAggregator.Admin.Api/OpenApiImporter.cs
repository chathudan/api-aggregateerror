using System.Text;
using System.Text.Json.Nodes;
using ApiAggregator.Engine.Model;
using Microsoft.OpenApi.Models;
using Microsoft.OpenApi.Readers;
using Microsoft.OpenApi.Writers;

namespace ApiAggregator.Admin.Api;

/// <summary>Either a URL to fetch the spec from, or the spec text itself (JSON or YAML).</summary>
public sealed record OpenApiImportRequest(string? Url, string? Spec, string[]? OperationIds);

public sealed record EndpointCandidate(string OperationId, string Method, string Path, string? Summary, string[] Tags, ParamDef[] Parameters);

public sealed record AuthHint(string SchemeName, string SuggestedKind, string Note);

public sealed record ImportPreview(string? Title, string? Version, string[] Servers, EndpointCandidate[] Endpoints, AuthHint[] AuthHints, string[] Warnings);

/// <summary>
/// Reads Swagger 2.0 / OpenAPI 3.x specs (Microsoft.OpenApi 1.6.x) and turns operations into
/// ApiEndpoint entries. Re-importing updates endpoints with the same operationId instead of duplicating them.
/// </summary>
public sealed class OpenApiImporter(IHttpClientFactory httpClientFactory, IConfiguration configuration)
{
    public const string HttpClientName = "openapi-import";
    private const int MaxSpecBytes = 5 * 1024 * 1024;

    public async Task<ImportPreview> PreviewAsync(OpenApiImportRequest request, CancellationToken ct)
    {
        var (doc, warnings) = await ReadAsync(request, ct);
        return new ImportPreview(
            doc.Info?.Title,
            doc.Info?.Version,
            doc.Servers.Select(s => s.Url ?? "").ToArray(),
            Candidates(doc).Select(c => c.Candidate).ToArray(),
            AuthHints(doc),
            warnings);
    }

    public async Task<ApiService> ImportAsync(ApiService service, OpenApiImportRequest request, CancellationToken ct)
    {
        var (doc, _) = await ReadAsync(request, ct);
        var wanted = request.OperationIds is { Length: > 0 } ids ? ids.ToHashSet(StringComparer.OrdinalIgnoreCase) : null;

        var endpoints = service.Endpoints.ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var (candidate, operation) in Candidates(doc))
        {
            if (wanted is not null && !wanted.Contains(candidate.OperationId)) continue;
            endpoints[candidate.OperationId] = new ApiEndpoint
            {
                Id = candidate.OperationId,
                Summary = candidate.Summary,
                Method = candidate.Method,
                Path = candidate.Path,
                Parameters = candidate.Parameters.ToList(),
                AuthProfileId = endpoints.GetValueOrDefault(candidate.OperationId)?.AuthProfileId, // keep a manual override
                RequestSchema = SchemaToJson(operation.RequestBody?.Content.FirstOrDefault(c => c.Key.Contains("json")).Value?.Schema),
                ResponseSchema = SchemaToJson(operation.Responses
                    .Where(r => r.Key.StartsWith('2')).OrderBy(r => r.Key).Select(r => r.Value).FirstOrDefault()?
                    .Content.FirstOrDefault(c => c.Key.Contains("json")).Value?.Schema),
                Source = "openapi"
            };
        }

        return new ApiService
        {
            Id = service.Id,
            Name = string.IsNullOrEmpty(service.Name) ? doc.Info?.Title ?? service.Id : service.Name,
            BaseUrl = service.BaseUrl,
            AuthProfileId = service.AuthProfileId,
            TimeoutMs = service.TimeoutMs,
            OpenApiSource = request.Url ?? "uploaded",
            Endpoints = endpoints.Values.OrderBy(e => e.Path).ThenBy(e => e.Method).ToList()
        };
    }

    // ------------------------------------------------------------------ reading

    private async Task<(OpenApiDocument Doc, string[] Warnings)> ReadAsync(OpenApiImportRequest request, CancellationToken ct)
    {
        Stream stream;
        if (!string.IsNullOrWhiteSpace(request.Spec))
        {
            if (request.Spec.Length > MaxSpecBytes) throw new BadHttpRequestException("The spec is too large.");
            stream = new MemoryStream(Encoding.UTF8.GetBytes(request.Spec));
        }
        else if (Uri.TryCreate(request.Url, UriKind.Absolute, out var url) && url.Scheme is "http" or "https")
        {
            EnsureAllowedHost(url);
            var client = httpClientFactory.CreateClient(HttpClientName);
            var bytes = await client.GetByteArrayAsync(url, ct);
            if (bytes.Length > MaxSpecBytes) throw new BadHttpRequestException("The spec is too large.");
            stream = new MemoryStream(bytes);
        }
        else throw new BadHttpRequestException("Give either a spec URL (http/https) or the spec text.");

        await using (stream)
        {
            var reader = new OpenApiStreamReader(new OpenApiReaderSettings { ReferenceResolution = ReferenceResolutionSetting.ResolveLocalReferences });
            var doc = reader.Read(stream, out var diagnostic);
            var warnings = diagnostic.Errors.Select(e => $"{e.Pointer}: {e.Message}").ToArray();
            if (doc?.Paths is null) throw new BadHttpRequestException("That doesn't look like an OpenAPI or Swagger document.");
            return (doc, warnings);
        }
    }

    /// <summary>Optional allow-list (Admin:OpenApiAllowedHosts) so the import feature can't be used to probe internal hosts.</summary>
    private void EnsureAllowedHost(Uri url)
    {
        var allowed = configuration.GetSection("Admin:OpenApiAllowedHosts").Get<string[]>();
        if (allowed is { Length: > 0 } && !allowed.Contains(url.Host, StringComparer.OrdinalIgnoreCase))
            throw new BadHttpRequestException($"Importing from '{url.Host}' isn't allowed. Ask an admin to add it to Admin:OpenApiAllowedHosts.");
    }

    private static IEnumerable<(EndpointCandidate Candidate, OpenApiOperation Operation)> Candidates(OpenApiDocument doc)
    {
        foreach (var (path, item) in doc.Paths)
        foreach (var (verb, op) in item.Operations)
        {
            var parameters = item.Parameters.Concat(op.Parameters)
                .GroupBy(p => (p.Name, p.In)).Select(g => g.Last()) // operation-level wins over path-level
                .Select(p => new ParamDef
                {
                    Name = p.Name ?? "",
                    In = p.In?.ToString().ToLowerInvariant() ?? "query",
                    Required = p.Required || p.In == ParameterLocation.Path,
                    Type = p.Schema?.Type
                })
                .ToArray();

            var method = verb.ToString().ToUpperInvariant();
            var operationId = string.IsNullOrWhiteSpace(op.OperationId) ? $"{method.ToLowerInvariant()}_{Slug(path)}" : op.OperationId;
            yield return (new EndpointCandidate(operationId, method, path, op.Summary, op.Tags.Select(t => t.Name ?? "").ToArray(), parameters), op);
        }
    }

    private static AuthHint[] AuthHints(OpenApiDocument doc) =>
        (doc.Components?.SecuritySchemes ?? new Dictionary<string, OpenApiSecurityScheme>())
        .Select(kv => kv.Value.Type switch
        {
            SecuritySchemeType.Http when kv.Value.Scheme == "basic" => new AuthHint(kv.Key, "Basic", "Add the username and a secret: reference for the password."),
            SecuritySchemeType.Http when kv.Value.Scheme == "bearer" => new AuthHint(kv.Key, "StaticBearer", "Or PassThroughUserToken if the service trusts your users' tokens."),
            SecuritySchemeType.ApiKey => new AuthHint(kv.Key, "ApiKey", $"Key name '{kv.Value.Name}' in {kv.Value.In}."),
            SecuritySchemeType.OAuth2 => new AuthHint(kv.Key, "OAuthClientCredentials", "Needs a client id and a secret: reference for the client secret."),
            SecuritySchemeType.OpenIdConnect => new AuthHint(kv.Key, "OAuthClientCredentials", "Use the provider's token endpoint."),
            _ => new AuthHint(kv.Key, "SessionLogin", "Not described by the spec: check with the service owner.")
        })
        .ToArray();

    private static JsonNode? SchemaToJson(OpenApiSchema? schema)
    {
        if (schema is null) return null;
        try
        {
            using var text = new StringWriter();
            schema.SerializeAsV3WithoutReference(new OpenApiJsonWriter(text));
            return JsonNode.Parse(text.ToString());
        }
        catch (Exception) { return null; } // a schema we can't serialize just means no autocomplete for it
    }

    private static string Slug(string path) =>
        new string(path.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_').ToArray()).Trim('_');
}
