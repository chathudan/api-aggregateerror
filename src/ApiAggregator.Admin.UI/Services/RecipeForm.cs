using System.Text.Json.Nodes;
using ApiAggregator.Engine.Model;

namespace ApiAggregator.Admin.UI.Services;

/// <summary>
/// Editable form model behind the wizard. The engine's Recipe is immutable (init-only);
/// this class is the mutable, UI-friendly shape, converted with FromRecipe / ToRecipe.
/// </summary>
public sealed class RecipeForm
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Method { get; set; } = "GET";
    public string Route { get; set; } = "";
    public bool RequireAuthentication { get; set; } = true;
    public string? RequiredScope { get; set; }
    public bool CacheEnabled { get; set; }
    public int CacheTtlSeconds { get; set; } = 30;
    public int TimeoutMs { get; set; } = 10_000;
    public List<InputForm> Inputs { get; } = [];
    public List<StepForm> Steps { get; } = [];
    public List<ResponseField> Response { get; } = [];

    /// <summary>Lists merged by key (see CombineForm). Used in the response as combine.&lt;name&gt;.</summary>
    public List<CombineForm> Combines { get; } = [];

    public static RecipeForm FromRecipe(Recipe r)
    {
        var form = new RecipeForm
        {
            Id = r.Id, Name = r.Name, Method = r.Method, Route = r.Route, RequireAuthentication = r.RequireAuthentication,
            RequiredScope = r.RequiredScope, CacheEnabled = r.Cache.Enabled, CacheTtlSeconds = r.Cache.TtlSeconds, TimeoutMs = r.TimeoutMs
        };
        foreach (var (name, def) in r.Combine) form.Combines.Add(CombineForm.From(name, def));
        foreach (var (key, def) in r.Inputs)
        {
            var parts = key.Split('.', 2);
            form.Inputs.Add(new InputForm { Source = parts[0], Name = parts[1], Type = def.Type, Required = def.Required, Default = def.Default, Pattern = def.Pattern });
        }
        foreach (var s in r.Steps)
        {
            var step = new StepForm
            {
                Alias = s.Alias, DisplayName = s.DisplayName ?? s.Alias, Endpoint = s.Endpoint, Required = s.Required,
                When = s.When, ForEach = s.ForEach, Distinct = s.Distinct, CacheEnabled = s.Cache.Enabled, CacheTtlSeconds = s.Cache.TtlSeconds
            };
            foreach (var b in s.Bind)
                step.Bindings[b.To] = b.From is not null
                    ? new BindingForm { Mode = BindingMode.FromSource, Expression = b.From }
                    : new BindingForm { Mode = BindingMode.FixedValue, FixedValue = b.Template?.ToString() };
            form.Steps.Add(step);
        }
        if (r.Response is JsonObject response)
            foreach (var (name, value) in response)
                form.Response.Add(new ResponseField { Name = name, Expression = Unwrap(value?.ToString()) });
        return form;
    }

    public Recipe ToRecipe() => new()
    {
        Id = Id.Trim(),
        Name = Name.Trim(),
        Method = Method,
        Route = Route.Trim().Trim('/'),
        RequireAuthentication = RequireAuthentication,
        RequiredScope = string.IsNullOrWhiteSpace(RequiredScope) ? null : RequiredScope.Trim(),
        TimeoutMs = TimeoutMs,
        Combine = ToCombineDictionary(),
        Cache = new CacheSettings { Enabled = CacheEnabled, TtlSeconds = CacheTtlSeconds },
        Inputs = Inputs.Where(i => !string.IsNullOrWhiteSpace(i.Name)).ToDictionary(
            i => $"{i.Source}.{i.Name.Trim()}",
            i => new InputDef { Type = i.Type, Required = i.Required, Default = Blank(i.Default), Pattern = Blank(i.Pattern) },
            StringComparer.OrdinalIgnoreCase),
        Steps = Steps.Select(s => new StepDef
        {
            Alias = s.Alias,
            DisplayName = s.DisplayName,
            Endpoint = s.Endpoint,
            Required = s.Required,
            When = Blank(s.When),
            ForEach = Blank(s.ForEach),
            Distinct = s.Distinct,
            Cache = new CacheSettings { Enabled = s.CacheEnabled, TtlSeconds = s.CacheTtlSeconds },
            Bind = s.Bindings
                .Where(kv => kv.Value.Mode != BindingMode.None)
                .Select(kv => kv.Value.Mode == BindingMode.FromSource
                    ? new BindingDef { To = kv.Key, From = kv.Value.Expression }
                    : new BindingDef { To = kv.Key, Template = JsonValue.Create(kv.Value.FixedValue ?? "") })
                .ToList()
        }).ToList(),
        Response = Response.Count == 0 ? null : new JsonObject(Response
            .Where(f => !string.IsNullOrWhiteSpace(f.Name) && !string.IsNullOrWhiteSpace(f.Expression))
            .Select(f => KeyValuePair.Create(f.Name.Trim(), (JsonNode?)JsonValue.Create($"{{{{{f.Expression}}}}}"))))
    };

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

    /// <summary>Turns "Get customer" into a short unique alias such as "getCustomer".</summary>
    public string NewAlias(string displayName)
    {
        var words = displayName.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => new string(w.Where(char.IsLetterOrDigit).ToArray())).Where(w => w.Length > 0).ToArray();
        var alias = words.Length == 0 ? "step" : words[0].ToLowerInvariant() + string.Concat(words.Skip(1).Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
        if (!char.IsLetter(alias[0])) alias = "s" + alias;
        alias = alias.Length > 24 ? alias[..24] : alias;
        var candidate = alias;
        for (var n = 2; Steps.Any(s => s.Alias == candidate); n++) candidate = $"{alias}{n}";
        return candidate;
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string Unwrap(string? template) =>
        template is { Length: > 4 } t && t.StartsWith("{{") && t.EndsWith("}}") ? t[2..^2].Trim() : template ?? "";
}

public sealed class InputForm
{
    public string Source { get; set; } = "query";
    public string Name { get; set; } = "";
    public string Type { get; set; } = "string";
    public bool Required { get; set; }
    public string? Default { get; set; }
    public string? Pattern { get; set; }
    public string? TestValue { get; set; }
}

public sealed class StepForm
{
    public string Alias { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Endpoint { get; set; } = "";
    public bool Required { get; set; } = true;
    public string? When { get; set; }
    public string? ForEach { get; set; }

    /// <summary>Only meaningful with ForEach: call each unique item once.</summary>
    public bool Distinct { get; set; } = true;
    public bool CacheEnabled { get; set; }
    public int CacheTtlSeconds { get; set; } = 30;
    public bool ShowAdvanced { get; set; }

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
}

public sealed class ResponseField
{
    public string Name { get; set; } = "";
    public string Expression { get; set; } = "";
}
