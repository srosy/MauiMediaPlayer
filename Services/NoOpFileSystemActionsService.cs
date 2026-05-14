using MauiMediaPlayer.Models;

namespace MauiMediaPlayer.Services;

public sealed class NoOpFileSystemActionsService : IFileSystemActionsService
{
    public Task RevealInFileManagerAsync(MediaItem item)
    {
        return Task.CompletedTask;
    }
}
