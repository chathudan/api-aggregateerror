using System.Text.Json;
using System.Text.Json.Nodes;
using ApiAggregator.Engine.Authoring;
using ApiAggregator.Engine.Expressions;
using ApiAggregator.Engine.Model;

namespace ApiAggregator.Admin.Services;

/// <summary>
/// Editable form model behind the wizard. The engine's Recipe is immutable (init-only);
/// this class is the mutable, UI-friendly shape, converted with FromRecipe / ToRecipe.
/// </summary>
public sealed class RecipeForm
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    // ---- kept as loaded (the store sets them on save and publish), so a round trip changes nothing
    public int Version { get; set; } = 1;
    public RecipeStatus Status { get; set; } = RecipeStatus.Draft;

    public string Method { get; set; } = "GET";
    public string Route { get; set; } = "";
    public bool RequireAuthentication { get; set; } = true;
    public string? RequiredScope { get; set; }
    public bool CacheEnabled { get; set; }
    public int CacheTtlSeconds { get; set; } = 30;
    public bool CachePerCaller { get; set; } = true;
    public int TimeoutMs { get; set; } = 10_000;
    public List<InputForm> Inputs { get; } = [];
    public List<StepForm> Steps { get; } = [];
    public List<ResponseField> Response { get; } = [];

    /// <summary>A response that isn't a plain object of fields (a list, a single value): kept exactly as written.</summary>
    public JsonNode? RawResponse { get; set; }

    /// <summary>The response was written as an empty object, {}, rather than left out.</summary>
    public bool EmptyResponseObject { get; set; }

    /// <summary>Lists merged by key (see CombineForm). Used in the response as combine.&lt;name&gt;.</summary>
    public List<CombineForm> Combines { get; } = [];

    public static RecipeForm FromRecipe(Recipe r)
    {
        var form = new RecipeForm
        {
            Id = r.Id, Name = r.Name, Version = r.Version, Status = r.Status, Method = r.Method, Route = r.Route,
            RequireAuthentication = r.RequireAuthentication, RequiredScope = r.RequiredScope,
            CacheEnabled = r.Cache.Enabled, CacheTtlSeconds = r.Cache.TtlSeconds, CachePerCaller = r.Cache.PerCaller, TimeoutMs = r.TimeoutMs
        };
        foreach (var (name, def) in r.Combine) form.Combines.Add(CombineForm.From(name, def));
        foreach (var (key, def) in r.Inputs)
        {
            var parts = key.Split('.', 2);
            form.Inputs.Add(new InputForm
            {
                Source = parts[0], Name = parts.Length > 1 ? parts[1] : "", Type = def.Type, Required = def.Required, Default = def.Default, Pattern = def.Pattern,
                Enum = def.Enum, Min = def.Min, Max = def.Max, MaxLength = def.MaxLength
            });
        }
        foreach (var s in r.Steps)
        {
            var step = new StepForm
            {
                Alias = s.Alias, DisplayName = s.DisplayName ?? s.Alias, NoDisplayName = s.DisplayName is null, Endpoint = s.Endpoint,
                Required = s.Required, When = s.When, ForEach = s.ForEach, Distinct = s.Distinct, MaxConcurrency = s.MaxConcurrency,
                TimeoutMs = s.TimeoutMs, CacheEnabled = s.Cache.Enabled, CacheTtlSeconds = s.Cache.TtlSeconds, CachePerCaller = s.Cache.PerCaller
            };
            foreach (var b in s.Bind)
                step.Bindings[b.To] = BindingForm.From(b);
            form.Steps.Add(step);
        }
        switch (r.Response)
        {
            case JsonObject { Count: 0 }:
                form.EmptyResponseObject = true;
                break;
            case JsonObject response:
                foreach (var (name, value) in response) form.Response.Add(ResponseField.From(name, value));
                break;
            case not null:
                form.RawResponse = r.Response.DeepClone();
                break;
        }
        return form;
    }

    public Recipe ToRecipe() => new()
    {
        Id = Id.Trim(),
        Name = Name.Trim(),
        Version = Version,
        Status = Status,
        Method = Method,
        Route = Route.Trim().Trim('/'),
        RequireAuthentication = RequireAuthentication,
        RequiredScope = string.IsNullOrWhiteSpace(RequiredScope) ? null : RequiredScope.Trim(),
        TimeoutMs = TimeoutMs,
        Combine = ToCombineDictionary(),
        Cache = new CacheSettings { Enabled = CacheEnabled, TtlSeconds = CacheTtlSeconds, PerCaller = CachePerCaller },
        Inputs = Inputs.Where(i => !string.IsNullOrWhiteSpace(i.Name)).ToDictionary(
            i => $"{i.Source}.{i.Name.Trim()}",
            i => new InputDef
            {
                Type = i.Type, Required = i.Required, Default = Blank(i.Default), Pattern = Blank(i.Pattern),
                Enum = i.Enum, Min = i.Min, Max = i.Max, MaxLength = i.MaxLength
            },
            StringComparer.OrdinalIgnoreCase),
        Steps = Steps.Select(s => new StepDef
        {
            Alias = s.Alias,
            // A step loaded without a display name keeps none until someone gives it one.
            DisplayName = s.NoDisplayName && s.DisplayName == s.Alias ? null : s.DisplayName,
            Endpoint = s.Endpoint,
            Required = s.Required,
            When = Blank(s.When),
            ForEach = Blank(s.ForEach),
            Distinct = s.Distinct,
            MaxConcurrency = s.MaxConcurrency,
            TimeoutMs = s.TimeoutMs,
            Cache = new CacheSettings { Enabled = s.CacheEnabled, TtlSeconds = s.CacheTtlSeconds, PerCaller = s.CachePerCaller },
            Bind = s.Bindings
                .Where(kv => kv.Value.Mode != BindingMode.None)
                .Select(kv => kv.Value.ToDef(kv.Key))
                .ToList()
        }).ToList(),
        Response = ToResponse()
    };

    private JsonNode? ToResponse()
    {
        if (RawResponse is not null) return RawResponse.DeepClone();
        var fields = Response.Where(f => !string.IsNullOrWhiteSpace(f.Name) && (f.Raw is not null || !string.IsNullOrWhiteSpace(f.Expression))).ToList();
        if (fields.Count == 0) return EmptyResponseObject ? new JsonObject() : null;
        return new JsonObject(fields.Select(f => KeyValuePair.Create(f.Name.Trim(), f.ToNode())));
    }

    private Dictionary<string, CombineDef> ToCombineDictionary()
    {
        var result = new Dictionary<string, CombineDef>(StringComparer.OrdinalIgnoreCase);
        foreach (var combine in Combines)
        {
            var name = combine.Name.Trim();
            // The screen shows this as a problem under "Things to fix" instead of silently dropping one of them.
            if (!result.TryAdd(name, combine.ToDef()))
                throw new InvalidOperationException($"Two combined lists are both called '{name}'. Give each its own name.");
        }
        return result;
    }

    /// <summary>A name for a new combined list that no other one uses: records, records2, ...</summary>
    public string NewCombineName(string wanted = "records")
    {
        var candidate = wanted;
        for (var n = 2; Combines.Any(c => c.Name.Trim().Equals(candidate, StringComparison.OrdinalIgnoreCase)); n++) candidate = $"{wanted}{n}";
        return candidate;
    }

    /// <summary>Turns "Get customer" into a short unique alias such as "getCustomer" (the rule is the engine's, RecipeRefactor.AliasFor).</summary>
    public string NewAlias(string displayName, StepForm? except = null) =>
        RecipeRefactor.AliasFor(displayName, Steps.Where(s => s != except).Select(s => s.Alias));

    /// <summary>How many places use a step's result (0 when the form can't be read as a recipe yet).</summary>
    public int References(string alias)
    {
        try { return RecipeRefactor.References(ToRecipe(), alias); }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException) { return 0; }
    }

    /// <summary>
    /// Renames a step and every place that uses it. The engine decides whether the new name is allowed and what counts
    /// as a use (RecipeRefactor.RenameStep); the same rewrite is applied here to the form, so what's on the screen
    /// (test values, "suggested" tags, picker choices) stays as it is. Returns a plain-English problem, or null.
    /// </summary>
    public string? RenameStep(string oldAlias, string newAlias)
    {
        newAlias = newAlias.Trim();
        RenameResult check;
        try { check = RecipeRefactor.RenameStep(ToRecipe(), oldAlias, newAlias); }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException) { return $"Fix this first: {ex.Message}"; }
        if (!check.Renamed) return check.Problem;
        if (oldAlias == newAlias) return null;

        string Path(string? p) => string.IsNullOrEmpty(p) ? p ?? "" : RecipeRefactor.RewriteRoot(p, oldAlias, newAlias);
        string? Text(string? t) => string.IsNullOrEmpty(t) ? t : RecipeRefactor.RewriteTemplate(t, oldAlias, newAlias);

        foreach (var step in Steps)
        {
            if (step.Alias == oldAlias) step.Alias = newAlias;
            step.When = Text(step.When);
            step.ForEach = Text(step.ForEach);
            if (!string.IsNullOrEmpty(step.ConditionField)) step.ConditionField = Path(step.ConditionField);
            foreach (var binding in step.Bindings.Values)
            {
                if (!string.IsNullOrEmpty(binding.Expression)) binding.Expression = Path(binding.Expression);
                if (binding.FixedValue is not { } fixedValue || !fixedValue.Contains("{{", StringComparison.Ordinal)) continue;
                binding.FixedValue = binding.FixedIsJson
                    ? RecipeRefactor.RewriteTemplate(JsonNode.Parse(fixedValue), oldAlias, newAlias)?.ToJsonString() ?? fixedValue
                    : Text(fixedValue);
            }
        }

        foreach (var field in Response)
        {
            if (!string.IsNullOrEmpty(field.Expression)) field.Expression = Path(field.Expression);
            if (field.Raw is not null) field.Raw = RecipeRefactor.RewriteTemplate(field.Raw, oldAlias, newAlias);
        }
        if (RawResponse is not null) RawResponse = RecipeRefactor.RewriteTemplate(RawResponse, oldAlias, newAlias);

        foreach (var combine in Combines)
            foreach (var source in combine.Sources)
            {
                var before = source.From;
                source.From = Path(source.From);
                // A list without its own name is called after its step: the "choose per field" rows follow it.
                if (string.IsNullOrWhiteSpace(source.Name) && before != source.From)
                    foreach (var owner in combine.Owners.Where(o => o.Source == oldAlias)) owner.Source = newAlias;
                if (!string.IsNullOrEmpty(source.Via.From)) source.Via.From = Path(source.Via.From);
            }
        return null;
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

public sealed class InputForm
{
    public string Source { get; set; } = "query";
    public string Name { get; set; } = "";
    public string Type { get; set; } = "string";
    public bool Required { get; set; }
    public string? Default { get; set; }
    public string? Pattern { get; set; }

    // ---- not shown in the wizard; kept as loaded
    public string[]? Enum { get; set; }
    public double? Min { get; set; }
    public double? Max { get; set; }
    public int MaxLength { get; set; } = 200;

    public string? TestValue { get; set; }
}

public sealed class StepForm
{
    public string Alias { get; set; } = "";
    public string DisplayName { get; set; } = "";

    /// <summary>The step was loaded without a display name (the wizard shows its alias instead).</summary>
    public bool NoDisplayName { get; set; }
    public string Endpoint { get; set; } = "";
    public bool Required { get; set; } = true;
    public string? When { get; set; }
    public string? ForEach { get; set; }

    /// <summary>Only meaningful with ForEach: call each unique item once.</summary>
    public bool Distinct { get; set; } = true;

    // ---- not shown in the wizard; kept as loaded
    public int MaxConcurrency { get; set; } = 4;
    public int? TimeoutMs { get; set; }
    public bool CachePerCaller { get; set; } = true;

    public bool CacheEnabled { get; set; }
    public int CacheTtlSeconds { get; set; } = 30;
    public bool ShowAdvanced { get; set; }

    // ---- picker state (screen only; ForEach and When above are what is saved)
    public LoopMode LoopMode { get; set; }
    public ConditionMode ConditionMode { get; set; }
    public string? ConditionField { get; set; }
    public ConditionOp ConditionOp { get; set; } = ConditionOp.Is;
    public string? ConditionValue { get; set; }

    /// <summary>A rename waiting for "Also rename…?" (the new display name's alias), or a refusal to show.</summary>
    public string? PendingAlias { get; set; }
    public string? RenameProblem { get; set; }

    /// <summary>Key = target, e.g. "path.customerId" or "header.X-Customer-Id".</summary>
    public Dictionary<string, BindingForm> Bindings { get; } = new(StringComparer.OrdinalIgnoreCase);
}

public enum BindingMode { None, FromSource, FixedValue }

public sealed class BindingForm
{
    public BindingMode Mode { get; set; }

    /// <summary>Filled in automatically; shown with a "suggested" tag until the user confirms or changes it.</summary>
    public bool Suggested { get; set; }
    public string? Reason { get; set; }
    public string? Expression { get; set; }
    public string? FixedValue { get; set; }

    /// <summary>The fixed value is JSON (an object, list, number...), not text: FixedValue holds its JSON.</summary>
    public bool FixedIsJson { get; set; }

    /// <summary>Sent when the value turns out empty. Not shown in the wizard; kept as loaded.</summary>
    public string? Default { get; set; }

    public static BindingForm From(BindingDef b) => b switch
    {
        { From: not null } => new BindingForm { Mode = BindingMode.FromSource, Expression = b.From, Default = b.Default },
        { Template: JsonValue v } when v.GetValueKind() == JsonValueKind.String =>
            new BindingForm { Mode = BindingMode.FixedValue, FixedValue = v.GetValue<string>(), Default = b.Default },
        _ => new BindingForm { Mode = BindingMode.FixedValue, FixedValue = b.Template?.ToJsonString(), FixedIsJson = b.Template is not null, Default = b.Default }
    };

    public BindingDef ToDef(string target) => Mode == BindingMode.FromSource
        ? new BindingDef { To = target, From = Expression, Default = Default }
        : new BindingDef { To = target, Template = FixedTemplate(target), Default = Default };

    private JsonNode? FixedTemplate(string target)
    {
        if (!FixedIsJson) return JsonValue.Create(FixedValue ?? "");
        try { return JsonNode.Parse(FixedValue ?? "null"); }
        catch (JsonException) { throw new FormatException($"The value for '{target}' isn't valid JSON. Check the brackets, quotes and commas."); }
    }
}

public sealed class ResponseField
{
    public string Name { get; set; } = "";
    public string Expression { get; set; } = "";

    /// <summary>A value that isn't exactly one "{{ value }}" (text around it, a number, a nested object): kept as written.</summary>
    public JsonNode? Raw { get; set; }

    public static ResponseField From(string name, JsonNode? value)
    {
        // "{{a.body.name}}" is shown as a pick from the list; anything else is kept exactly as it was written.
        if (value is JsonValue v && v.GetValueKind() == JsonValueKind.String && v.GetValue<string>() is var text
            && Template.IsSingleExpression(text) && text == $"{{{{{text[2..^2].Trim()}}}}}")
            return new ResponseField { Name = name, Expression = text[2..^2].Trim() };
        return new ResponseField { Name = name, Raw = value?.DeepClone() ?? JsonValue.Create((string?)null) };
    }

    public JsonNode? ToNode() => Raw is not null ? Raw.DeepClone() : JsonValue.Create($"{{{{{Expression.Trim()}}}}}");
}
