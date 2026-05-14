namespace MauiMediaPlayer.Services;

/// <summary>
/// Lightweight bus for popover-style UI. Provides two responsibilities:
/// <list type="number">
/// <item>A global "close all popovers" signal raised by outside clicks, Escape
/// key, or a modal opening — so the user never lands on stacked popovers.</item>
/// <item>An aggregate "is any popover currently open?" state so the shell can
/// suppress the native MediaElement overlay while a popover is up (the native
/// Windows SwapChainPanel otherwise paints above HTML popovers, making the
/// video appear in front of the menu).</item>
/// </list>
/// Popovers should call <see cref="SetOpen(object, bool)"/> when their visible
/// state changes; the owner key (typically <c>this</c>) keeps the count
/// idempotent if a popover is toggled multiple times.
/// </summary>
public sealed class PopoverStateService
{
    private readonly HashSet<object> _openOwners = [];

    public event Action? CloseRequested;

    /// <summary>
    /// Fires when the aggregate <see cref="AnyOpen"/> value flips between
    /// <c>true</c> and <c>false</c>. Subscribers receive the new value.
    /// </summary>
    public event Action<bool>? AnyOpenChanged;

    public bool AnyOpen => _openOwners.Count > 0;

    public void CloseAll() => CloseRequested?.Invoke();

    /// <summary>
    /// Records that <paramref name="owner"/> is open or closed. Idempotent — a
    /// second "open" for the same owner does nothing. Only raises
    /// <see cref="AnyOpenChanged"/> when the aggregate boolean actually flips.
    /// </summary>
    public void SetOpen(object owner, bool open)
    {
        ArgumentNullException.ThrowIfNull(owner);

        var wasOpen = AnyOpen;
        var changed = open ? _openOwners.Add(owner) : _openOwners.Remove(owner);

        if (!changed)
        {
            return;
        }

        var nowOpen = AnyOpen;

        if (wasOpen != nowOpen)
        {
            AnyOpenChanged?.Invoke(nowOpen);
        }
    }
}
