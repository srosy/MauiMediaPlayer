namespace MauiMediaPlayer.Services;

/// <summary>
/// Lightweight bus for popover-style UI. Provides two responsibilities:
/// <list type="number">
/// <item>A global "close all popovers" signal raised by outside clicks, Escape
/// key, or a modal opening — so the user never lands on stacked popovers.</item>
/// <item>An aggregate "is any popover currently open?" state, plus optional
/// native-video suppression for popovers that overlap the media viewport (the
/// Windows SwapChainPanel paints above HTML otherwise).</item>
/// </list>
/// Popovers should call <see cref="SetOpen(object, bool, bool)"/> when their
/// visible state changes; the owner key (typically <c>this</c>) keeps the count
/// idempotent if a popover is toggled multiple times.
/// </summary>
public sealed class PopoverStateService
{
    private readonly Dictionary<object, PopoverRegistration> _openOwners = new();

    public event Action? CloseRequested;

    /// <summary>
    /// Fires when the aggregate <see cref="AnyOpen"/> value flips between
    /// <c>true</c> and <c>false</c>. Subscribers receive the new value.
    /// </summary>
    public event Action<bool>? AnyOpenChanged;

    /// <summary>Raised when <see cref="SettingsPanelOpen"/> changes.</summary>
    public event Action? SettingsPanelOpenChanged;

    public bool AnyOpen => _openOwners.Count > 0;

    /// <summary>
    /// The playback settings panel is rendered at the shell root (not inside the
    /// transport bar) so it stacks above media pane captions.
    /// </summary>
    public bool SettingsPanelOpen { get; private set; }

    /// <summary>
    /// True when any open popover asked to hide native MediaElement surfaces.
    /// Header overflow menus that sit above the viewport do not set this.
    /// </summary>
    public bool AnySuppressesNativeVideo =>
        _openOwners.Values.Any(registration => registration.SuppressNativeVideo);

    public void CloseAll()
    {
        var wasOpen = AnyOpen;
        CloseRequested?.Invoke();

        // CloseRequested handlers should call SetOpen(owner, false), but paths
        // like outside-click on the overflow menu only cleared local UI state.
        // Clear any orphaned owners so AnyOpen cannot stay true forever.
        if (_openOwners.Count > 0)
        {
            _openOwners.Clear();
        }

        SetSettingsPanelOpen(false);

        if (wasOpen)
        {
            AnyOpenChanged?.Invoke(false);
        }
    }

    public void SetSettingsPanelOpen(bool open)
    {
        if (SettingsPanelOpen == open)
        {
            return;
        }

        SettingsPanelOpen = open;
        SettingsPanelOpenChanged?.Invoke();
    }

    /// <summary>
    /// Records that <paramref name="owner"/> is open or closed. Idempotent — a
    /// second "open" for the same owner does nothing. Only raises
    /// <see cref="AnyOpenChanged"/> when the aggregate boolean actually flips.
    /// </summary>
    /// <param name="suppressNativeVideo">
    /// When <c>true</c> (default), the shell hides native video while this
    /// popover is open. Use <c>false</c> for header menus that do not overlap
    /// the media panes (e.g. the overflow hamburger).
    /// </param>
    public void SetOpen(object owner, bool open, bool suppressNativeVideo = true)
    {
        ArgumentNullException.ThrowIfNull(owner);

        var wasOpen = AnyOpen;
        var wasSuppressing = AnySuppressesNativeVideo;

        if (open)
        {
            if (_openOwners.TryGetValue(owner, out var existing)
                && existing.SuppressNativeVideo == suppressNativeVideo)
            {
                return;
            }

            _openOwners[owner] = new PopoverRegistration(suppressNativeVideo);
        }
        else
        {
            if (!_openOwners.Remove(owner))
            {
                return;
            }
        }

        var nowOpen = AnyOpen;
        var nowSuppressing = AnySuppressesNativeVideo;

        if (wasOpen != nowOpen || wasSuppressing != nowSuppressing)
        {
            AnyOpenChanged?.Invoke(nowOpen);
        }
    }

    private readonly record struct PopoverRegistration(bool SuppressNativeVideo);
}
