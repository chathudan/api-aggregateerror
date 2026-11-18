using System.Text.RegularExpressions;
using ApiAggregator.Engine.Config;
using ApiAggregator.Engine.Expressions;
using ApiAggregator.Engine.Model;
using ApiAggregator.Engine.Planning;

namespace ApiAggregator.Engine.Validation;

public sealed record ValidationIssue(string Where, string Message);

public sealed record ValidationResult(IReadOnlyList<ValidationIssue> Issues)
{
    public bool IsValid => Issues.Count == 0;
}

/// <summary>
/// Checks a recipe before it can be tested or published. Messages are written in plain
/// English because QA and technicians read them in the admin UI.
/// </summary>
public static partial class RecipeValidator
{
    private static readonly HashSet<string> AllowedMethods = ["GET", "POST", "PUT", "PATCH", "DELETE"];
    private static readonly HashSet<string> InputSources = new(StringComparer.OrdinalIgnoreCase) { "route", "query", "header" };
    private static readonly HashSet<string> InputTypes = ["string", "int", "number", "bool", "date"];

    /// <summary>Headers a recipe may never set: auth is handled by auth profiles, transport by the HTTP stack.</summary>
    public static readonly IReadOnlySet<string> BlockedHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization", "Proxy-Authorization", "Cookie", "Set-Cookie", "Host", "Content-Length",
        "Transfer-Encoding", "Connection", "Upgrade", "TE", "Trailer", "Keep-Alive", "Expect"
    };

    public static ValidationResult Validate(Recipe recipe, ConfigSnapshot config)
    {
        var issues = new List<ValidationIssue>();
        void Add(string where, string message) => issues.Add(new ValidationIssue(where, message));

        // ---- endpoint
        if (!SlugRegex().IsMatch(recipe.Id)) Add("id", "Use lowercase letters, digits and dashes only, e.g. customer-overview.");
        if (!AllowedMethods.Contains(recipe.Method.ToUpperInvariant())) Add("method", $"Method must be one of {string.Join(", ", AllowedMethods)}.");
        if (string.IsNullOrWhiteSpace(recipe.Route) || recipe.Route.Contains("..") || recipe.Route.Contains('?'))
            Add("route", "Enter a route such as customer/{id}/overview (no '..' or '?').");
        if (recipe.Steps.Count == 0) Add("steps", "Add at least one step.");
        if (recipe.Steps.Count > 25) Add("steps", "Keep it to 25 steps or fewer. Split very large endpoints in two.");
        if (recipe.TimeoutMs is < 100 or > 60_000) Add("timeoutMs", "Timeout must be between 100 ms and 60 s.");
        CheckCache(recipe.Cache, "cache", Add);

        // ---- inputs
        foreach (var (key, input) in recipe.Inputs)
        {
            var parts = key.Split('.', 2);
            if (parts.Length != 2 || !InputSources.Contains(parts[0])) Add($"inputs.{key}", "Input names must start with route., query. or header.");
            if (!InputTypes.Contains(input.Type)) Add($"inputs.{key}", $"Type must be one of {string.Join(", ", InputTypes)}.");
            if (input.Pattern is not null && !IsSafeRegex(input.Pattern)) Add($"inputs.{key}", "The pattern is not a valid regular expression.");
        }
        foreach (var routeParam in RouteParamRegex().Matches(recipe.Route).Select(m => m.Groups[1].Value))
            if (!recipe.Inputs.ContainsKey($"route.{routeParam}"))
                Add("inputs", $"The route has {{{routeParam}}} but no input 'route.{routeParam}' is declared.");

        // ---- steps
        var aliases = new HashSet<string>(StringComparer.Ordinal);
        foreach (var step in recipe.Steps)
        {
            var at = $"steps.{step.Alias}";
            if (!AliasRegex().IsMatch(step.Alias)) Add(at, "Step names must start with a letter and use letters, digits or underscore.");
            if (ExecutionPlanner.ReservedRoots.Contains(step.Alias)) Add(at, $"'{step.Alias}' is a reserved name. Pick another step name.");
            if (!aliases.Add(step.Alias)) Add(at, "Two steps have the same name.");
            if (step.MaxConcurrency is < 1 or > 32) Add(at, "Max concurrency must be between 1 and 32.");
            CheckCache(step.Cache, $"{at}.cache", Add);

            var endpoint = config.FindEndpoint(step.Endpoint, out var service);
            if (endpoint is null || service is null) { Add(at, $"Endpoint '{step.Endpoint}' was not found. Pick one from the service list."); continue; }

            var boundTargets = step.Bind.Select(b => b.To).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var pathParam in RouteParamRegex().Matches(endpoint.Path).Select(m => m.Groups[1].Value))
                if (!boundTargets.Contains($"path.{pathParam}"))
                    Add(at, $"'{pathParam}' (path) is required by {step.Endpoint} but has no value.");
            foreach (var p in endpoint.Parameters.Where(p => p.Required && p.In != "path"))
                if (!boundTargets.Contains($"{p.In}.{p.Name}"))
                    Add(at, $"'{p.Name}' ({p.In}) is required by {step.Endpoint} but has no value.");

            foreach (var b in step.Bind) CheckBinding(b, at, Add);
            if (step.ForEach is not null && !step.ForEach.Trim().StartsWith("{{")) Add(at, "For-each must be an expression such as {{b.body.items[*].id}}.");
        }

        // ---- expressions (steps + response)
        var expressions = recipe.Steps.SelectMany(s => ExecutionPlanner.Expressions(s).Select(e => (Where: $"steps.{s.Alias}", Expr: e, Step: (StepDef?)s)))
            .Concat(Template.FindExpressions(recipe.Response).Select(e => (Where: "response", Expr: e, Step: (StepDef?)null)));
        foreach (var (where, expr, step) in expressions)
            CheckExpression(expr, where, step, recipe, aliases, Add);

        // ---- run order
        if (issues.Count == 0)
        {
            try { ExecutionPlanner.Plan(recipe); }
            catch (InvalidOperationException ex) { Add("steps", ex.Message); }
        }

        return new ValidationResult(issues);
    }

    private static void CheckBinding(BindingDef b, string at, Action<string, string> add)
    {
        if (string.IsNullOrWhiteSpace(b.From) == (b.Template is null))
            add(at, $"Binding to '{b.To}' needs exactly one source (a value or a template).");

        var parts = b.To.Split('.', 2);
        var target = parts[0].ToLowerInvariant();
        switch (target)
        {
            case "path" or "query" or "cookie" when parts.Length == 2 && parts[1].Length > 0:
                break;
            case "header" when parts.Length == 2 && HeaderNameRegex().IsMatch(parts[1]):
                if (BlockedHeaders.Contains(parts[1])) add(at, $"Header '{parts[1]}' can't be set here. Use the service's auth profile instead.");
                break;
            case "body":
                break;
            default:
                add(at, $"'{b.To}' is not a valid target. Use path.x, query.x, header.X-Name, cookie.x or body.");
                break;
        }
    }

    private static void CheckExpression(string expr, string where, StepDef? step, Recipe recipe,
        HashSet<string> aliases, Action<string, string> add)
    {
        ValuePath path;
        try { path = ValuePath.Parse(expr); }
        catch (FormatException ex) { add(where, ex.Message); return; }

        foreach (var t in path.TransformCalls.Where(t => !Transforms.Names.Contains(t.Name)))
            add(where, $"Unknown transform '{t.Name}'. Allowed: {string.Join(", ", Transforms.Names)}.");

        switch (path.Root)
        {
            case "request":
                var key = $"{path.FirstProperty}.{path.SecondProperty}";
                if (path.FirstProperty == "claims") break;
                if (!recipe.Inputs.ContainsKey(key))
                    add(where, $"'{expr}' uses request input '{key}', which isn't declared under Inputs.");
                break;
            case "item" or "index":
                if (step?.ForEach is null) add(where, $"'{path.Root}' can only be used in a step that runs for each item.");
                break;
            case "ctx":
                break;
            default:
                if (!aliases.Contains(path.Root)) add(where, $"'{expr}' refers to a step named '{path.Root}' that doesn't exist.");
                else if (step is not null && path.Root == step.Alias) add(where, "A step can't use its own result.");
                break;
        }
    }

    private static void CheckCache(CacheSettings cache, string at, Action<string, string> add)
    {
        if (cache.Enabled && cache.TtlSeconds is < 1 or > 86_400) add(at, "Cache time must be between 1 second and 24 hours.");
    }

    private static bool IsSafeRegex(string pattern)
    {
        try { _ = new Regex(pattern, RegexOptions.None, TimeSpan.FromMilliseconds(50)); return true; }
        catch (ArgumentException) { return false; }
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$")] private static partial Regex SlugRegex();
    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]{0,31}$")] private static partial Regex AliasRegex();
    [GeneratedRegex(@"\{([A-Za-z0-9_]+)(?::[^}]*)?\}")] private static partial Regex RouteParamRegex();
    [GeneratedRegex("^[A-Za-z0-9-]{1,64}$")] private static partial Regex HeaderNameRegex();
}
