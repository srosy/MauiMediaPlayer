using System.Diagnostics;
using MauiMediaPlayer.Models;
using MauiMediaPlayer.Services;

namespace MauiMediaPlayer.Platforms.Windows;

public sealed class WindowsFileSystemActionsService : IFileSystemActionsService
{
    public Task RevealInFileManagerAsync(MediaItem item)
    {
        if (!File.Exists(item.FilePath))
        {
            return Task.CompletedTask;
        }

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"/select,\"{item.FilePath}\"",
            UseShellExecute = true
        });

        return Task.CompletedTask;
    }
}
