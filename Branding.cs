namespace MauiMediaPlayer;

/// <summary>
/// Single source of truth for the app's user-facing identity: name, tagline,
/// version, brand color. Centralized so renames or rebrands touch one file.
/// The .csproj <ApplicationTitle /> and the launcher icon swap should be kept
/// in sync with these values manually when changed (those control the OS-level
/// shell label and tile color outside of the running app).
/// </summary>
public static class Branding
{
    public const string AppName = "Mosaic";

    public const string Tagline = "Local media player & slideshow";

    public const string Version = "1.0";

    public const string BrandAccentHex = "#7C3AED";

    public const string Credits = "Built on .NET MAUI Blazor Hybrid · CommunityToolkit MediaElement · Bootstrap Icons (MIT)";

    public static string FormatWindowTitle(string? nowPlaying)
    {
        if (string.IsNullOrWhiteSpace(nowPlaying))
        {
            return AppName;
        }

        return $"{nowPlaying} — {AppName}";
    }
}
