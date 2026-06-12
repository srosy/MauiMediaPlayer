namespace MauiMediaPlayer.Models;

public sealed record SkippedMediaFile(string Path, string Reason);

public sealed record IrregularMediaFile(
    string FilePath,
    string DisplayName,
    string DeclaredExtension,
    string DetectedExtension,
    MediaKind DetectedKind,
    string SuggestedFileName,
    string Reason);

public sealed record MediaScanResult(
    IReadOnlyList<MediaItem> Items,
    IReadOnlyList<SkippedMediaFile> SkippedFiles,
    IReadOnlyList<IrregularMediaFile> IrregularFiles)
{
    public static MediaScanResult Empty { get; } = new([], [], []);
}
