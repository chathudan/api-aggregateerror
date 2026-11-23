using ApiAggregator.Engine.Authoring;
using ApiAggregator.Engine.Config;

namespace ApiAggregator.Gateway;

/// <summary>
/// The OpenAPI document of every published endpoint, built once per config version. The document names the address
/// it was asked on (its "server"), which comes from the request's Host header. Anyone can send any Host, and with
/// thousands of endpoints a build takes tens of milliseconds and tens of megabytes, so only two addresses get their
/// own copy; any further one gets a single shared copy without a server (Swagger UI then calls the page's own address).
/// </summary>
public sealed class OpenApiSpecCache(bool includeInternalDetails, int maxServers = 2)
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, string> _byServer = new(StringComparer.OrdinalIgnoreCase);
    private string _version = "";
    private string? _withoutServer;

    /// <summary>How many documents were built (for tests).</summary>
    public int Builds { get; private set; }

    public string Get(ConfigSnapshot snapshot, string server)
    {
        // One build at a time: many requests right after a publish wait for one build instead of each doing their own.
        lock (_lock)
        {
            if (_version != snapshot.Version)
            {
                _byServer.Clear();
                _withoutServer = null;
                _version = snapshot.Version;
            }
            if (_byServer.TryGetValue(server, out var json)) return json;
            if (_byServer.Count < maxServers) return _byServer[server] = Build(snapshot, server);
            return _withoutServer ??= Build(snapshot, null);
        }
    }

    private string Build(ConfigSnapshot snapshot, string? server)
    {
        Builds++;
        return OpenApiDocumentBuilder.Build(snapshot, new OpenApiBuildOptions { ServerUrl = server, IncludeInternalDetails = includeInternalDetails }).ToJsonString();
    }
}
