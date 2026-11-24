using System.Text;
using System.Text.RegularExpressions;

namespace ApiAggregator.Docs.Tests;

/// <summary>
/// One checked example in a guide: a marker comment, then the code block holding the output the guide promises.
/// <code>
/// &lt;!-- check: run customer-overview --route id=C-10042 --&gt;
/// ```json
/// { ...the response body... }
/// ```
/// </code>
/// Kinds:
/// <list type="bullet">
/// <item><c>run &lt;recipe&gt; [CLI options]</c>: runs <c>aggregator run</c> against the mocks.</item>
/// <item><c>validate</c>: runs <c>aggregator validate</c>.</item>
/// <item><c>gateway &lt;METHOD&gt; &lt;path&gt; [scope=a,b | token=none] [header:Name=value] [cors-headers]</c>: calls the gateway as an
/// app would. With <c>cors-headers</c> the output is the response's <c>Access-Control-*</c> and <c>Vary</c> headers, one per line.</item>
/// <item><c>mcp &lt;tool&gt; [name=value] [name=@block]</c>: calls an MCP tool on the admin app as an AI agent would (ADR 0021).
/// Values are text; <c>@block</c> is the JSON code block just before the marker (what the guide shows the agent sending).
/// A refusal is status 400.</item>
/// </list>
/// Options for every kind: <c>expect=502</c> (expected HTTP status, default 200) and <c>partial</c> (the guide shows only
/// some properties of each object). A <c>json</c> block is compared as JSON; a <c>text</c> block's lines must appear in
/// the output in that order (timings and spacing are ignored).
/// </summary>
public sealed partial record DocExample(string File, int Line, string Kind, IReadOnlyList<string> Args, string Language, string Expected)
{
    /// <summary>The code block just before the marker (not itself an example's output), for <c>name=@block</c>.</summary>
    public string? PreviousBlock { get; init; }

    public int ExpectedStatus => Option("expect") is { } s ? int.Parse(s) : 200;
    public bool Partial => Args.Contains("partial");

    /// <summary>The value of a <c>name=value</c> option, or null.</summary>
    public string? Option(string name) =>
        Args.FirstOrDefault(a => a.StartsWith(name + "=", StringComparison.Ordinal))?[(name.Length + 1)..];

    /// <summary>Arguments that aren't checker options, i.e. what goes to the CLI or makes up the request.</summary>
    public IEnumerable<string> PlainArgs => Args.Where(a => a != "partial" && !a.StartsWith("expect=", StringComparison.Ordinal));

    public override string ToString() => $"{File}:{Line}";

    /// <summary>Every example in the markdown files under <paramref name="folder"/>, in file and line order.</summary>
    public static List<DocExample> FindAll(string repoRoot, string folder)
    {
        var found = new List<DocExample>();
        foreach (var path in System.IO.Directory.EnumerateFiles(Path.Combine(repoRoot, folder), "*.md", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            found.AddRange(Parse(Path.GetRelativePath(repoRoot, path).Replace('\\', '/'), System.IO.File.ReadAllLines(path)));
        return found;
    }

    public static IEnumerable<DocExample> Parse(string file, string[] lines)
    {
        string? outerFence = null; // set while inside a code block that isn't an example, e.g. one showing how to write one
        StringBuilder? block = null;  // that block's text
        string? lastBlock = null;     // the last such block, for name=@block
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (outerFence is not null)
            {
                if (trimmed.StartsWith(outerFence, StringComparison.Ordinal) && trimmed.Trim().Trim('`').Length == 0) { outerFence = null; lastBlock = block!.ToString(); }
                else block!.AppendLine(lines[i]);
                continue;
            }
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                outerFence = new string('`', trimmed.TakeWhile(c => c == '`').Count());
                block = new StringBuilder();
                continue;
            }

            var marker = MarkerRegex().Match(lines[i]);
            if (!marker.Success) continue;

            // The code block must follow the marker, with only blank lines between.
            var open = i + 1;
            while (open < lines.Length && lines[open].Trim().Length == 0) open++;
            if (open >= lines.Length || !lines[open].TrimStart().StartsWith("```", StringComparison.Ordinal))
                throw new FormatException($"{file}:{i + 1}: a check: comment must be followed by a code block with the expected output.");

            var fence = lines[open].TrimStart();
            var language = fence[3..].Trim();
            var body = new StringBuilder();
            var close = open + 1;
            for (; close < lines.Length && !lines[close].TrimStart().StartsWith("```", StringComparison.Ordinal); close++)
                body.AppendLine(lines[close]);
            if (close >= lines.Length) throw new FormatException($"{file}:{open + 1}: the code block isn't closed.");

            var words = Split(marker.Groups[1].Value);
            if (words.Count == 0) throw new FormatException($"{file}:{i + 1}: say what to check, e.g. check: run customer-overview --route id=C-10042.");
            yield return new DocExample(file, i + 1, words[0], words.Skip(1).ToList(), language, body.ToString()) { PreviousBlock = lastBlock };
            lastBlock = null;
            i = close;
        }
    }

    /// <summary>Splits on spaces, keeping "quoted parts" together.</summary>
    private static List<string> Split(string text)
    {
        var words = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        foreach (var c in text)
        {
            if (c == '"') { quoted = !quoted; continue; }
            if (c == ' ' && !quoted) { if (current.Length > 0) { words.Add(current.ToString()); current.Clear(); } continue; }
            current.Append(c);
        }
        if (current.Length > 0) words.Add(current.ToString());
        return words;
    }

    [GeneratedRegex(@"^\s*<!--\s*check:\s*(.*?)\s*-->\s*$")]
    private static partial Regex MarkerRegex();
}
