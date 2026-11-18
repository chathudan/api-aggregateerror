using ApiAggregator.Engine.Expressions;
using ApiAggregator.Engine.Model;

namespace ApiAggregator.Engine.Planning;

/// <summary>
/// Steps grouped into levels. Every step in a level can run in parallel;
/// a level starts only when the previous level has finished.
/// </summary>
public sealed record ExecutionPlan(
    IReadOnlyList<IReadOnlyList<StepDef>> Levels,
    IReadOnlyDictionary<string, IReadOnlySet<string>> DependsOn);

/// <summary>
/// Works out the run order from the bindings themselves: if step "b" reads "a.body.id",
/// then "b" depends on "a". Nobody has to wire dependencies by hand.
/// </summary>
public static class ExecutionPlanner
{
    /// <summary>Roots that are not step aliases.</summary>
    public static readonly IReadOnlySet<string> ReservedRoots =
        new HashSet<string>(StringComparer.Ordinal) { "request", "item", "index", "ctx" };

    public static ExecutionPlan Plan(Recipe recipe)
    {
        var aliases = recipe.Steps.Select(s => s.Alias).ToHashSet(StringComparer.Ordinal);
        var deps = recipe.Steps.ToDictionary(
            s => s.Alias,
            s => (IReadOnlySet<string>)ReferencedRoots(s).Where(r => aliases.Contains(r) && r != s.Alias).ToHashSet(StringComparer.Ordinal),
            StringComparer.Ordinal);

        // Kahn's algorithm, level by level. Original step order is kept inside a level.
        var levels = new List<IReadOnlyList<StepDef>>();
        var done = new HashSet<string>(StringComparer.Ordinal);
        var remaining = recipe.Steps.ToList();

        while (remaining.Count > 0)
        {
            var ready = remaining.Where(s => deps[s.Alias].All(done.Contains)).ToList();
            if (ready.Count == 0)
                throw new InvalidOperationException(
                    $"Steps depend on each other in a loop: {string.Join(", ", remaining.Select(s => s.Alias))}.");

            levels.Add(ready);
            foreach (var s in ready) { done.Add(s.Alias); remaining.Remove(s); }
        }

        return new ExecutionPlan(levels, deps);
    }

    /// <summary>All expression roots a step reads (request, aliases, item...).</summary>
    public static IEnumerable<string> ReferencedRoots(StepDef step) =>
        Expressions(step).Select(e => ValuePath.Parse(e).Root).Distinct(StringComparer.Ordinal);

    /// <summary>Every expression used by a step, without the {{ }}.</summary>
    public static IEnumerable<string> Expressions(StepDef step)
    {
        foreach (var b in step.Bind)
        {
            if (!string.IsNullOrWhiteSpace(b.From)) yield return b.From.Trim();
            foreach (var e in Template.FindExpressions(b.Template)) yield return e;
        }
        foreach (var e in Template.FindExpressions(step.When)) yield return e;
        foreach (var e in Template.FindExpressions(step.ForEach)) yield return e;
    }
}
