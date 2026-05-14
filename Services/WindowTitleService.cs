namespace MauiMediaPlayer.Services;

/// <summary>
/// Pushes window-chrome title updates from anywhere in the app. The MAUI Window
/// itself is the source of truth; this service is a thin event channel so that
/// .NET MAUI views (which live on the UI thread) can listen and update their
/// title property, while Blazor components dispatch raw strings without taking
/// a direct dependency on MAUI internals.
/// </summary>
public sealed class WindowTitleService
{
    private string _currentTitle = Branding.AppName;

    public event Action<string>? TitleChanged;

    public string CurrentTitle => _currentTitle;

    /// <summary>
    /// Set the window title to reflect the currently-playing item. A null or
    /// empty value falls back to the bare app name. Repeat values are coalesced
    /// to skip needless UI work.
    /// </summary>
    public void SetNowPlaying(string? nowPlaying)
    {
        var title = Branding.FormatWindowTitle(nowPlaying);

        if (string.Equals(_currentTitle, title, StringComparison.Ordinal))
        {
            return;
        }

        _currentTitle = title;
        TitleChanged?.Invoke(title);
    }
}
