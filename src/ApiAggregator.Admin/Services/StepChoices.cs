using ApiAggregator.Engine.Authoring;
using ApiAggregator.Engine.Execution;
using ApiAggregator.Engine.Expressions;

namespace ApiAggregator.Admin.Services;

/// <summary>"Run this step: once / once for each item in a list / custom expression".</summary>
public enum LoopMode { Once, List, Custom }

/// <summary>"Only run when": always (no condition), a picked field, or a custom condition.</summary>
public enum ConditionMode { None, Picked, Custom }

public enum ConditionOp { Is, IsNot, HasValue }

/// <summary>
/// Turns a step's saved "for each" and "only run when" into the pickers' choices and back. Pure functions, so loading
/// existing recipes is unit-tested without a browser. What is saved is still the same text the engine always read.
/// </summary>
public static class StepChoices
{
    // ------------------------------------------------------------------ for each

    /// <summary>The picker choice for a saved "for each": one of the listed options, or Custom with the text kept as it is.</summary>
    public static LoopMode LoadLoop(string? forEach, IEnumerable<LoopOption> options)
    {
        if (string.IsNullOrWhiteSpace(forEach)) return LoopMode.Once;
        return LoopExpression(forEach) is { } expression && options.Any(o => o.Expression == expression) ? LoopMode.List : LoopMode.Custom;
    }

    /// <summary>"{{ orders.body.orders[*] }}" → "orders.body.orders[*]"; null when it isn't exactly one value.</summary>
    public static string? LoopExpression(string? forEach)
    {
        var text = forEach?.Trim();
        return !string.IsNullOrEmpty(text) && text.Length > 4 && Template.IsSingleExpression(text) ? text[2..^2].Trim() : null;
    }

    public static string ToForEach(string expression) => $"{{{{{expression}}}}}";

    // ------------------------------------------------------------------ only run when

    /// <summary>Fills the step's condition picker from its saved "when". A field that isn't in the list, or any other shape, is Custom.</summary>
    public static void LoadCondition(StepForm step, IEnumerable<SourceOption> fields)
    {
        step.ConditionField = null; step.ConditionValue = null; step.ConditionOp = ConditionOp.Is;
        if (string.IsNullOrWhiteSpace(step.When)) { step.ConditionMode = ConditionMode.None; return; }

        if (!Conditions.TryParseSimple(step.When, out var expression, out var op, out var value) || fields.All(f => f.Expression != expression))
        {
            step.ConditionMode = ConditionMode.Custom;
            return;
        }

        step.ConditionMode = ConditionMode.Picked;
        step.ConditionField = expression;
        step.ConditionOp = op switch { "==" => ConditionOp.Is, "!=" => ConditionOp.IsNot, _ => ConditionOp.HasValue };
        step.ConditionValue = value;
    }

    /// <summary>
    /// The saved "when" for a picked condition: "{{f}}" (has a value), "{{f}} == v" (is) or "{{f}} != v" (is not).
    /// Null while no field is picked, so a half-filled picker never blocks the step.
    /// </summary>
    public static string? ToWhen(StepForm step)
    {
        if (string.IsNullOrWhiteSpace(step.ConditionField)) return null;
        var left = $"{{{{{step.ConditionField}}}}}";
        return step.ConditionOp switch
        {
            ConditionOp.HasValue => left,
            ConditionOp.Is => $"{left} == {step.ConditionValue?.Trim()}".TrimEnd(),
            _ => $"{left} != {step.ConditionValue?.Trim()}".TrimEnd()
        };
    }

    /// <summary>A plain-English problem with the picked value, or null.</summary>
    public static string? ConditionProblem(StepForm step) =>
        step.ConditionMode == ConditionMode.Picked && step.ConditionOp != ConditionOp.HasValue
        && ((step.ConditionValue ?? "").Contains("{{") || (step.ConditionValue ?? "").Contains("}}"))
            ? "Type a plain value here. To compare with another value, choose Custom condition…"
            : null;

    /// <summary>Sets both pickers of every step from what was loaded (once the service list is known).</summary>
    public static void LoadAll(RecipeForm form, Func<StepForm, IReadOnlyList<LoopOption>> loops, Func<StepForm, IReadOnlyList<SourceOption>> fields)
    {
        foreach (var step in form.Steps)
        {
            step.LoopMode = LoadLoop(step.ForEach, loops(step));
            LoadCondition(step, fields(step));
            // A loaded condition, or a cache shared by every caller, is visible straight away rather than hidden under Advanced.
            if (step.ConditionMode != ConditionMode.None || (step.CacheEnabled && !step.CachePerCaller)) step.ShowAdvanced = true;
        }
    }
}
