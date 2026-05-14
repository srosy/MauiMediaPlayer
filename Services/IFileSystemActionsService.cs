using MauiMediaPlayer.Models;

namespace MauiMediaPlayer.Services;

public interface IFileSystemActionsService
{
    Task RevealInFileManagerAsync(MediaItem item);
}
