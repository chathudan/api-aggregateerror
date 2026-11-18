using System.Text.Json;
using System.Text.Json.Nodes;
using ApiAggregator.Engine.Expressions;

namespace ApiAggregator.Engine.Execution;

/// <summary>
/// Step conditions ("when"). Intentionally minimal:
///   "{{a.body.isPremium}}"                 truthy check
///   "{{a.body.status}} == active"          equality (case-insensitive text compare)
///   "{{a.body.status}} != closed"          inequality
/// </summary>
public static class Conditions
{
    public static bool IsTrue(string condition, IValueScope scope)
    {
        var (op, index) = FindOperator(condition);
        if (op is null) return IsTruthy(Template.Resolve(condition, scope));

        var left = JsonText.Of(Template.Resolve(condition[..index].Trim(), scope))?.Trim() ?? "";
        var right = JsonText.Of(Template.Resolve(condition[(index + 2)..].Trim(), scope))?.Trim().Trim('\'', '"') ?? "";
        var equal = string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        return op == "==" ? equal : !equal;
    }

    private static (string? Op, int Index) FindOperator(string s)
    {
        var depth = 0;
        for (var i = 0; i < s.Length - 1; i++)
        {
            if (s[i] == '{' && s[i + 1] == '{') { depth++; i++; continue; }
            if (s[i] == '}' && s[i + 1] == '}') { depth--; i++; continue; }
            if (depth == 0 && (s[i] == '=' || s[i] == '!') && s[i + 1] == '=') return (s.Substring(i, 2), i);
        }
        return (null, -1);
    }

    private static bool IsTruthy(JsonNode? value) => value switch
    {
        null => false,
        JsonArray a => a.Count > 0,
        JsonObject => true,
        JsonValue v when v.GetValueKind() == JsonValueKind.False => false,
        JsonValue v when v.GetValueKind() == JsonValueKind.True => true,
        JsonValue v when v.GetValueKind() == JsonValueKind.Number => v.ToJsonString() != "0",
        _ => JsonText.Of(value) is { Length: > 0 } s && !s.Equals("false", StringComparison.OrdinalIgnoreCase) && s != "0"
    };
}
