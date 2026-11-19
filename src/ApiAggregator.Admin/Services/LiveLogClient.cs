using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ApiAggregator.Engine.Config;
using ApiAggregator.Engine.Logging.Live;

namespace ApiAggregator.Admin.Services;

/// <summary>Which running process to read log lines from.</summary>
public enum LogSource { Gateway, Admin }

/// <summary>One answer from a process's log lines. Latest is the newest Seq it has; Source is its friendly name.</summary>
public sealed record LogPage(string Source, long Latest, int Capacity, List<LiveLogEntry> Entries);

/// <summary>
/// The live log lines for the Logs page and the home page. This app's own lines come straight from its in-memory
/// buffer. The gateway's come over HTTP from /_admin/logs, with a token for the signed-in person
/// (<see cref="CurrentUser.GatewayLogsTokenAsync"/>). Never throws for an unreachable gateway: it returns a message.
/// </summary>
public sealed class LiveLogClient(HttpClient http, CurrentUser user, IConfiguration configuration, IServiceProvider services)
{
    private const int MaxWaitSeconds = 25;

    public string GatewayUrl => configuration["Gateway:BaseUrl"] ?? "http://localhost:5200/";

    /// <summary>Lines newer than <paramref name="after"/>. With waitSeconds > 0 it waits until a line arrives (up to 25 s).</summary>
    public Task<(LogPage? Page, string? Error)> ReadAsync(LogSource source, long after, int waitSeconds, CancellationToken ct) =>
        source == LogSource.Admin ? ReadOwnAsync(after, waitSeconds, ct) : ReadGatewayAsync(after, waitSeconds, ct);

    private async Task<(LogPage?, string?)> ReadOwnAsync(long after, int waitSeconds, CancellationToken ct)
    {
        var buffer = services.GetService<LiveLogBuffer>(); // only there when LiveLogs:Enabled is true
        if (buffer is null) return (null, "has live logs turned off (set LiveLogs:Enabled to true).");

        var entries = buffer.Read(Math.Max(0, after));
        if (entries.Count == 0 && waitSeconds > 0)
        {
            await buffer.WaitForNewAsync(after, TimeSpan.FromSeconds(Math.Min(waitSeconds, MaxWaitSeconds)), ct);
            entries = buffer.Read(Math.Max(0, after));
        }
        return (new LogPage("Admin", buffer.LatestSeq, buffer.Capacity, entries.ToList()), null);
    }

    private async Task<(LogPage?, string?)> ReadGatewayAsync(long after, int waitSeconds, CancellationToken ct)
    {
        var baseUrl = GatewayUrl.TrimEnd('/');
        try
        {
            var token = await user.GatewayLogsTokenAsync();
            if (token is null) return (null, "needs a token to read its logs. Set Gateway:LogsToken, or have the sign-in issue one for the gateway.");

            using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/_admin/logs?after={after}&wait={waitSeconds}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

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
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; } // the person left the page
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return (null, $"not reachable at {baseUrl}. Is it running?");
        }
    }
}
