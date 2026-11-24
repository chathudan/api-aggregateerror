using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ApiAggregator.Docs.Tests;

/// <summary>
/// Every example marked with a check: comment in docs/ gives the output the guide shows. When one fails, either the
/// product changed (update the guide) or the guide is wrong (fix it). The message says which, where and how.
/// </summary>
public sealed partial class GuideExampleTests(DocsStack stack) : IClassFixture<DocsStack>
{
    public static TheoryData<string, int> Examples()
    {
        var data = new TheoryData<string, int>();
        foreach (var example in DocExample.FindAll(DocsStack.RepoRoot, "docs")) data.Add(example.File, example.Line);
        return data;
    }

    [Theory]
    [MemberData(nameof(Examples))]
    public async Task The_guide_shows_the_real_output(string file, int line)
    {
        var example = DocExample.Parse(file, File.ReadAllLines(Path.Combine(DocsStack.RepoRoot, file))).Single(e => e.Line == line);

        var (status, output, body) = example.Kind switch
        {
            "run" => await RunAsync(example),
            "validate" => await ValidateAsync(),
            "gateway" => await CallGatewayAsync(example),
            "mcp" => await CallToolAsync(example),
            _ => throw new FormatException($"{example}: unknown check '{example.Kind}'. Use run, validate, gateway or mcp.")
        };

        var problems = new List<string>();
        if (status != example.ExpectedStatus)
            problems.Add($"HTTP status: the guide expects {example.ExpectedStatus}, it was {status}. (Add expect={status} to the check: comment if that's intended.)");

        switch (example.Language)
        {
            case "json":
                JsonNode? actual;
                try { actual = body is null ? null : JsonNode.Parse(body); }
                catch (System.Text.Json.JsonException) { problems.Add("The output isn't JSON."); break; }
                problems.AddRange(OutputComparer.CompareJson(JsonNode.Parse(example.Expected), actual, example.Partial));
                break;
            case "text" or "":
                problems.AddRange(OutputComparer.CompareText(example.Expected, output).Select(l => $"Not in the output: {l}"));
                break;
            default:
                problems.Add($"Use a json or text code block for the expected output, not '{example.Language}'.");
                break;
        }

        Assert.True(problems.Count == 0,
            $"{example} ({example.Kind} {string.Join(' ', example.Args)}) doesn't match the real output:\n  " +
            string.Join("\n  ", problems) + $"\n\nReal output:\n{output}");
    }

    // ------------------------------------------------------------------ kinds

    private async Task<(int, string, string?)> RunAsync(DocExample example)
    {
        var (exit, raw) = await stack.RunCliAsync(["run", .. example.PlainArgs]);
        var output = stack.AsDocumented(raw);
        if (exit != 0) return (-exit, output, null);

        // The last "--- run n: HTTP 200 ..." line has the status; the JSON body is printed after the step lines.
        var lines = output.Split('\n');
        var last = Array.FindLastIndex(lines, l => l.StartsWith("--- run ", StringComparison.Ordinal));
        if (last < 0) return (0, output, null);
        var status = int.Parse(RunStatusRegex().Match(lines[last]).Groups[1].Value);
        var start = Array.FindIndex(lines, last + 1, l => l.StartsWith('{') || l.StartsWith('[') || l.Trim() == "null");
        return (status, output, start < 0 ? null : string.Join('\n', lines[start..]));
    }

    /// <summary>
    /// check: mcp &lt;tool&gt; name=value name=@block: an AI agent's tool call (ADR 0021). A guide's tool calls build on each
    /// other (a sign-in, then the service, then the draft) but tests run in any order, so the earlier calls of the same
    /// guide run first, once per stack. They are all safe to repeat.
    /// </summary>
    private async Task<(int, string, string?)> CallToolAsync(DocExample example)
    {
        var earlier = DocExample.Parse(example.File, File.ReadAllLines(Path.Combine(DocsStack.RepoRoot, example.File)))
            .Where(e => e.Kind == "mcp" && e.Line < example.Line);
        foreach (var before in earlier)
            if (stack.FirstTime($"{before.File}:{before.Line}")) await RunToolAsync(before);
        stack.FirstTime($"{example.File}:{example.Line}");
        return await RunToolAsync(example);
    }

    private async Task<(int, string, string?)> RunToolAsync(DocExample example)
    {
        var words = example.PlainArgs.ToList();
        if (words.Count == 0) throw new FormatException($"{example}: write check: mcp <tool> [name=value] [name=@block].");
        var arguments = new Dictionary<string, object?>();
        foreach (var pair in words.Skip(1).Select(w => w.Split('=', 2)))
        {
            if (pair.Length != 2) throw new FormatException($"{example}: '{pair[0]}' should be name=value.");
            arguments[pair[0]] = pair[1] == "@block"
                ? JsonDocument.Parse(stack.AsRunning(example.PreviousBlock ?? throw new FormatException($"{example}: {pair[0]}=@block needs a JSON code block just before the check: comment."))).RootElement.Clone()
                : stack.AsRunning(pair[1]);
        }
        var (status, text) = await stack.CallToolAsync(words[0], arguments);
        var output = stack.AsDocumented(text);
        return (status, output, status == 200 ? output : null);
    }

    private async Task<(int, string, string?)> ValidateAsync()
    {
        var (exit, output) = await stack.RunCliAsync(["validate"]);
        return (exit == 0 ? 200 : 400, stack.AsDocumented(output), null);
    }

    private async Task<(int, string, string?)> CallGatewayAsync(DocExample example)
    {
        var words = example.PlainArgs.ToList();
        if (words.Count < 2) throw new FormatException($"{example}: write check: gateway GET /bff/... [scope=...].");
        var scope = example.Option("token") == "none" ? null : example.Option("scope") ?? "customer.read";
        var headers = words.Where(w => w.StartsWith("header:", StringComparison.Ordinal))
            .Select(w => w["header:".Length..].Split('=', 2)).Select(p => (p[0], p[1]));
        var (status, body, cors) = await stack.CallGatewayAsync(words[0], words[1], scope, headers);
        // cors-headers: the guide shows the browser-access headers instead of the body (e.g. a preflight, which has none).
        if (words.Contains("cors-headers")) return (status, string.Join("\n", cors), null);
        return (status, body, body.Length == 0 ? null : body);
    }

    [GeneratedRegex(@"HTTP (\d{3})")] private static partial Regex RunStatusRegex();
}
