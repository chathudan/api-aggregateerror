using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ApiAggregator.Engine.Authoring;
using ApiAggregator.Engine.Config;
using ApiAggregator.Engine.Execution;
using ApiAggregator.Engine.Expressions;
using ApiAggregator.Engine.Model;
using ApiAggregator.Engine.Planning;

namespace ApiAggregator.Engine.Validation;

public sealed record ValidationIssue(string Where, string Message);

/// <summary>
/// Issues block testing and publishing. Warnings never do: they are things a person should look at
/// first (for example a risky combine key), shown next to the issues.
/// </summary>
public sealed record ValidationResult(IReadOnlyList<ValidationIssue> Issues)
{
    public IReadOnlyList<ValidationIssue> Warnings { get; init; } = [];

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
        var warnings = new List<ValidationIssue>();
        void Add(string where, string message) => issues.Add(new ValidationIssue(where, message));
        void Warn(string where, string message) => warnings.Add(new ValidationIssue(where, message));

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
            if (SignInReferences.Problem(service, endpoint, config) is { } signIn) Add(at, signIn);

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

        // ---- for-each steps that send the looped field under another field's name
        foreach (var step in recipe.Steps) CheckLoopField(step, recipe, config, Warn);

        // ---- combined lists
        CheckCombines(recipe, aliases, Add, Warn);

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

        return new ValidationResult(issues) { Warnings = warnings };
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
            case "combine":
                // Combined lists are built after every step has finished, so a step can never read one.
                if (step is not null) add(where, "Steps can't use combined lists; they're built after all steps finish.");
                else if (path.FirstProperty is null || !recipe.Combine.ContainsKey(path.FirstProperty))
                    add(where, $"'{expr}' refers to a combined list that isn't defined. Use combine.<name> with a name from the Combine section.");
                break;
            default:
                if (!aliases.Contains(path.Root)) add(where, UnknownStep(path.Root, recipe));
                else if (step is not null && path.Root == step.Alias) add(where, "A step can't use its own result.");
                break;
        }
    }

    /// <summary>
    /// "'ordersFromCrm' isn't a step here. Did you mean 'getOrders' (Orders from CRM)?" The usual cause is a step whose
    /// display name was changed while its name in expressions stayed the same, so display names are matched too.
    /// </summary>
    public static string UnknownStep(string name, Recipe recipe)
    {
        if (RecipeRefactor.ClosestStep(recipe, name) is not { } step)
            return $"'{name}' isn't a step here. Pick the value again from the list.";
        var shown = string.IsNullOrWhiteSpace(step.DisplayName) || step.DisplayName == step.Alias ? "" : $" ({step.DisplayName})";
        return $"'{name}' isn't a step here. Did you mean '{step.Alias}'{shown}?";
    }

    /// <summary>
    /// Warns when a step loops over one field of each item but sends it under the name of ANOTHER field of the same item:
    /// looping over each order's id and sending it as contactId, while each order also has a contactId. Easy to pick by
    /// mistake, and the calls then quietly return nothing. Stays quiet for id → orderId (the item has no orderId).
    /// </summary>
    private static void CheckLoopField(StepDef step, Recipe recipe, ConfigSnapshot config, Action<string, string> warn)
    {
        if (SourceCatalog.LoopPath(step) is not { } loop || loop.TransformCalls.Count > 0) return;
        if (loop.Segments is not [.., { Kind: ValuePath.SegmentKind.Wildcard }, { Kind: ValuePath.SegmentKind.Property, Name: { } looped }]) return;
        if (!loop.Text.EndsWith("." + looped, StringComparison.Ordinal)) return;

        var itemPath = loop.Text[..^(looped.Length + 1)];
        ValuePath item;
        try { item = ValuePath.Parse(itemPath); }
        catch (FormatException) { return; }
        if (SourceCatalog.SchemaAt(recipe, item, config)?["properties"] is not JsonObject itemFields) return;

        var options = new SourceCatalog().LoopOptions(recipe, step.Alias, config);
        string Words(string expression, string fallback) =>
            options.FirstOrDefault(o => o.Expression == expression)?.Label.Split(" → ", 2) is [_, var words] ? words : fallback;

        foreach (var binding in step.Bind.Where(b => b.From?.Trim() == "item"))
        {
            var parts = binding.To.Split('.', 2);
            if (parts.Length != 2) continue;
            var name = AutoBinder.Normalize(parts[1]);
            if (name == AutoBinder.Normalize(looped)) continue;
            var better = itemFields.Select(f => f.Key).FirstOrDefault(f => AutoBinder.Normalize(f) == name);
            if (better is null) continue;

            warn($"steps.{step.Alias}",
                $"{step.DisplayName ?? step.Alias} sends {Words(loop.Text, $"each {looped}")} as {parts[1]}. " +
                $"Did you mean {Words($"{itemPath}.{better}", better)}? Pick it under Run this step.");
        }
    }

    // ------------------------------------------------------------------ combined lists

    private static readonly HashSet<string> AllowedNormalize = new(StringComparer.OrdinalIgnoreCase) { "trim", "lower" };

    /// <summary>
    /// Key fields that identify a PERSON loosely (an email, a name, a phone number...). Two different people
    /// can share one, so combining on them can merge the wrong records. Compared after lowercasing and
    /// keeping only letters and digits.
    /// </summary>
    private static readonly HashSet<string> PersonLikeKeys = new(StringComparer.Ordinal)
    {
        "email", "emailaddress", "name", "fullname", "firstname", "lastname", "surname", "givenname", "familyname",
        "dob", "dateofbirth", "birthdate", "phone", "mobile", "phonenumber", "address"
    };

    private static void CheckCombines(Recipe recipe, HashSet<string> aliases, Action<string, string> add, Action<string, string> warn)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, def) in recipe.Combine)
        {
            var at = $"combine.{name}";
            if (!AliasRegex().IsMatch(name)) add(at, "Combined list names must start with a letter and use only letters, digits or underscore (32 characters at most).");
            if (!names.Add(name)) add(at, "Two combined lists have the same name.");
            if (def.Sources.Count == 0) add(at, "Add at least one source, the list of records to combine.");

            // ---- sources
            var sourceNames = new List<string>();
            var warned = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < def.Sources.Count; i++)
            {
                var source = def.Sources[i];
                var label = $"Source {i + 1}";
                string? root = null;

                if (string.IsNullOrWhiteSpace(source.From)) add(at, $"{label} needs a 'from' path, such as crm.body.contacts.");
                else if (source.From.Contains("{{")) add(at, $"{label}: write the path without braces, e.g. crm.body.contacts.");
                else
                {
                    try
                    {
                        var path = ValuePath.Parse(source.From);
                        root = path.Root;
                        foreach (var t in path.TransformCalls.Where(t => !Transforms.Names.Contains(t.Name)))
                            add(at, $"{label}: unknown transform '{t.Name}'. Allowed: {string.Join(", ", Transforms.Names)}.");

                        if (ExecutionPlanner.ReservedRoots.Contains(root))
                            add(at, $"{label}: '{source.From}' must start with the name of a step. Combined lists can only use step results.");
                        else if (!aliases.Contains(root))
                            add(at, $"{label}: {UnknownStep(root, recipe)}");
                    }
                    catch (FormatException ex) { add(at, $"{label}: {ex.Message}"); }
                }

                if (!string.IsNullOrWhiteSpace(source.Name) && !AliasRegex().IsMatch(source.Name.Trim()))
                    add(at, $"{label}: the name must start with a letter and use only letters, digits or underscore.");
                var sourceName = !string.IsNullOrWhiteSpace(source.Name) ? source.Name.Trim() : root;
                if (sourceName is not null)
                {
                    if (sourceNames.Contains(sourceName)) add(at, $"Two sources are called '{sourceName}'. Give each source its own name.");
                    sourceNames.Add(sourceName);
                }

                // ---- key
                var keyParts = Parts(source.Key);
                if (string.IsNullOrWhiteSpace(source.Key)) add(at, $"{label} needs a key: the field that identifies a record, such as userId.");
                else CheckFieldList(source.Key, $"{label}: key", at, add);

                // ---- mapping ("via"): for services that don't share an id
                var comparedOn = source.Key; // the fields whose values are compared across lists
                if (source.Via is { } via)
                {
                    CheckVia(via, label, keyParts.Length, recipe, aliases, at, add);
                    comparedOn = via.Use;
                }

                // Loose person-like identifiers are risky to match on; the compared field is the mapping's, when there is one.
                foreach (var part in Parts(comparedOn).Where(p => p.Length > 0 && p.Split('.').All(IsFieldName)))
                {
                    var last = new string(part.Split('.')[^1].Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
                    if (PersonLikeKeys.Contains(last) && warned.Add(part))
                        warn(at, $"Matching on '{part}' can merge different people's or customers' records. Use an id that both services share, or link them with a mapping step.");
                }
            }

            // ---- rules
            foreach (var (field, owner) in def.Owners)
                if (!sourceNames.Contains(owner))
                    add(at, $"The owner of '{field}' is '{owner}', which isn't one of the sources ({string.Join(", ", sourceNames)}).");

            foreach (var n in def.Normalize.Where(n => !AllowedNormalize.Contains(n)))
                add(at, $"Normalize can only be 'trim' or 'lower', not '{n}'.");

            if (def.OnConflict.Trim() != "priority")
            {
                var newest = RecordCombiner.NewestField(def.OnConflict);
                if (newest is null || !newest.Split('.').All(IsFieldName))
                    add(at, "On conflict must be 'priority' or 'newest:<field>', for example newest:updatedAt.");
            }

            if (!def.Nulls.Equals("neverOverwrite", StringComparison.OrdinalIgnoreCase) && !def.Nulls.Equals("overwrite", StringComparison.OrdinalIgnoreCase))
                add(at, "Nulls must be 'neverOverwrite' or 'overwrite'.");
            if (!def.WithoutKey.Equals("keep", StringComparison.OrdinalIgnoreCase) && !def.WithoutKey.Equals("drop", StringComparison.OrdinalIgnoreCase))
                add(at, "Without a key must be 'keep' or 'drop'.");
            if (def.MaxRecords is < 1 or > 50_000) add(at, "Max records must be between 1 and 50,000.");
            if (!string.IsNullOrWhiteSpace(def.SortBy) && !def.SortBy.Trim().TrimStart('-').Trim().Split('.').All(IsFieldName))
                add(at, "Sort by must be a field name, optionally starting with '-' for descending, e.g. -updatedAt.");

            // Without a response template every combined list is returned under its own name.
            if (recipe.Response is null && aliases.Contains(name))
                add(at, $"'{name}' is also the name of a step, so the default answer would mix them up. Rename the combined list.");
            else if (recipe.Response is not null && !UsesCombine(recipe.Response, name))
                warn(at, $"The combined list '{name}' isn't used in the response.");
        }
    }

    /// <summary>True when the response template contains {{combine.&lt;name&gt;...}}.</summary>
    private static bool UsesCombine(JsonNode response, string name) =>
        Template.FindExpressions(response).Any(e =>
        {
            try { var path = ValuePath.Parse(e); return path.Root == "combine" && path.FirstProperty == name; }
            catch (FormatException) { return false; } // reported elsewhere
        });

    private static string[] Parts(string? commaSeparated) => (commaSeparated ?? "").Split(',', StringSplitOptions.TrimEntries);

    /// <summary>Each comma-separated part must be a field name, or a dotted path like customer.id.</summary>
    private static void CheckFieldList(string list, string what, string at, Action<string, string> add)
    {
        foreach (var part in Parts(list))
            if (part.Length == 0 || !part.Split('.').All(IsFieldName))
                add(at, $"{what} '{part}' isn't a valid field. Use a field name such as userId, or customer.id. Separate several fields with commas.");
    }

    private static void CheckVia(CombineVia via, string label, int keyParts, Recipe recipe, HashSet<string> aliases, string at, Action<string, string> add)
    {
        label += " (mapping)";
        if (string.IsNullOrWhiteSpace(via.From) || via.From.Contains("{{")) add(at, $"{label} needs a 'from' path without braces, such as links.body.items.");
        else
        {
            try
            {
                var root = ValuePath.Parse(via.From).Root;
                if (ExecutionPlanner.ReservedRoots.Contains(root)) add(at, $"{label}: '{via.From}' must start with the name of a step.");
                else if (!aliases.Contains(root)) add(at, $"{label}: {UnknownStep(root, recipe)}");
            }
            catch (FormatException ex) { add(at, $"{label}: {ex.Message}"); }
        }

        foreach (var (what, list) in new[] { ("the field that matches", via.Match), ("the field to use", via.Use) })
        {
            if (string.IsNullOrWhiteSpace(list)) { add(at, $"{label} needs {what}."); continue; }
            CheckFieldList(list, $"{label}: {what}", at, add);
            if (Parts(list).Length != keyParts)
                add(at, $"{label}: {what} must have the same number of fields as the key ({keyParts}).");
        }
    }

    /// <summary>The shape of a step's name in expressions (and of a combined list's name).</summary>
    public static bool IsValidStepName(string name) => AliasRegex().IsMatch(name);

    private static bool IsFieldName(string s) =>
        s.Length is > 0 and <= 128 && s.All(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-' or '$');

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
