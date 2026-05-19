using MauiMediaPlayer.Models;

namespace MauiMediaPlayer.Services;

public sealed class NoOpImageDisplayUriProvider : IImageDisplayUriProvider
{
    public bool TryGetDisplayUri(MediaItem item, out string uri)
    {
        uri = string.Empty;
        return false;
    }
}
