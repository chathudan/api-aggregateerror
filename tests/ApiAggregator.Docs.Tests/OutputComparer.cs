using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ApiAggregator.Docs.Tests;

/// <summary>Compares what a guide says with what really happened, and explains any difference in plain words.</summary>
public static partial class OutputComparer
{
    /// <summary>Values that differ on every run. The guide may show any value for them, but they must be present.</summary>
    private static readonly HashSet<string> VolatileProperties = ["correlationId", "durationMs"];

    /// <summary>Differences between the expected and actual JSON, as "where: what" lines. Empty when they match.</summary>
    public static List<string> CompareJson(JsonNode? expected, JsonNode? actual, bool partial)
    {
        var differences = new List<string>();
        Compare(expected, actual, "$", partial, differences);
        return differences;
    }

    private static void Compare(JsonNode? expected, JsonNode? actual, string where, bool partial, List<string> differences)
    {
        switch (expected)
        {
            case null:
                if (actual is not null) differences.Add($"{where}: the guide says null, the output has {Short(actual)}");
                return;

            case JsonObject e when actual is JsonObject a:
                foreach (var (name, value) in e)
                {
                    if (!a.ContainsKey(name)) { differences.Add($"{where}.{name}: in the guide, missing from the output"); continue; }
                    if (!VolatileProperties.Contains(name)) Compare(value, a[name], $"{where}.{name}", partial, differences);
                }
                if (!partial)
                    foreach (var (name, value) in a)
                        if (!e.ContainsKey(name)) differences.Add($"{where}.{name}: in the output ({Short(value)}), missing from the guide");
                return;

            case JsonArray e when actual is JsonArray a:
                if (e.Count != a.Count) { differences.Add($"{where}: the guide lists {e.Count} items, the output has {a.Count}"); return; }
                for (var i = 0; i < e.Count; i++) Compare(e[i], a[i], $"{where}[{i}]", partial, differences);
                return;

            case JsonValue e when actual is JsonValue a:
                if (!SameValue(e, a)) differences.Add($"{where}: the guide says {Short(e)}, the output has {Short(a)}");
                return;

            default:
                differences.Add($"{where}: the guide says {Short(expected)}, the output has {Short(actual)}");
                return;
        }
    }

    /// <summary>Numbers compare by value (41 and 41.0 are the same); everything else by its JSON text.</summary>
    private static bool SameValue(JsonValue expected, JsonValue actual)
    {
        if (expected.TryGetValue<decimal>(out var x) && actual.TryGetValue<decimal>(out var y)) return x == y;
        return expected.ToJsonString() == actual.ToJsonString();
    }

    private static string Short(JsonNode? node)
    {
        var text = node?.ToJsonString() ?? "null";
        return text.Length <= 80 ? text : text[..77] + "...";
    }

    /// <summary>
    /// The guide's lines that don't appear in the output, in order. Each guide line must be found after the previous one.
    /// Timings ("37 ms") and runs of spaces are ignored, because they change from run to run.
    /// </summary>
    public static List<string> CompareText(string expected, string actual)
    {
        var output = actual.Split('\n').Select(Normalize).ToList();
        var missing = new List<string>();
        var from = 0;
        foreach (var line in expected.Split('\n').Select(Normalize).Where(l => l.Length > 0))
        {
            var at = output.FindIndex(from, o => o == line);
            if (at < 0) missing.Add(line);
            else from = at + 1;
        }
        return missing;
    }

    private static string Normalize(string line) => SpacesRegex().Replace(TimingRegex().Replace(line, "# ms"), " ").Trim();

    [GeneratedRegex(@"\b\d+ ms\b")] private static partial Regex TimingRegex();
    [GeneratedRegex(@"\s+")] private static partial Regex SpacesRegex();
}
