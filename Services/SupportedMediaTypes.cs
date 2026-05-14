using MauiMediaPlayer.Models;

namespace MauiMediaPlayer.Services;

public static class SupportedMediaTypes
{
    public static readonly IReadOnlySet<string> VideoExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".3g2", ".3gp", ".asf", ".avi", ".flv", ".m2ts", ".m4v", ".mkv", ".mov", ".mp4",
        ".mpeg", ".mpg", ".mts", ".ogm", ".ogv", ".rm", ".rmvb", ".ts", ".vob", ".webm", ".wmv"
    };

    public static readonly IReadOnlySet<string> ImageExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".bmp", ".heic", ".heif", ".jfif", ".jpeg", ".jpg", ".png", ".tif", ".tiff", ".webp"
    };

    public static readonly IReadOnlySet<string> GifExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".gif"
    };

    public static readonly IReadOnlySet<string> AllExtensions = new HashSet<string>(
        VideoExtensions.Concat(ImageExtensions).Concat(GifExtensions),
        StringComparer.OrdinalIgnoreCase);

    public static MediaKind GetKind(string path)
    {
        var extension = GetFinalExtension(path);

        if (GifExtensions.Contains(extension))
        {
            return MediaKind.Gif;
        }

        if (ImageExtensions.Contains(extension))
        {
            return MediaKind.Image;
        }

        if (VideoExtensions.Contains(extension))
        {
            return MediaKind.Video;
        }

        return MediaKind.Unknown;
    }

    public static string GetFinalExtension(string path)
    {
        var fileName = Path.GetFileName(path);
        var extension = Path.GetExtension(fileName);

        return extension.ToLowerInvariant();
    }
}
