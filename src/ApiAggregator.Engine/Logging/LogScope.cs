using System.Collections;

namespace ApiAggregator.Engine.Logging;

/// <summary>
/// Log scope with named fields (CorrelationId, RecipeId, Step...). Structured providers (JSON console,
/// Application Insights, Seq) read the key/value pairs; text consoles print "CorrelationId:abc RecipeId:x".
/// </summary>
public sealed class LogScope(params (string Key, object? Value)[] fields) : IReadOnlyList<KeyValuePair<string, object?>>
{
    private readonly KeyValuePair<string, object?>[] _fields = fields.Select(f => KeyValuePair.Create(f.Key, f.Value)).ToArray();

    public KeyValuePair<string, object?> this[int index] => _fields[index];
    public int Count => _fields.Length;
    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => ((IEnumerable<KeyValuePair<string, object?>>)_fields).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public override string ToString() => string.Join(" ", _fields.Select(f => $"{f.Key}:{f.Value}"));
}
