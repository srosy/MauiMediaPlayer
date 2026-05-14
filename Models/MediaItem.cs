using MauiMediaPlayer.Services;

namespace MauiMediaPlayer.Models;

public sealed record MediaItem(
    Guid Id,
    string FilePath,
    string DisplayName,
    MediaKind Kind,
    string Extension,
    string? SourceFolder,
    DateTimeOffset LastModified,
    long SizeBytes)
{
    public string SourceUri => new Uri(FilePath).AbsoluteUri;

    public bool IsVideo => Kind == MediaKind.Video;

    public bool IsTimedVisual => Kind is MediaKind.Image or MediaKind.Gif;

    public static MediaItem FromFile(string filePath, MediaKind kind, string? sourceFolder = null)
    {
        var fileInfo = new FileInfo(filePath);

        return new MediaItem(
            Guid.NewGuid(),
            fileInfo.FullName,
            Path.GetFileName(filePath),
            kind,
            SupportedMediaTypes.GetFinalExtension(filePath),
            sourceFolder,
            fileInfo.Exists ? fileInfo.LastWriteTimeUtc : DateTimeOffset.MinValue,
            fileInfo.Exists ? fileInfo.Length : 0);
    }
}
