using MauiMediaPlayer.Models;

namespace MauiMediaPlayer.Services;

/// <summary>
/// Platform-specific strategy for exposing local image files to the WebView without base64 data URIs.
/// </summary>
public interface IImageDisplayUriProvider
{
    bool TryGetDisplayUri(MediaItem item, out string uri);
}
