using System.Text.Json;
using System.Text.Json.Nodes;
using ApiAggregator.Engine.Config;
using ApiAggregator.Engine.Expressions;
using ApiAggregator.Engine.Model;
using ApiAggregator.Engine.Planning;
using ApiAggregator.Engine.Validation;

namespace ApiAggregator.Engine.Authoring;

/// <summary>The outcome of <see cref="RecipeRefactor.RenameStep"/>: the new recipe, or a plain-English reason it wasn't renamed.</summary>
public sealed record RenameResult(Recipe? Recipe, int Changed, string? Problem)
{
    public bool Renamed => Recipe is not null;
}

/// <summary>
/// Safe edits to a whole recipe, used by the admin UI so technicians never edit step names inside expressions by hand.
///
/// Renaming works on the parsed path language, never on text search-and-replace: an expression is rewritten only when
/// its ROOT (the first name, see <see cref="ValuePath.Root"/>) is the old step name. A field or a transform argument that
/// happens to be spelled like the step ("orders.body.orders", "distinct('orders')") is left alone.
/// </summary>
public static class RecipeRefactor
{
    /// <summary>Longest name made from a display name; leaves room for a number to keep it unique (the limit is 32).</summary>
    private const int MaxGeneratedLength = 24;

    /// <summary>Turns "Orders from CRM" into "ordersFromCrm": camelCase, starts with a letter, unique, never a reserved word.</summary>
    public static string AliasFor(string? displayName, IEnumerable<string> taken)
    {
        var words = (displayName ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => new string(w.Where(char.IsAsciiLetterOrDigit).ToArray())).Where(w => w.Length > 0).ToArray();
        var alias = words.Length == 0
            ? "step"
            : words[0].ToLowerInvariant() + string.Concat(words.Skip(1).Select(w => char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant()));
        if (!char.IsAsciiLetter(alias[0])) alias = "s" + alias;
        if (alias.Length > MaxGeneratedLength) alias = alias[..MaxGeneratedLength];

        var used = taken.ToHashSet(StringComparer.Ordinal);
        var candidate = alias;
        for (var n = 2; used.Contains(candidate) || ExecutionPlanner.ReservedRoots.Contains(candidate); n++) candidate = $"{alias}{n}";
        return candidate;
    }

    /// <summary>"orders.body.items[*].id" with root "orders" becomes "crmOrders.body.items[*].id". Anything else is returned as it is.</summary>
    public static string RewriteRoot(string path, string oldAlias, string newAlias)
    {
        ValuePath parsed;
        try { parsed = ValuePath.Parse(path); }
        catch (Exception ex) when (ex is FormatException or ArgumentException) { return path; } // the validator reports it

        if (!string.Equals(parsed.Root, oldAlias, StringComparison.Ordinal)) return path;

        // The root is the first name in the text. Rewrite exactly that token; an unusual shape stays as it is
        // (and the validator then points at it) rather than being guessed at.
        var lead = path.Length - path.TrimStart().Length;
        var rest = path[lead..];
        if (!rest.StartsWith(oldAlias, StringComparison.Ordinal)) return path;
        if (rest.Length > oldAlias.Length && rest[oldAlias.Length] is not ('.' or '[' or '|') && !char.IsWhiteSpace(rest[oldAlias.Length])) return path;
        return path[..lead] + newAlias + rest[oldAlias.Length..];
    }

    /// <summary>Rewrites the root of every "{{ }}" expression in a text; the text around them is kept.</summary>
    public static string RewriteTemplate(string text, string oldAlias, string newAlias) =>
        Template.MapExpressions(text, e => RewriteRoot(e, oldAlias, newAlias));

    /// <summary>Rewrites every string inside a JSON template (a request body, the response).</summary>
    public static JsonNode? RewriteTemplate(JsonNode? node, string oldAlias, string newAlias) => node switch
    {
        null => null,
        JsonObject obj => new JsonObject(obj.Select(p => KeyValuePair.Create(p.Key, RewriteTemplate(p.Value, oldAlias, newAlias)))),
        JsonArray arr => new JsonArray(arr.Select(n => RewriteTemplate(n, oldAlias, newAlias)).ToArray()),
        JsonValue v when v.GetValueKind() == JsonValueKind.String => JsonValue.Create(RewriteTemplate(v.GetValue<string>(), oldAlias, newAlias)),
        _ => node.DeepClone()
    };

    /// <summary>How many places in the recipe read the step's result (each binding, condition, list, response field... counts once).</summary>
    public static int References(Recipe recipe, string alias)
    {
        // Renaming to a name that can't occur counts exactly the places a real rename would change.
        var json = ToJson(recipe);
        return Rewrite(json, alias, "\u0001");
    }

    /// <summary>
    /// Renames a step and updates everything that uses it: values sent to services, request bodies, conditions,
    /// for-each lists, the response, and combined lists (sources, mappings and the owners that name the step).
    /// </summary>
    public static RenameResult RenameStep(Recipe recipe, string oldAlias, string newAlias)
    {
        newAlias = newAlias.Trim();
        if (recipe.Steps.All(s => s.Alias != oldAlias)) return Refuse($"There is no step called '{oldAlias}'.");
        if (newAlias == oldAlias) return new RenameResult(recipe, 0, null);
        if (ExecutionPlanner.ReservedRoots.Contains(newAlias))
            return Refuse($"'{newAlias}' is a reserved word ({string.Join(", ", ExecutionPlanner.ReservedRoots)}). Pick a different name.");
        if (!RecipeValidator.IsValidStepName(newAlias))
            return Refuse($"'{newAlias}' can't be used as a step's name in expressions. Start with a letter and use only letters, digits or underscore (32 characters at most).");
        if (recipe.Steps.Any(s => s.Alias == newAlias)) return Refuse($"'{newAlias}' is already the name of another step. Pick a different name.");

        var json = ToJson(recipe);
        var changed = Rewrite(json, oldAlias, newAlias);
        foreach (var step in json["steps"]!.AsArray().OfType<JsonObject>())
            if ((string?)step["alias"] == oldAlias) step["alias"] = newAlias;

        var renamed = json.Deserialize<Recipe>(AggregatorJson.Options)
            ?? throw new InvalidOperationException("The renamed recipe couldn't be read back.");
        return new RenameResult(renamed, changed, null);

        static RenameResult Refuse(string why) => new(null, 0, why);
    }

    /// <summary>
    /// The step a mistyped name most likely meant: one whose display name gives that name ("ordersFromCrm" for a step
    /// shown as "Orders from CRM"), else the closest step name by spelling. Null when nothing is close.
    /// </summary>
    public static StepDef? ClosestStep(Recipe recipe, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || recipe.Steps.Count == 0) return null;

        var byDisplayName = recipe.Steps.FirstOrDefault(s =>
            s.DisplayName is { Length: > 0 } d &&
            (string.Equals(AliasFor(d, []), name, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(Squash(d), Squash(name), StringComparison.Ordinal)));
        if (byDisplayName is not null) return byDisplayName;

        var limit = Math.Max(2, name.Length / 3);
        return recipe.Steps
            .Select(s => (Step: s, Distance: Distance(s.Alias.ToLowerInvariant(), name.ToLowerInvariant())))
            .Where(x => x.Distance <= limit)
            .OrderBy(x => x.Distance)
            .Select(x => x.Step)
            .FirstOrDefault();
    }

    // ------------------------------------------------------------------ the walk over every expression

    private static JsonObject ToJson(Recipe recipe) =>
        JsonSerializer.SerializeToNode(recipe, AggregatorJson.Options)!.AsObject();

    /// <summary>Rewrites in place and returns how many values changed. Property names are the camelCase JSON names.</summary>
    private static int Rewrite(JsonObject recipe, string oldAlias, string newAlias)
    {
        var changed = 0;

        // A plain path (no braces): bindings' "from", combine "from".
        void Path(JsonObject owner, string property)
        {
            if (owner[property] is not JsonValue v || v.GetValueKind() != JsonValueKind.String) return;
            var before = v.GetValue<string>();
            var after = RewriteRoot(before, oldAlias, newAlias);
            if (after != before) { owner[property] = after; changed++; }
        }

        // A template: "{{ }}" inside a string, or a JSON value made of such strings.
        void TemplateAt(JsonObject owner, string property)
        {
            var before = owner[property];
            if (before is null) return;
            var after = RewriteTemplate(before, oldAlias, newAlias);
            if (!JsonNode.DeepEquals(before, after)) { owner[property] = after; changed++; }
        }

        foreach (var step in (recipe["steps"] as JsonArray ?? []).OfType<JsonObject>())
        {
            foreach (var binding in (step["bind"] as JsonArray ?? []).OfType<JsonObject>())
            {
                Path(binding, "from");
                TemplateAt(binding, "template");
            }
            TemplateAt(step, "when");
            TemplateAt(step, "forEach");
        }

        // Each response field counts once.
        if (recipe["response"] is JsonObject response)
            foreach (var key in response.Select(p => p.Key).ToList()) TemplateAt(response, key);
        else TemplateAt(recipe, "response");

        foreach (var (_, combineNode) in (recipe["combine"] as JsonObject ?? []).ToList())
        {
            if (combineNode is not JsonObject combine) continue;
            foreach (var source in (combine["sources"] as JsonArray ?? []).OfType<JsonObject>())
            {
                // A source without its own name is named after its step: owners that point at it follow the rename.
                var named = source["name"] is JsonValue n && !string.IsNullOrWhiteSpace(n.GetValue<string>());
                var before = (string?)source["from"];
                Path(source, "from");
                if (!named && before != (string?)source["from"] && combine["owners"] is JsonObject owners)
                    foreach (var field in owners.Where(o => (string?)o.Value == oldAlias).Select(o => o.Key).ToList())
                    {
                        owners[field] = newAlias;
                        changed++;
                    }

                if (source["via"] is JsonObject via) Path(via, "from");
            }
        }
        return changed;
    }

    /// <summary>"Orders from CRM" and "orders_from_crm" both become "ordersfromcrm".</summary>
    private static string Squash(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>Levenshtein distance; names are at most 32 characters, so the simple version is plenty.</summary>
    private static int Distance(string a, string b)
    {
        var previous = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var current = new int[b.Length + 1];
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            previous = current;
        }
        return previous[b.Length];
    }
}
