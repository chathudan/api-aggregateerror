using System.Text.Json.Nodes;
using ApiAggregator.Engine.Config;
using ApiAggregator.Engine.Model;

namespace ApiAggregator.Admin.Services;

/// <summary>
/// Editable shape of one "combine" (merging lists from several steps). Converts to and from the engine's
/// CombineDef without losing anything: settings the wizard doesn't show (such as Nulls) are kept as loaded.
/// </summary>
public sealed class CombineForm
{
    public string Name { get; set; } = "records";
    public List<CombineSourceForm> Sources { get; } = [];
    public List<string> Normalize { get; set; } = ["trim"];
    public string OnConflict { get; set; } = "priority";
    public List<OwnerRow> Owners { get; } = [];
    public string Nulls { get; set; } = "neverOverwrite";
    public string WithoutKey { get; set; } = "keep";
    public bool TrackSources { get; set; }
    public string? SortBy { get; set; }
    public int MaxRecords { get; set; } = 5000;
    public bool Required { get; set; } = true;

    // ---- screen state only (not saved)
    public bool Open { get; set; } = true;
    public bool ShowMore { get; set; }

    // ---- friendlier views of the settings above

    public bool Trim { get => Has("trim"); set => Toggle("trim", value); }
    public bool Lower { get => Has("lower"); set => Toggle("lower", value); }

    /// <summary>The field after "newest:", or null when the rule is not "newest".</summary>
    public string? NewestField =>
        OnConflict.Trim().StartsWith("newest:", StringComparison.OrdinalIgnoreCase) ? OnConflict.Trim()["newest:".Length..].Trim() : null;
    public void FirstListWins() => OnConflict = "priority";
    public void NewestWins(string field) => OnConflict = "newest:" + field;

    public bool LeaveOutUnmatched { get => WithoutKey.Equals("drop", StringComparison.OrdinalIgnoreCase); set => WithoutKey = value ? "drop" : "keep"; }

    public string SortField { get => (SortBy ?? "").TrimStart().TrimStart('-').Trim(); set => SortBy = Compose(value, SortDescending); }
    public bool SortDescending { get => (SortBy ?? "").TrimStart().StartsWith('-'); set => SortBy = Compose(SortField, value); }

    private static string? Compose(string field, bool descending) =>
        string.IsNullOrWhiteSpace(field) ? null : (descending ? "-" : "") + field.Trim();

    private bool Has(string name) => Normalize.Contains(name, StringComparer.OrdinalIgnoreCase);
    private void Toggle(string name, bool on)
    {
        Normalize.RemoveAll(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (on) Normalize.Add(name);
    }

    /// <summary>A label not used by another list in this combine, e.g. "orders", "orders2".</summary>
    public string UniqueLabel(string wanted, CombineSourceForm? except = null)
    {
        var candidate = wanted;
        for (var n = 2; Sources.Any(s => s != except && s.Label == candidate); n++) candidate = $"{wanted}{n}";
        return candidate;
    }

    /// <summary>Changes a list's label and keeps any "choose per field" rows pointing at it.</summary>
    public void Relabel(CombineSourceForm source, string? label)
    {
        var old = source.Label;
        source.Name = label;
        foreach (var owner in Owners.Where(o => o.Source == old)) owner.Source = source.Label;
    }

    public static CombineForm From(string name, CombineDef def)
    {
        var form = new CombineForm
        {
            Name = name, Normalize = [.. def.Normalize], OnConflict = def.OnConflict, Nulls = def.Nulls, WithoutKey = def.WithoutKey,
            TrackSources = def.TrackSources, SortBy = def.SortBy, MaxRecords = def.MaxRecords, Required = def.Required,
            Open = false
        };
        foreach (var s in def.Sources)
            form.Sources.Add(new CombineSourceForm
            {
                From = s.From, Key = s.Key, Name = s.Name,
                UseVia = s.Via is not null,
                Via = s.Via is null ? new CombineViaForm() : new CombineViaForm { From = s.Via.From, Match = s.Via.Match, Use = s.Via.Use }
            });
        foreach (var (field, source) in def.Owners) form.Owners.Add(new OwnerRow { Field = field, Source = source });
        return form;
    }

    public CombineDef ToDef() => new()
    {
        Sources = Sources.Select(s => new CombineSource
        {
            From = s.From.Trim(), Key = s.Key.Trim(),
            Name = string.IsNullOrWhiteSpace(s.Name) ? null : s.Name.Trim(),
            Via = s.UseVia ? new CombineVia { From = s.Via.From.Trim(), Match = s.Via.Match.Trim(), Use = s.Via.Use.Trim() } : null
        }).ToList(),
        Normalize = [.. Normalize],
        OnConflict = OnConflict,
        Owners = Owners.Where(o => !string.IsNullOrWhiteSpace(o.Field) && !string.IsNullOrWhiteSpace(o.Source))
            .GroupBy(o => o.Field.Trim()).ToDictionary(g => g.Key, g => g.Last().Source.Trim(), StringComparer.Ordinal),
        Nulls = Nulls,
        WithoutKey = WithoutKey,
        TrackSources = TrackSources,
        SortBy = string.IsNullOrWhiteSpace(SortBy) ? null : SortBy.Trim(),
        MaxRecords = MaxRecords,
        Required = Required
    };
}

public sealed class CombineSourceForm
{
    public string From { get; set; } = "";
    public string Key { get; set; } = "";

    /// <summary>The label shown as "came from" and used by "choose per field". Empty = the step's name.</summary>
    public string? Name { get; set; }

    /// <summary>True when the services don't share an id and a mapping list links them.</summary>
    public bool UseVia { get; set; }
    public CombineViaForm Via { get; set; } = new();

    /// <summary>What the engine will call this list: its Name, or the step name at the start of From.</summary>
    public string Label => !string.IsNullOrWhiteSpace(Name) ? Name.Trim() : StepOf(From);

    public static string StepOf(string path)
    {
        var end = path.IndexOfAny(['.', '[', '|', ' ']);
        return (end < 0 ? path : path[..end]).Trim();
    }
}

public sealed class CombineViaForm
{
    public string From { get; set; } = "";
    public string Match { get; set; } = "";
    public string Use { get; set; } = "";
}

/// <summary>One "choose per field" row: this field always comes from that list.</summary>
public sealed class OwnerRow
{
    public string Field { get; set; } = "";
    public string Source { get; set; } = "";
}

/// <summary>A list a combine can use: an array in a step's response, and the fields of its items (empty when unknown).</summary>
public sealed record ListOption(string Expression, string Label, IReadOnlyList<string> Fields);

public static class ListOptions
{
    private const int MaxDepth = 3;

    /// <summary>
    /// Every array found in the response schemas of the recipe's steps, e.g. "crm.body.contacts".
    /// For-each steps are left out: their result is a list of whole responses, not a plain list of records.
    /// </summary>
    public static List<ListOption> For(RecipeForm form, ConfigSnapshot snapshot)
    {
        var options = new List<ListOption>();
        foreach (var step in form.Steps.Where(s => string.IsNullOrWhiteSpace(s.ForEach) && !string.IsNullOrWhiteSpace(s.Alias)))
        {
            if (snapshot.FindEndpoint(step.Endpoint, out _)?.ResponseSchema is not JsonObject schema) continue;
            var label = string.IsNullOrWhiteSpace(step.DisplayName) ? step.Alias : step.DisplayName;
            Walk(schema, $"{step.Alias}.body", label, "", 0, options);
        }
        return options;
    }

    private static void Walk(JsonObject schema, string path, string stepLabel, string relative, int depth, List<ListOption> options)
    {
        if (schema["type"]?.ToString() == "array")
        {
            var fields = schema["items"]?["properties"] is JsonObject props ? props.Select(p => p.Key).ToList() : [];
            options.Add(new ListOption(path, $"{stepLabel} → {(relative.Length == 0 ? "whole response" : relative)}", fields));
            return; // lists inside lists aren't offered
        }
        if (depth >= MaxDepth || schema["properties"] is not JsonObject properties) return;

        foreach (var (name, child) in properties)
            if (child is JsonObject childSchema && name.All(c => char.IsLetterOrDigit(c) || c == '_'))
                Walk(childSchema, $"{path}.{name}", stepLabel, relative.Length == 0 ? name : $"{relative}.{name}", depth + 1, options);
    }
}
