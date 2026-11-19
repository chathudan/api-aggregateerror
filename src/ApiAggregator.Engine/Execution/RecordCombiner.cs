using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ApiAggregator.Engine.Expressions;
using ApiAggregator.Engine.Model;

namespace ApiAggregator.Engine.Execution;

/// <summary>How a combine went. Counts only, never values, so it is safe to log and to show in the admin trace.</summary>
internal sealed record CombineStats(IReadOnlyList<KeyValuePair<string, int>> Inputs, int Matched, int WithoutKey, int Ignored, int Output,
    int Unmapped = 0, int Ambiguous = 0)
{
    /// <summary>"crm:12,billing:9" (for logs).</summary>
    public string InputsText => string.Join(",", Inputs.Select(i => $"{i.Key}:{i.Value}"));

    /// <summary>
    /// "crm 12, billing 9 → 15 records (6 matched, 1 unmapped)" (for the admin trace).
    /// Counts that are zero are left out, except "matched", to keep it short.
    /// </summary>
    public string Describe()
    {
        var counts = new List<string> { $"{Matched} matched" };
        if (Unmapped > 0) counts.Add($"{Unmapped} unmapped");
        if (Ambiguous > 0) counts.Add($"{Ambiguous} ambiguous");
        if (WithoutKey > 0) counts.Add($"{WithoutKey} without a key");
        if (Ignored > 0) counts.Add($"{Ignored} ignored");
        return $"{string.Join(", ", Inputs.Select(i => $"{i.Key} {i.Value}"))} → {Output} records ({string.Join(", ", counts)})";
    }
}

/// <summary>Either the merged records plus stats, or a caller-safe error message.</summary>
internal sealed record CombineOutcome(JsonArray? Records, CombineStats? Stats, string? Error);

/// <summary>
/// Merges records from several lists by key. One linear pass with a dictionary index; deterministic
/// (same inputs, same output, same order).
///
/// Only TOP-LEVEL fields are merged. A nested object or array is taken whole from one source and is
/// never deep-merged: merging inside nested data would be guesswork, and wrong guesses on patient or
/// customer data are worse than a visible choice.
/// </summary>
internal static class RecordCombiner
{
    private const string NewestPrefix = "newest:";

    /// <summary>The label of a source: its Name, or the step name at the start of its From path.</summary>
    public static string SourceName(CombineSource source) =>
        !string.IsNullOrWhiteSpace(source.Name) ? source.Name.Trim() : ValuePath.Parse(source.From).Root;

    /// <summary>For OnConflict "newest:updatedAt" returns "updatedAt". Null for "priority" (or anything else).</summary>
    public static string? NewestField(string onConflict)
    {
        var text = onConflict.Trim();
        return text.StartsWith(NewestPrefix, StringComparison.OrdinalIgnoreCase) ? text[NewestPrefix.Length..].Trim() : null;
    }

    public static CombineOutcome Combine(string name, CombineDef def, IValueScope scope)
    {
        // 1. Read every source. A source whose step failed or was skipped is null and is simply left out
        //    (an optional step's failure is already reported in "_errors").
        var lists = new List<(string Name, List<JsonNode?> Items)>();
        var total = 0;
        foreach (var source in def.Sources)
        {
            var items = ItemsOf(ValuePath.Parse(source.From).Evaluate(scope));
            lists.Add((SourceName(source), items));
            total += items.Count;
        }

        // 2. Refuse to start when there is too much: merging part of a list would give a wrong answer.
        if (total > def.MaxRecords)
            return new CombineOutcome(null, null, $"The combined list '{name}' has more than {def.MaxRecords} records.");

        // 3. One pass. "index" finds the merged record for a key.
        var rules = new Rules(
            NewestField(def.OnConflict),
            string.Equals(def.Nulls, "overwrite", StringComparison.OrdinalIgnoreCase),
            def.Owners);
        var trim = def.Normalize.Contains("trim", StringComparer.OrdinalIgnoreCase);
        var lower = def.Normalize.Contains("lower", StringComparer.OrdinalIgnoreCase);
        var dropWithoutKey = string.Equals(def.WithoutKey, "drop", StringComparison.OrdinalIgnoreCase);

        var merged = new List<Entry>();
        var index = new Dictionary<string, Entry>(StringComparer.Ordinal);
        int matched = 0, withoutKey = 0, ignored = 0, unmapped = 0, ambiguous = 0;

        for (var i = 0; i < lists.Count; i++)
        {
            var (sourceName, items) = lists[i];
            var keyFields = Fields(def.Sources[i].Key);
            var mapping = def.Sources[i].Via is { } via ? BuildMapping(via, scope, trim, lower) : null; // built once per source

            foreach (var item in items)
            {
                if (item is not JsonObject record) { ignored++; continue; }

                // The record's own key; with a mapping, the key is looked up there and the SHARED id is used.
                var key = KeyOf(record, keyFields, trim, lower);
                var noKeyReason = NoKey.Missing;
                if (key is not null && mapping is not null)
                {
                    if (mapping.Ambiguous.Contains(key)) { key = null; noKeyReason = NoKey.Ambiguous; } // two different ids: never guess
                    else if (mapping.Shared.TryGetValue(key, out var shared)) key = shared;
                    else { key = null; noKeyReason = NoKey.Unmapped; }
                }

                if (key is null)
                {
                    switch (noKeyReason)
                    {
                        case NoKey.Unmapped: unmapped++; break;
                        case NoKey.Ambiguous: ambiguous++; break;
                        default: withoutKey++; break;
                    }
                    if (!dropWithoutKey) merged.Add(NewEntry(record, sourceName, rules)); // its own record, never matched
                    continue;
                }

                if (index.TryGetValue(key, out var existing))
                {
                    matched++;
                    Merge(existing, record, sourceName, rules);
                }
                else
                {
                    var entry = NewEntry(record, sourceName, rules);
                    index[key] = entry;
                    merged.Add(entry);
                }
            }
        }

        // 4. Output in first-seen order, then the optional stable sort.
        var output = merged.Select(e =>
        {
            if (def.TrackSources) e.Record["_sources"] = new JsonArray(e.Sources.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
            return e.Record;
        }).ToList();

        if (!string.IsNullOrWhiteSpace(def.SortBy))
        {
            var descending = def.SortBy.TrimStart().StartsWith('-');
            output = output.OrderBy(r => r, new FieldComparer(def.SortBy.Trim().TrimStart('-').Trim(), descending)).ToList(); // OrderBy is stable
        }

        var stats = new CombineStats(
            lists.Select(l => KeyValuePair.Create(l.Name, l.Items.Count)).ToList(), matched, withoutKey, ignored, output.Count, unmapped, ambiguous);
        return new CombineOutcome(new JsonArray(output.Select(r => (JsonNode?)r).ToArray()), stats, null);
    }

    // ------------------------------------------------------------------ mapping ("via")

    private enum NoKey { Missing, Unmapped, Ambiguous }

    /// <summary>Source key -> shared id, plus the source keys that map to more than one shared id.</summary>
    private sealed record Mapping(Dictionary<string, string> Shared, HashSet<string> Ambiguous);

    /// <summary>
    /// Reads the mapping rows once. If the mapping step failed (no rows), the dictionary is empty, so every
    /// record of the source counts as "unmapped" and that is visible in the stats.
    /// </summary>
    private static Mapping BuildMapping(CombineVia via, IValueScope scope, bool trim, bool lower)
    {
        var matchFields = Fields(via.Match);
        var useFields = Fields(via.Use);
        var shared = new Dictionary<string, string>(StringComparer.Ordinal);
        var ambiguous = new HashSet<string>(StringComparer.Ordinal);

        foreach (var row in ItemsOf(ValuePath.Parse(via.From).Evaluate(scope)))
        {
            if (row is not JsonObject link) continue;
            var from = KeyOf(link, matchFields, trim, lower);
            var to = KeyOf(link, useFields, trim, lower);
            if (from is null || to is null) continue;

            // The same source id pointing at two DIFFERENT shared ids can't be resolved safely.
            if (shared.TryGetValue(from, out var existing) && existing != to) ambiguous.Add(from);
            else shared[from] = to;
        }
        return new Mapping(shared, ambiguous);
    }

    private static string[] Fields(string commaSeparated) =>
        commaSeparated.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // ------------------------------------------------------------------ merging

    private sealed record Rules(string? NewestField, bool OverwriteNulls, IReadOnlyDictionary<string, string> Owners);

    /// <summary>One merged record, plus what we need to remember about where its fields came from.</summary>
    private sealed class Entry(JsonObject record, double? stamp)
    {
        public JsonObject Record { get; } = record;
        public Dictionary<string, string> FieldSource { get; } = new(StringComparer.Ordinal); // field -> source that supplied it
        public double? Stamp { get; set; } = stamp;                                            // timestamp, when OnConflict is newest:<field>
        public List<string> Sources { get; } = [];                                            // sources that contributed, in order
    }

    private static Entry NewEntry(JsonObject record, string source, Rules rules)
    {
        var entry = new Entry((JsonObject)record.DeepClone(), rules.NewestField is null ? null : Stamp(record, rules.NewestField));
        foreach (var (field, _) in entry.Record) entry.FieldSource[field] = source;
        entry.Sources.Add(source);
        return entry;
    }

    /// <summary>Folds an incoming record into the merged one, field by field (top level only).</summary>
    private static void Merge(Entry target, JsonObject incoming, string source, Rules rules)
    {
        var incomingStamp = rules.NewestField is null ? null : Stamp(incoming, rules.NewestField);
        var isNewer = incomingStamp is not null && (target.Stamp is null || incomingStamp > target.Stamp);

        foreach (var (field, value) in incoming)
        {
            if (value is null && !rules.OverwriteNulls) continue; // a null never erases a known value

            if (rules.Owners.TryGetValue(field, out var owner))
            {
                if (target.FieldSource.GetValueOrDefault(field) == owner) continue; // the owner already supplied it: never replace
                if (source == owner) { Set(target, field, value, source); continue; } // the owner always wins
            }

            // "priority": the earlier source keeps its value; a later one only fills a gap.
            // "newest": a newer record overwrites; an older one only fills a gap.
            if (isNewer || target.Record[field] is null) Set(target, field, value, source);
        }

        if (isNewer) target.Stamp = incomingStamp;
        if (!target.Sources.Contains(source)) target.Sources.Add(source);
    }

    private static void Set(Entry target, string field, JsonNode? value, string source)
    {
        target.Record[field] = value?.DeepClone(); // a node can have only one parent, so always copy
        target.FieldSource[field] = source;
    }

    // ------------------------------------------------------------------ keys, timestamps, sorting

    /// <summary>
    /// The record's key: each key field as text (JSON text for non-strings), normalised, joined with a
    /// separator that can't appear in normal data. Null when any part is missing or empty.
    /// </summary>
    private static string? KeyOf(JsonObject record, string[] fields, bool trim, bool lower)
    {
        if (fields.Length == 0) return null;

        var parts = new string[fields.Length];
        for (var i = 0; i < fields.Length; i++)
        {
            var node = JsonFields.Get(record, fields[i]);
            if (node is null) return null;

            var text = JsonText.Of(node) ?? "";
            if (trim) text = text.Trim();
            if (lower) text = text.ToLowerInvariant();
            if (text.Length == 0) return null;
            parts[i] = text;
        }
        return string.Join('\u001f', parts);
    }

    /// <summary>
    /// A timestamp as a comparable number: a date text becomes UTC ticks, a JSON number is used as it is.
    /// Missing or unparseable = null = oldest. Sources should use the same format for the field.
    /// </summary>
    private static double? Stamp(JsonObject record, string field)
    {
        if (JsonFields.Get(record, field) is not JsonValue value) return null;

        switch (value.GetValueKind())
        {
            case JsonValueKind.Number:
                return value.GetValue<double>();
            case JsonValueKind.String:
                return DateTimeOffset.TryParse(value.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
                    ? date.UtcTicks
                    : null;
            default:
                return null;
        }
    }

    private static List<JsonNode?> ItemsOf(JsonNode? value) => value switch
    {
        null => [],
        JsonArray array => array.ToList(),
        _ => [value] // a single object is a list of one (anything else is counted as "ignored" later)
    };

    /// <summary>Numbers compare as numbers, everything else as ordinal text. Missing or null values go last, ascending or descending.</summary>
    private sealed class FieldComparer(string field, bool descending) : IComparer<JsonObject>
    {
        public int Compare(JsonObject? x, JsonObject? y)
        {
            var a = JsonFields.Get(x, field);
            var b = JsonFields.Get(y, field);
            if (a is null && b is null) return 0;
            if (a is null) return 1;
            if (b is null) return -1;

            var result = IsNumber(a) && IsNumber(b)
                ? a.GetValue<double>().CompareTo(b.GetValue<double>())
                : string.CompareOrdinal(JsonText.Of(a), JsonText.Of(b));
            return descending ? -result : result;
        }

        private static bool IsNumber(JsonNode node) => node is JsonValue v && v.GetValueKind() == JsonValueKind.Number;
    }
}
