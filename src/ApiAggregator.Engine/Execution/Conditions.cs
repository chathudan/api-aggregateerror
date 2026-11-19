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

    /// <summary>
    /// Splits a condition the admin UI's picker can show: "{{x}}" (Op null), or "{{x}} == text" / "{{x}} != text" where the
    /// right side has no "{{ }}". Anything else (text around the value, two values compared) returns false.
    /// </summary>
    public static bool TryParseSimple(string? condition, out string expression, out string? op, out string? value)
    {
        expression = ""; op = null; value = null;
        if (string.IsNullOrWhiteSpace(condition)) return false;

        var (found, index) = FindOperator(condition);
        var left = (found is null ? condition : condition[..index]).Trim();
        if (!Template.IsSingleExpression(left) || left.Length <= 4) return false;
        expression = left[2..^2].Trim();
        if (expression.Length == 0) return false;
        if (found is null) return true;

        var right = condition[(index + 2)..].Trim();
        if (right.Contains("{{", StringComparison.Ordinal) || right.Contains("}}", StringComparison.Ordinal)) return false;
        op = found;
        value = right.Length >= 2 && (right[0] is '\'' or '"') && right[^1] == right[0] ? right[1..^1] : right;
        return true;
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
