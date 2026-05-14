namespace MauiMediaPlayer.Models;

public sealed record SkippedMediaFile(string Path, string Reason);

public sealed record MediaScanResult(
    IReadOnlyList<MediaItem> Items,
    IReadOnlyList<SkippedMediaFile> SkippedFiles)
{
    public static MediaScanResult Empty { get; } = new([], []);
}
