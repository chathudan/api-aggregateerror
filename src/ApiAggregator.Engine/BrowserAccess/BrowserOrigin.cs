namespace ApiAggregator.Engine.BrowserAccess;

/// <summary>
/// One website allowed to call /bff/* from a browser (CORS, ADR 0015), added in the admin app.
/// <see cref="Origin"/> is canonical: lowercase <c>scheme://host[:port]</c>, exactly what browsers send.
/// </summary>
public sealed class BrowserOrigin
{
    public required string Origin { get; init; }

    /// <summary>Why it's there, e.g. "Patient portal (web team)". Optional.</summary>
    public string? Note { get; init; }

    public DateTimeOffset? AddedAt { get; init; }

    /// <summary>The subject id of the person who added it (never an email).</summary>
    public string? AddedBy { get; init; }

    public string? AddedByName { get; init; }
}

/// <summary>The content of config/cors.json.</summary>
public sealed class BrowserAccessFile
{
    public List<BrowserOrigin> Origins { get; init; } = [];
}

public enum BrowserAccessAction { Added, Removed, Changed }

/// <summary>
/// One change to the admin-managed origins. Like recipe history (ADR 0012): written once, never changed, and
/// <see cref="Number"/> is never reused.
/// </summary>
public sealed class BrowserAccessHistoryEntry
{
    public required int Number { get; init; }
    public required BrowserAccessAction Action { get; init; }
    public required string Origin { get; init; }
    public string? Note { get; init; }
    public DateTimeOffset At { get; init; }
    public string? By { get; init; }
    public string? ByName { get; init; }

    /// <summary>For <see cref="BrowserAccessAction.Changed"/>: the origin and note before the change.</summary>
    public string? PreviousOrigin { get; init; }
    public string? PreviousNote { get; init; }
}

/// <summary>
/// The limits operations set in app settings (<c>Gateway:Cors</c>). The gateway and the admin app read the same
/// values; the gateway re-checks every admin entry against them, so a mismatch can never widen access.
/// </summary>
public sealed class BrowserAccessSettings
{
    public const string Section = "Gateway:Cors";

    /// <summary>Always allowed. Can't be removed in the admin app.</summary>
    public string[] AllowedOrigins { get; set; } = [];

    /// <summary>When set, every origin added in the admin app must match one, e.g. <c>https://*.example.com</c>.</summary>
    public string[] AllowedOriginPatterns { get; set; } = [];

    /// <summary>How long browsers may remember a preflight answer.</summary>
    public int PreflightMaxAgeSeconds { get; set; } = 600;
}
