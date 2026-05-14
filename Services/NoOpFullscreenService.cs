namespace MauiMediaPlayer.Services;

public sealed class NoOpFullscreenService : IFullscreenService
{
    public bool IsSupported => false;

    public bool IsFullscreen => false;

    public event Action? FullscreenChanged
    {
        add { }
        remove { }
    }

    public Task ToggleAsync()
    {
        return Task.CompletedTask;
    }

    public Task ExitAsync()
    {
        return Task.CompletedTask;
    }
}
