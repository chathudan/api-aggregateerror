using ApiAggregator.Admin.Application;

namespace ApiAggregator.Admin.Services;

public enum ToastKind { Success, Info, Error }

/// <summary>One short message. <see cref="LinkHref"/> is optional (for example "See logs").</summary>
public sealed record Toast(Guid Id, ToastKind Kind, string Message, string? LinkText = null, string? LinkHref = null);

/// <summary>
/// Short "it worked" (or "it didn't") messages after saving, publishing, deleting... One per browser tab
/// (scoped to the Blazor circuit). The Toasts component in the layout shows them.
/// </summary>
public sealed class ToastService
{
    private readonly List<Toast> _toasts = [];

    public event Action? Changed;

    public IReadOnlyList<Toast> Current => _toasts;

    public void Success(string message, string? linkText = null, string? linkHref = null) => Add(ToastKind.Success, message, linkText, linkHref);
    public void Info(string message, string? linkText = null, string? linkHref = null) => Add(ToastKind.Info, message, linkText, linkHref);
    public void Error(string message, string? linkText = null, string? linkHref = null) => Add(ToastKind.Error, message, linkText, linkHref);

    public void Dismiss(Guid id)
    {
        _toasts.RemoveAll(t => t.Id == id);
        Changed?.Invoke();
    }

    private void Add(ToastKind kind, string message, string? linkText, string? linkHref)
    {
        var toast = new Toast(Guid.NewGuid(), kind, message, linkText, linkHref);
        _toasts.Add(toast);
        if (_toasts.Count > 4) _toasts.RemoveAt(0); // never pile up
        Changed?.Invoke();

        // Success and info go away by themselves; errors stay until closed, so nobody misses one.
        if (kind != ToastKind.Error) _ = DismissLater(toast.Id);
    }

    private async Task DismissLater(Guid id)
    {
        await Task.Delay(TimeSpan.FromSeconds(6));
        Dismiss(id);
    }
}

/// <summary>
/// What this installation and person can do, worked out once per browser tab. Only hides buttons:
/// AdminService checks the role on every action.
/// </summary>
public sealed class UiContext(AdminService admin, CurrentUser user)
{
    private static readonly Capabilities Nothing = new(false, false, true, false, []);
    private Task<Capabilities>? _capabilities;

    public Task<Capabilities> CapabilitiesAsync() => _capabilities ??= Load();

    private async Task<Capabilities> Load() => admin.GetCapabilities(await user.ActorAsync()).Value ?? Nothing;
}
