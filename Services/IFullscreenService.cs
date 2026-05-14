namespace MauiMediaPlayer.Services;

public interface IFullscreenService
{
    bool IsSupported { get; }

    bool IsFullscreen { get; }

    event Action? FullscreenChanged;

    Task ToggleAsync();

    Task ExitAsync();
}
