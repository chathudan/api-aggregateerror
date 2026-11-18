using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace ApiAggregator.Engine.Expressions;

/// <summary>Gives a path its root values: "request", step aliases, "item", "ctx".</summary>
public interface IValueScope
{
    JsonNode? GetRoot(string name);
}

/// <summary>
/// A tiny, safe path language. No code execution, no reflection, no regex backtracking.
///
///   request.route.id            property access
///   a.body.items[0]             index (negative counts from the end)
///   a.body.items[0:5].id        slice, then project "id" over each item
///   d[*].body                   wildcard projection
///   a.headers['X-Trace-Id']     quoted property
///   a.body.ids | join(',')      transforms (see Transforms)
///
/// Parsed paths are cached, so evaluation is allocation-light and fast.
/// </summary>
public sealed class ValuePath
{
    private static readonly ConcurrentDictionary<string, ValuePath> Cache = new(StringComparer.Ordinal);

    public string Text { get; }
    public string Root { get; }

    /// <summary>The second segment name, e.g. "route" in request.route.id (used by validation).</summary>
    public string? FirstProperty => _segments.Length > 0 ? _segments[0].Name : null;
    public string? SecondProperty => _segments.Length > 1 ? _segments[1].Name : null;
    public IReadOnlyList<TransformCall> TransformCalls => _transforms;
    private readonly Segment[] _segments;
    private readonly TransformCall[] _transforms;

    private ValuePath(string text, string root, Segment[] segments, TransformCall[] transforms)
    {
        Text = text;
        Root = root;
        _segments = segments;
        _transforms = transforms;
    }

    public static ValuePath Parse(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        return Cache.GetOrAdd(text.Trim(), static t => ParseCore(t));
    }

    public JsonNode? Evaluate(IValueScope scope)
    {
        var current = new List<JsonNode?> { scope.GetRoot(Root) };
        var projected = false;

        foreach (var segment in _segments)
        {
            var next = new List<JsonNode?>(current.Count);
            foreach (var node in current)
            {
                switch (segment.Kind)
                {
                    case SegmentKind.Property:
                        if (node is JsonObject obj)
                        {
                            var value = GetProperty(obj, segment.Name!);
                            if (value is not null || !projected) next.Add(value);
                        }
                        else if (!projected) next.Add(null);
                        break;

                    case SegmentKind.Index:
                        if (node is JsonArray arr)
                        {
                            var i = segment.Start!.Value < 0 ? arr.Count + segment.Start.Value : segment.Start.Value;
                            next.Add(i >= 0 && i < arr.Count ? arr[i] : null);
                        }
                        else if (!projected) next.Add(null);
                        break;

                    case SegmentKind.Slice:
                    case SegmentKind.Wildcard:
                        if (node is JsonArray items)
                        {
                            var (from, to) = segment.Kind == SegmentKind.Wildcard
                                ? (0, items.Count)
                                : Range(segment.Start, segment.End, items.Count);
                            for (var i = from; i < to; i++) next.Add(items[i]);
                        }
                        break;
                }
            }

            if (segment.Kind is SegmentKind.Slice or SegmentKind.Wildcard) projected = true;
            current = next;
        }

        JsonNode? result = projected
            ? new JsonArray(current.Select(n => n?.DeepClone()).ToArray())
            : current.FirstOrDefault();

        foreach (var transform in _transforms)
            result = Transforms.Apply(transform, result);

        return result;
    }

    /// <summary>Exact match first, then case-insensitive (HTTP headers, loose JSON).</summary>
    private static JsonNode? GetProperty(JsonObject obj, string name)
    {
        if (obj.TryGetPropertyValue(name, out var value)) return value;
        foreach (var (key, v) in obj)
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase)) return v;
        return null;
    }

    private static (int From, int To) Range(int? start, int? end, int count)
    {
        static int Clamp(int v, int count) => Math.Clamp(v < 0 ? count + v : v, 0, count);
        var from = Clamp(start ?? 0, count);
        var to = Clamp(end ?? count, count);
        return (from, Math.Max(from, to));
    }

    // ---------------------------------------------------------------- parsing

    private static ValuePath ParseCore(string text)
    {
        var parts = SplitPipes(text);
        var path = parts[0].Trim();
        var transforms = parts.Skip(1).Select(TransformCall.Parse).ToArray();

        var segments = new List<Segment>();
        string? root = null;
        var i = 0;
        while (i < path.Length)
        {
            var c = path[i];
            if (c == '.') { i++; continue; }
            if (c == '[')
            {
                var close = path.IndexOf(']', i);
                if (close < 0) throw new FormatException($"Missing ']' in '{text}'.");
                segments.Add(ParseBracket(path[(i + 1)..close].Trim(), text));
                i = close + 1;
                continue;
            }

            var start = i;
            while (i < path.Length && path[i] != '.' && path[i] != '[') i++;
            var name = path[start..i];
            if (!IsName(name)) throw new FormatException($"Invalid name '{name}' in '{text}'.");
            if (root is null) root = name;
            else segments.Add(new Segment(SegmentKind.Property, name, null, null));
        }

        if (root is null) throw new FormatException($"Path '{text}' has no root.");
        return new ValuePath(text, root, segments.ToArray(), transforms);
    }

    private static Segment ParseBracket(string inner, string text)
    {
        if (inner == "*") return new Segment(SegmentKind.Wildcard, null, null, null);
        if (inner.Length >= 2 && (inner[0] == '\'' || inner[0] == '"') && inner[^1] == inner[0])
            return new Segment(SegmentKind.Property, inner[1..^1], null, null);

        var colon = inner.IndexOf(':');
        if (colon >= 0)
            return new Segment(SegmentKind.Slice, null, ParseIntOrNull(inner[..colon], text), ParseIntOrNull(inner[(colon + 1)..], text));

        return new Segment(SegmentKind.Index, null, ParseIntOrNull(inner, text) ?? throw new FormatException($"Empty index in '{text}'."), null);
    }

    private static int? ParseIntOrNull(string s, string text)
    {
        s = s.Trim();
        if (s.Length == 0) return null;
        return int.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v)
            ? v
            : throw new FormatException($"'{s}' is not a number in '{text}'.");
    }

    private static bool IsName(string s) =>
        s.Length is > 0 and <= 128 && s.All(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-' or '$');

    private static List<string> SplitPipes(string text)
    {
        var parts = new List<string>();
        var sb = new StringBuilder();
        char? quote = null;
        foreach (var c in text)
        {
            if (quote is not null) { if (c == quote) quote = null; sb.Append(c); continue; }
            if (c is '\'' or '"') { quote = c; sb.Append(c); continue; }
            if (c == '|') { parts.Add(sb.ToString()); sb.Clear(); continue; }
            sb.Append(c);
        }
        parts.Add(sb.ToString());
        return parts;
    }

    private enum SegmentKind { Property, Index, Slice, Wildcard }

    private sealed record Segment(SegmentKind Kind, string? Name, int? Start, int? End);
}
