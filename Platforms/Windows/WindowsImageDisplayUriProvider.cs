using MauiMediaPlayer.Models;
using MauiMediaPlayer.Services;

namespace MauiMediaPlayer.Platforms.Windows;

public sealed class WindowsImageDisplayUriProvider : IImageDisplayUriProvider
{
    // WebView2 loads local files directly; avoid in-memory data URIs for large photos.
    private const long MaxFileUriBytes = 256L * 1024 * 1024;

    public bool TryGetDisplayUri(MediaItem item, out string uri)
    {
        uri = string.Empty;

        if (item.Kind is not (MediaKind.Image or MediaKind.Gif))
        {
            return false;
        }

        if (item.SizeBytes <= 0 || item.SizeBytes > MaxFileUriBytes || !File.Exists(item.FilePath))
        {
            return false;
        }

        try
        {
            uri = new Uri(Path.GetFullPath(item.FilePath)).AbsoluteUri;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
