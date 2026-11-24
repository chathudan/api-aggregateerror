using System.Text.Json;
using System.Text.Json.Nodes;
using ApiAggregator.Admin.Application;
using ApiAggregator.Engine.Auth;
using ApiAggregator.Engine.Config;
using ApiAggregator.Engine.Model;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ApiAggregator.Admin.Mcp;

/// <summary>A plain-English refusal for the agent: AdminService's message, or a tool's own (bad input, too large).</summary>
internal sealed class McpRefusal(string message) : Exception(message);

/// <summary>What every MCP tool needs: who is asking, results as JSON text, refusals in plain English, size limits.</summary>
internal static class McpCall
{
    /// <summary>
    /// Runs a tool. A refusal becomes an error result with its plain message. Anything unexpected becomes an error result
    /// with a reference, logged by type only: exception messages can quote service data, and the SDK's own handling
    /// would log them.
    /// </summary>
    public static async Task<CallToolResult> Run(ILogger log, string tool, Func<Task<string>> work)
    {
        try
        {
            return new CallToolResult { Content = [new TextContentBlock { Text = await work() }] };
        }
        catch (McpRefusal refusal)
        {
            return Error(refusal.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var reference = Guid.NewGuid().ToString("n")[..12];
            log.LogError("MCP tool {Tool} failed ({Error}), reference {Reference}", tool, ex.GetType().Name, reference);
            return Error($"Something went wrong on the server (reference {reference}). Try again; if it keeps happening, tell a person the reference.");
        }
    }

    public static Task<CallToolResult> Run(ILogger log, string tool, Func<string> work) => Run(log, tool, () => Task.FromResult(work()));

    private static CallToolResult Error(string message) => new() { IsError = true, Content = [new TextContentBlock { Text = message }] };

    public const int MaxRecipeChars = 256 * 1024;
    public const int MaxSpecChars = 2 * 1024 * 1024;

    /// <summary>The person behind the token, working via MCP with this client (a label for the activity log).</summary>
    public static AdminActor Actor(RequestContext<CallToolRequestParams> context) =>
        AdminActor.From(context.User) with { Via = ChangeChannel.Mcp, Client = ClientName(context.Server) };

    /// <summary>The name the client gives itself, cleaned up: printable, at most 60 characters. Never trusted for anything.</summary>
    public static string ClientName(McpServer? server)
    {
        var name = new string((server?.ClientInfo?.Name ?? "").Where(c => !char.IsControl(c)).Take(60).ToArray()).Trim();
        return name.Length == 0 ? "unknown client" : name;
    }

    /// <summary>The caller as the engine sees it for a try: the person, without their token (it is never passed on from MCP).</summary>
    public static CallerContext Caller(RequestContext<CallToolRequestParams> context) =>
        new() { Subject = context.User?.FindFirst("sub")?.Value };

    /// <summary>The value as JSON (the app's form: camelCase, enums as words), or the refusal as an MCP error.</summary>
    public static string Json<T>(AdminResult<T> result) =>
        result.Failure is { } failure ? throw new McpRefusal(failure.Message) : JsonSerializer.Serialize(result.Value, Output);

    public static string Json<T>(T value) => JsonSerializer.Serialize(value, Output);

    /// <summary>The app's JSON form, with readable text (an apostrophe stays one): read by a model, never put into HTML.</summary>
    private static readonly JsonSerializerOptions Output = new(AggregatorJson.Options) { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Endpoint JSON from the agent, read the way the config files are, within the size limit.</summary>
    public static Recipe Recipe(JsonElement json)
    {
        var text = json.GetRawText();
        if (text.Length > MaxRecipeChars) throw new McpRefusal($"The endpoint JSON is too large (over {MaxRecipeChars / 1024} KB).");
        try
        {
            return JsonSerializer.Deserialize<Recipe>(text, AggregatorJson.Options)
                   ?? throw new McpRefusal("Give the endpoint as a JSON object. get_recipe_schema shows its shape.");
        }
        catch (JsonException ex)
        {
            // The path in the JSON, not the message: it can quote the text.
            throw new McpRefusal($"That isn't valid endpoint JSON{(ex.Path is { Length: > 0 } at ? $" (at {at})" : "")}. get_recipe_schema shows its shape; id and route are required, and each step needs alias and endpoint.");
        }
    }

    public static T Parse<T>(JsonElement json, string what, int maxChars)
    {
        var text = json.GetRawText();
        if (text.Length > maxChars) throw new McpRefusal($"The {what} is too large.");
        try { return JsonSerializer.Deserialize<T>(text, AggregatorJson.Options) ?? throw new McpRefusal($"Give the {what} as JSON."); }
        catch (JsonException ex) { throw new McpRefusal($"That isn't a valid {what}{(ex.Path is { Length: > 0 } at ? $" (at {at})" : "")}."); }
    }

    public static Dictionary<string, string?>? Values(JsonElement? json, string what) =>
        json is { ValueKind: JsonValueKind.Object } o ? Parse<Dictionary<string, string?>>(o, what, 16 * 1024) : null;

    public static JsonNode Node(JsonElement json) => JsonNode.Parse(json.GetRawText())!;
}
