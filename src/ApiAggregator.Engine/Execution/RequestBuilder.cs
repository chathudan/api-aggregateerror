using System.Text;
using System.Text.Json.Nodes;
using ApiAggregator.Engine.Expressions;
using ApiAggregator.Engine.Model;

namespace ApiAggregator.Engine.Execution;

/// <summary>A downstream request description. Re-buildable into a fresh HttpRequestMessage for retries.</summary>
public sealed class BuiltRequest
{
    public required HttpMethod Method { get; init; }
    public required Uri Uri { get; init; }
    public List<KeyValuePair<string, string>> Headers { get; } = [];
    public List<KeyValuePair<string, string>> Cookies { get; } = [];
    public JsonNode? Body { get; set; }

    public HttpRequestMessage ToHttpRequestMessage()
    {
        var message = new HttpRequestMessage(Method, Uri);
        foreach (var (name, value) in Headers) message.Headers.TryAddWithoutValidation(name, value);
        if (Cookies.Count > 0) message.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", Cookies.Select(c => $"{c.Key}={c.Value}")));
        if (Body is not null) message.Content = new StringContent(Body.ToJsonString(), Encoding.UTF8, "application/json");
        return message;
    }

    /// <summary>Stable text used for the step cache key. Hashed before use.</summary>
    public string Fingerprint() =>
        $"{Method} {Uri}\n{string.Join("\n", Headers.Select(h => $"{h.Key}:{h.Value}"))}\n{string.Join(";", Cookies.Select(c => $"{c.Key}={c.Value}"))}\n{Body?.ToJsonString()}";

    /// <summary>For admin traces: method + URL with query VALUES masked.</summary>
    public string Describe()
    {
        var query = string.IsNullOrEmpty(Uri.Query)
            ? ""
            : "?" + string.Join("&", Uri.Query.TrimStart('?').Split('&').Select(p => p.Split('=')[0] + "=…"));
        return $"{Method} {Uri.GetLeftPart(UriPartial.Path)}{query}";
    }
}

public sealed class BindingException(string message) : Exception(message);

/// <summary>Turns an endpoint definition + bindings into a safe, fully-escaped request.</summary>
public static class RequestBuilder
{
    public static BuiltRequest Build(ApiService service, ApiEndpoint endpoint, IEnumerable<BindingDef> bindings, IValueScope scope)
    {
        var path = endpoint.Path;
        var query = new List<KeyValuePair<string, string>>();
        var headers = new List<KeyValuePair<string, string>>();
        var cookies = new List<KeyValuePair<string, string>>();
        JsonNode? body = null;

        foreach (var b in bindings)
        {
            var value = b.From is not null ? ValuePath.Parse(b.From).Evaluate(scope)?.DeepClone() : Template.ResolveNode(b.Template, scope);
            if (value is null && b.Default is not null) value = JsonValue.Create(b.Default);

            var parts = b.To.Split('.', 2);
            var target = parts[0].ToLowerInvariant();
            var name = parts.Length > 1 ? parts[1] : "";

            switch (target)
            {
                case "path":
                    var text = JsonText.Of(value);
                    if (string.IsNullOrEmpty(text)) throw new BindingException($"No value for '{name}' in the URL path.");
                    if (text is "." or "..") throw new BindingException($"'{name}' has an invalid value.");
                    path = path.Replace("{" + name + "}", Uri.EscapeDataString(text), StringComparison.Ordinal);
                    break;

                case "query":
                    if (value is JsonArray many)
                        foreach (var item in many) AddIfPresent(query, name, JsonText.Of(item));
                    else AddIfPresent(query, name, JsonText.Of(value));
                    break;

                case "header":
                    if (JsonText.Of(value) is { } headerValue)
                    {
                        EnsureSafeHeaderValue(name, headerValue);
                        headers.Add(KeyValuePair.Create(name, headerValue));
                    }
                    break;

                case "cookie":
                    if (JsonText.Of(value) is { } cookieValue)
                    {
                        if (cookieValue.IndexOfAny([';', ',', '\r', '\n', '\0']) >= 0) throw new BindingException($"Cookie '{name}' has an invalid value.");
                        cookies.Add(KeyValuePair.Create(name, cookieValue));
                    }
                    break;

                case "body":
                    if (name.Length == 0) body = value;
                    else SetNested(body ??= new JsonObject(), name, value);
                    break;

                default:
                    throw new BindingException($"'{b.To}' is not a valid target.");
            }
        }

        if (path.Contains('{')) throw new BindingException($"The URL path '{endpoint.Path}' still has values missing.");

        var uri = BuildUri(service.BaseUrl, path, query);
        var request = new BuiltRequest { Method = new HttpMethod(endpoint.Method.ToUpperInvariant()), Uri = uri, Body = body };
        request.Headers.AddRange(headers);
        request.Cookies.AddRange(cookies);
        return request;
    }

    private static Uri BuildUri(string baseUrl, string path, List<KeyValuePair<string, string>> query)
    {
        var baseUri = new Uri(baseUrl.TrimEnd('/') + "/", UriKind.Absolute);
        if (baseUri.Scheme is not ("http" or "https")) throw new BindingException("Service base URL must use http or https.");

        var sb = new StringBuilder(baseUri.ToString()).Append(path.TrimStart('/'));
        for (var i = 0; i < query.Count; i++)
            sb.Append(i == 0 ? '?' : '&').Append(Uri.EscapeDataString(query[i].Key)).Append('=').Append(Uri.EscapeDataString(query[i].Value));

        var uri = new Uri(sb.ToString(), UriKind.Absolute);
        // Defence in depth: whatever the values were, the call must still go to the configured service.
        if (!string.Equals(uri.Authority, baseUri.Authority, StringComparison.OrdinalIgnoreCase)
            || !uri.AbsolutePath.StartsWith(baseUri.AbsolutePath, StringComparison.Ordinal))
            throw new BindingException("A value tried to change the destination of the call.");
        return uri;
    }

    private static void AddIfPresent(List<KeyValuePair<string, string>> list, string name, string? value)
    {
        if (value is not null) list.Add(KeyValuePair.Create(name, value));
    }

    private static void EnsureSafeHeaderValue(string name, string value)
    {
        // Blocks header injection (CR/LF) and oversized values.
        if (value.Length > 4096 || value.Any(c => c is '\r' or '\n' or '\0'))
            throw new BindingException($"Header '{name}' has an invalid value.");
    }

    private static void SetNested(JsonNode root, string dottedPath, JsonNode? value)
    {
        var keys = dottedPath.Split('.');
        var current = root.AsObject();
        foreach (var key in keys[..^1])
        {
            if (current[key] is not JsonObject next) current[key] = next = new JsonObject();
            current = next;
        }
        current[keys[^1]] = value;
    }
}
