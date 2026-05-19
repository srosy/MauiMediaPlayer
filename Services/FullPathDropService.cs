namespace MauiMediaPlayer.Services;

public sealed class FullPathDropService
{
    public event Func<string[], Task>? PathsDropped;

    public Task NotifyPathsDroppedAsync(string[] paths)
    {
        var handler = PathsDropped;
        return handler is null ? Task.CompletedTask : handler.Invoke(paths);
    }
}
