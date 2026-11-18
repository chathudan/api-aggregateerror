using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ApiAggregator.Engine.Config;
using ApiAggregator.Engine.Logging.Live;

namespace ApiAggregator.Admin.UI.Services;

/// <summary>Which running process to read log lines from.</summary>
public enum LogSource { Gateway, AdminApi }

/// <summary>One answer from a process's log endpoint. Latest is the newest Seq it has; Source is its friendly name.</summary>
public sealed record LogPage(string Source, long Latest, int Capacity, List<LiveLogEntry> Entries);

/// <summary>
/// Reads the live log lines of the gateway and the Admin API. Uses the same dev token as the Admin API client.
/// Never throws for an unreachable process: it returns an error message the page can show.
/// </summary>
public sealed class LiveLogClient(HttpClient http, AdminTokenProvider tokens, IConfiguration configuration)
{
    public string BaseUrl(LogSource source) => source == LogSource.Gateway
        ? configuration["Gateway:BaseUrl"] ?? "http://localhost:5200/"
        : configuration["AdminApi:BaseUrl"] ?? "http://localhost:5210/";

    /// <summary>Lines newer than <paramref name="after"/>. With waitSeconds > 0 the process holds the call open until a line arrives.</summary>
    public async Task<(LogPage? Page, string? Error)> ReadAsync(LogSource source, long after, int waitSeconds, CancellationToken ct)
    {
        var baseUrl = BaseUrl(source).TrimEnd('/');
        var path = source == LogSource.Gateway ? "_admin/logs" : "admin/logs";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/{path}?after={after}&wait={waitSeconds}");
            if (await tokens.GetAsync(ct) is { } token) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await http.SendAsync(request, ct);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return (null, "refused the sign-in. Logs need the author or publisher role.");
            if (response.StatusCode == HttpStatusCode.NotFound)
                return (null, "has live logs turned off (set LiveLogs:Enabled to true).");
            if (!response.IsSuccessStatusCode)
                return (null, $"answered with an error ({(int)response.StatusCode}).");

            var page = await response.Content.ReadFromJsonAsync<LogPage>(AggregatorJson.Options, ct);
            return page is null ? (null, "sent a reply the page couldn't read.") : (page, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; } // the user left the page
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return (null, $"not reachable at {baseUrl} — is it running?");
        }
    }
}
