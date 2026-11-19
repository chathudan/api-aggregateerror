namespace ApiAggregator.Engine.BrowserAccess;

/// <summary>An origin (or pattern) that passed the rules, or the plain-English reason it didn't.</summary>
public readonly record struct OriginCheck(string? Value, string? Problem)
{
    public bool Ok => Problem is null;
    public static OriginCheck Good(string value) => new(value, null);
    public static OriginCheck Bad(string problem) => new(null, problem);
}

/// <summary>
/// The rules for browser origins (ADR 0015). Pure functions shared by the gateway (startup and every policy
/// rebuild), the admin app (adding an origin, "Test an origin") and the CLI. Messages are read by technicians.
/// </summary>
public static class OriginRules
{
    public const int MaxAdminEntries = 50;
    public const int MaxNoteLength = 200;
    private const int MaxOriginLength = 253 + 20;

    /// <summary>
    /// Checks one exact origin and returns it canonical (lowercase <c>scheme://host[:port]</c>). Outside Development,
    /// plain http is allowed only for localhost.
    /// </summary>
    public static OriginCheck CheckOrigin(string? text, bool isDevelopment)
    {
        var value = text?.Trim() ?? "";
        if (value.Length == 0) return OriginCheck.Bad("Enter an origin, for example https://app.example.com.");
        if (value == "*") return OriginCheck.Bad("\"*\" would let every website call the gateway. Add each website's origin instead.");
        if (value.Equals("null", StringComparison.OrdinalIgnoreCase))
            return OriginCheck.Bad("\"null\" is what sandboxed pages and local files send, so allowing it would let any of them in. Add the website's real origin.");
        if (value.Contains('*')) return OriginCheck.Bad("Wildcards aren't allowed here. Add the exact origin, for example https://app.example.com.");

        if (Parse(value, out var parsed) is { } problem) return OriginCheck.Bad(problem);
        if (parsed.Scheme == "http" && !isDevelopment && parsed.Host != "localhost")
            return OriginCheck.Bad("Use https://. Plain http:// is only allowed for localhost outside development.");
        return OriginCheck.Good(parsed.ToString());
    }

    /// <summary>Checks a pattern like <c>https://*.example.com</c>: "*." only at the start, then at least two labels.</summary>
    public static OriginCheck CheckPattern(string? text)
    {
        var value = text?.Trim() ?? "";
        var star = value.IndexOf("://*.", StringComparison.Ordinal);
        if (star <= 0 || value.IndexOf('*') != star + 3)
            return OriginCheck.Bad($"\"{Shorten(value)}\" isn't a pattern. Use the form https://*.example.com (\"*.\" only at the start of the host).");

        // Check the rest as an origin with a stand-in label for the "*".
        if (Parse(value[..(star + 3)] + "x" + value[(star + 4)..], out var parsed) is { } problem) return OriginCheck.Bad(problem);
        var domain = parsed.Host[2..];
        if (domain.Split('.').Length < 2 || parsed.Host.StartsWith('['))
            return OriginCheck.Bad($"\"{Shorten(value)}\" is too wide. A pattern needs at least two parts after \"*.\", like https://*.example.com.");
        return OriginCheck.Good(parsed.ToString().Replace("://x.", "://*.", StringComparison.Ordinal));
    }

    /// <summary>
    /// True when a canonical origin matches a checked pattern: same scheme and port, and the host is one or more
    /// labels under the pattern's domain. Patterns only limit what admins may add; they never allow an origin.
    /// </summary>
    public static bool Matches(string origin, string pattern)
    {
        if (Parse(origin, out var o) is not null) return false;
        if (Parse(pattern.Replace("://*.", "://x.", StringComparison.Ordinal), out var p) is not null) return false;
        var domain = p.Host[1..]; // ".example.com"
        return o.Scheme == p.Scheme && o.Port == p.Port && o.Host.Length > domain.Length && o.Host.EndsWith(domain, StringComparison.Ordinal);
    }

    /// <summary>Checks an optional note: up to 200 characters, no control characters.</summary>
    public static string? CheckNote(string? note) =>
        note is null || (note.Length <= MaxNoteLength && !note.Any(char.IsControl))
            ? null
            : $"Keep the note to {MaxNoteLength} characters on one line.";

    /// <summary>Everything that decides whether an admin may add this origin. Returns the canonical origin.</summary>
    public static OriginCheck CheckAdminEntry(string? origin, string? note, IReadOnlyCollection<BrowserOrigin> adminEntries,
        BrowserAccessSettings settings, bool isDevelopment)
    {
        var check = CheckOrigin(origin, isDevelopment);
        if (!check.Ok) return check;
        var canonical = check.Value!;
        if (CheckNote(note) is { } noteProblem) return OriginCheck.Bad(noteProblem);

        if (OperationsOrigins(settings, isDevelopment).Contains(canonical))
            return OriginCheck.Bad("Operations already allow this origin in the app settings, so it doesn't need adding here.");
        if (adminEntries.Any(e => e.Origin == canonical)) return OriginCheck.Bad("This origin is already on the list.");
        if (adminEntries.Count >= MaxAdminEntries)
            return OriginCheck.Bad($"There are already {MaxAdminEntries} origins added here, the most allowed. Remove one that's no longer needed first.");
        if (PatternProblem(canonical, settings) is { } patternProblem) return OriginCheck.Bad(patternProblem);
        return OriginCheck.Good(canonical);
    }

    /// <summary>Null when the origin fits the operations patterns (or there are none), otherwise why not.</summary>
    public static string? PatternProblem(string canonicalOrigin, BrowserAccessSettings settings)
    {
        var patterns = Patterns(settings);
        if (patterns.Count == 0 || patterns.Any(p => Matches(canonicalOrigin, p))) return null;
        return $"Operations only allow origins that match {string.Join(" or ", patterns)}. Ask operations if this website needs access.";
    }

    /// <summary>Stops the app from starting with settings that can't work. Messages name the setting and the fix.</summary>
    public static void ValidateSettings(BrowserAccessSettings settings, bool isDevelopment)
    {
        foreach (var origin in settings.AllowedOrigins ?? [])
            if (CheckOrigin(origin, isDevelopment) is { Ok: false } bad)
                throw new InvalidOperationException($"{BrowserAccessSettings.Section}:AllowedOrigins has \"{Shorten(origin)}\", which can't be used: {bad.Problem}");
        foreach (var pattern in settings.AllowedOriginPatterns ?? [])
            if (CheckPattern(pattern) is { Ok: false } bad)
                throw new InvalidOperationException($"{BrowserAccessSettings.Section}:AllowedOriginPatterns: {bad.Problem}");
        if (settings.PreflightMaxAgeSeconds is < 0 or > 86_400)
            throw new InvalidOperationException($"{BrowserAccessSettings.Section}:PreflightMaxAgeSeconds must be between 0 and 86400 (one day).");
    }

    /// <summary>The operations origins, canonical. Assumes <see cref="ValidateSettings"/> passed; anything else is skipped.</summary>
    public static IReadOnlySet<string> OperationsOrigins(BrowserAccessSettings settings, bool isDevelopment) =>
        (settings.AllowedOrigins ?? []).Select(o => CheckOrigin(o, isDevelopment)).Where(c => c.Ok).Select(c => c.Value!).ToHashSet(StringComparer.Ordinal);

    public static IReadOnlyList<string> Patterns(BrowserAccessSettings settings) =>
        (settings.AllowedOriginPatterns ?? []).Select(CheckPattern).Where(c => c.Ok).Select(c => c.Value!).Distinct().ToList();

    /// <summary>For log lines: the canonical origin when it is one, otherwise a fixed text (never the raw header).</summary>
    public static string ForLog(string? header) =>
        Parse(header?.Trim() ?? "", out var parsed) is null ? parsed.ToString() : "(not a valid origin)";

    // ------------------------------------------------------------------ parsing

    private readonly record struct Parsed(string Scheme, string Host, int? Port)
    {
        public override string ToString() => Port is null ? $"{Scheme}://{Host}" : $"{Scheme}://{Host}:{Port}";
    }

    /// <summary>Null when <paramref name="value"/> is exactly scheme://host[:port]; otherwise the problem.</summary>
    private static string? Parse(string value, out Parsed parsed)
    {
        parsed = default;
        if (value.Length > MaxOriginLength) return "That's too long to be an origin.";
        if (value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))) return "An origin can't contain spaces or line breaks.";

        var sep = value.IndexOf("://", StringComparison.Ordinal);
        var scheme = sep > 0 ? value[..sep].ToLowerInvariant() : "";
        if (scheme is not ("http" or "https")) return "Start with https:// (or http:// for localhost), for example https://app.example.com.";

        var rest = value[(sep + 3)..];
        var end = rest.IndexOfAny(['/', '?', '#']);
        if (end >= 0)
            return end == rest.Length - 1 && rest[end] == '/'
                ? $"Leave out the trailing slash: {scheme}://{rest[..end]}"
                : "An origin has no path, query or #. Use only the scheme, host and port, like https://app.example.com.";
        if (rest.Contains('@')) return "An origin can't contain a user name or password.";
        if (rest.Any(c => c > 127)) return "Use the xn-- form of an international domain name (that's what browsers send).";

        string host;
        string? portText = null;
        if (rest.StartsWith('['))
        {
            var close = rest.IndexOf(']');
            if (close < 0) return "That host isn't valid.";
            host = rest[..(close + 1)];
            if (host.Length < 4 || !host[1..^1].All(c => char.IsAsciiHexDigit(c) || c == ':')) return "That host isn't valid.";
            var after = rest[(close + 1)..];
            if (after.Length > 0)
            {
                if (after[0] != ':') return "That host isn't valid.";
                portText = after[1..];
            }
        }
        else
        {
            var colon = rest.IndexOf(':');
            host = colon < 0 ? rest : rest[..colon];
            if (colon >= 0) portText = rest[(colon + 1)..];
            if (host.Length == 0 || host.Split('.').Any(l => l.Length is 0 or > 63 || l.StartsWith('-') || l.EndsWith('-')
                    || !l.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')))
                return "That host isn't valid. Use a name like app.example.com.";
        }

        int? port = null;
        if (portText is not null)
        {
            if (portText.Length is 0 or > 5 || !portText.All(char.IsAsciiDigit) || int.Parse(portText) is < 1 or > 65535)
                return "The port must be a number from 1 to 65535.";
            port = int.Parse(portText);
            if ((scheme, port) is ("https", 443) or ("http", 80))
                return $"Leave out :{port}. Browsers don't send the default port.";
        }

        parsed = new Parsed(scheme, host.ToLowerInvariant(), port);
        return null;
    }

    private static string Shorten(string? value) => value is null ? "" : value.Length <= 80 ? value : value[..80] + "…";
}
