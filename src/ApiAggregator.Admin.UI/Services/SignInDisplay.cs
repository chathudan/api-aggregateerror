using ApiAggregator.Admin.UI.Components.Shared;
using ApiAggregator.Engine.Model;

namespace ApiAggregator.Admin.UI.Services;

/// <summary>How a service's sign-in state is shown (Home and Services use the same words).</summary>
public static class SignInDisplay
{
    public sealed record Display(StatusPill.Kind State, string Text, string Message, DateTimeOffset? CheckedAt);

    public static Display For(ApiService service, IEnumerable<SignInStatusRow> statuses)
    {
        if (service.AuthProfileId is null) return new(StatusPill.Kind.Neutral, "No sign-in", "This service doesn't need a sign-in.", null);
        var row = statuses.FirstOrDefault(r => r.ProfileId == service.AuthProfileId);
        return row?.State switch
        {
            "signedIn" => new(StatusPill.Kind.Ok, "Signed in", row.Message, row.CheckedAt),
            "settingsOk" => new(StatusPill.Kind.Ok, "Ready", row.Message, row.CheckedAt),
            "failed" => new(StatusPill.Kind.Bad, "Failed", row.Message, row.CheckedAt),
            _ => new(StatusPill.Kind.Warn, "Not checked", "Not checked since the admin API started. Choose Check sign-in.", null)
        };
    }

    public static string Ago(DateTimeOffset? at)
    {
        if (at is not { } when) return "";
        var span = DateTimeOffset.UtcNow - when;
        return span.TotalMinutes < 1 ? "checked just now" : span.TotalHours < 1 ? $"checked {(int)span.TotalMinutes} min ago" : $"checked {when.ToLocalTime():d MMM, HH:mm}";
    }
}
