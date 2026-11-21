using System.Text.Json.Nodes;
using ApiAggregator.Engine.Authoring;
using ApiAggregator.Engine.Config;
using ApiAggregator.Engine.Expressions;
using ApiAggregator.Engine.Model;

namespace ApiAggregator.Admin.Services;

/// <summary>What a response field returns of a list: all of it, how many, without repeats, or how many without repeats.</summary>
public enum ResponseShape { All, Count, Distinct, DistinctCount }

/// <summary>A response value split into the picked value and its shape, e.g. crm.body.contacts + Distinct by id.</summary>
public sealed record ShapedValue(string Value, ResponseShape Shape, string? By);

/// <summary>
/// "How many" and "Without repeats" for response fields, so nobody types | count or | distinct('id'). The saved text
/// stays the same expression the engine always read; these functions only read and write it. A saved value that isn't
/// exactly one of the shapes below is left alone (shown as written), so nothing is ever rewritten by accident.
/// </summary>
public static class ResponseShapes
{
    /// <summary>The choices shown next to a list value, in the order shown.</summary>
    public static readonly IReadOnlyList<(ResponseShape Shape, string Label)> Choices =
    [
        (ResponseShape.All, "All of it"),
        (ResponseShape.Count, "How many"),
        (ResponseShape.Distinct, "Without repeats"),
        (ResponseShape.DistinctCount, "How many, without repeats")
    ];

    public static bool NeedsField(ResponseShape shape) => shape is ResponseShape.Distinct or ResponseShape.DistinctCount;

    /// <summary>
    /// "crm.body.contacts | distinct('id') | count" → (crm.body.contacts, DistinctCount, id). Null when the text isn't
    /// exactly one of the shapes as this class writes them (other transforms, other spacing): then it's shown as written.
    /// </summary>
    public static ShapedValue? Split(string? expression)
    {
        var text = expression?.Trim() ?? "";
        if (text.Length == 0) return null;
        var bar = text.IndexOf('|');
        if (bar < 0) return new ShapedValue(text, ResponseShape.All, null);

        IReadOnlyList<TransformCall> calls;
        try { calls = ValuePath.Parse(text).TransformCalls; }
        catch (FormatException) { return null; }

        ShapedValue? shaped = calls switch
        {
            [{ Name: "count", Argument: null }] => new(text[..bar].TrimEnd(), ResponseShape.Count, null),
            [{ Name: "distinct", Argument: { } by }] => new(text[..bar].TrimEnd(), ResponseShape.Distinct, by),
            [{ Name: "distinct", Argument: { } by }, { Name: "count", Argument: null }] => new(text[..bar].TrimEnd(), ResponseShape.DistinctCount, by),
            _ => null
        };
        // Only a value that this class would write back exactly the same is taken apart.
        return shaped is not null && Compose(shaped) == text ? shaped : null;
    }

    /// <summary>Writes the expression back. A "by" field that isn't a plain field name is left out (the picker shows a problem).</summary>
    public static string Compose(ShapedValue v) => v.Shape switch
    {
        ResponseShape.Count => $"{v.Value} | count",
        ResponseShape.Distinct when IsPlainField(v.By) => $"{v.Value} | distinct('{v.By}')",
        ResponseShape.DistinctCount when IsPlainField(v.By) => $"{v.Value} | distinct('{v.By}') | count",
        _ => v.Value
    };

    /// <summary>Letters, digits, _, - and $ (dots for nested fields): nothing that could close the quotes around it.</summary>
    public static bool IsPlainField(string? field) =>
        !string.IsNullOrWhiteSpace(field) && field.Length <= 128
        && field.Split('.').All(part => part.Length > 0 && part.All(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '$'));

    /// <summary>True when the value is a list, so the shapes make sense: the schema says array, or the path takes every item ([*]).</summary>
    public static bool IsList(Recipe recipe, string value, ConfigSnapshot config)
    {
        if (!TryParse(value, out var path)) return false;
        if (path.Segments.Any(s => s.Kind is ValuePath.SegmentKind.Wildcard or ValuePath.SegmentKind.Slice)) return true;
        return SourceCatalog.SchemaAt(recipe, path, config)?["type"]?.ToString() == "array";
    }

    /// <summary>The fields of one item of the list, for "without repeats, by…" (empty when the schema doesn't say).</summary>
    public static IReadOnlyList<string> ItemFields(Recipe recipe, string value, ConfigSnapshot config)
    {
        if (!TryParse(value, out var path)) return [];
        var schema = SourceCatalog.SchemaAt(recipe, path, config);
        if (schema?["type"]?.ToString() == "array") schema = schema["items"];
        return schema?["properties"] is JsonObject props ? props.Select(p => p.Key).Where(IsPlainField).ToList() : [];
    }

    private static bool TryParse(string value, out ValuePath path)
    {
        path = null!;
        try { path = ValuePath.Parse(value); return path.TransformCalls.Count == 0; }
        catch (Exception ex) when (ex is FormatException or ArgumentException) { return false; }
    }
}
