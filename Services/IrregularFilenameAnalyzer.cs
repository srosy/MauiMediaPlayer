using MauiMediaPlayer.Models;

namespace MauiMediaPlayer.Services;

public static class IrregularFilenameAnalyzer
{
    public static bool TryAnalyze(
        string filePath,
        MediaKind declaredKind,
        string declaredExtension,
        MediaSniffResult sniff,
        out IrregularMediaFile irregular)
    {
        var reasons = new List<string>();

        if (SupportedMediaTypes.HasCompoundExtension(filePath))
        {
            reasons.Add("compound-extension");
        }

        var canonicalExtension = SupportedMediaTypes.GetCanonicalExtension(sniff.Kind, sniff.Extension);

        if (!string.Equals(declaredExtension, canonicalExtension, StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add("extension-mismatch");
        }

        if (declaredKind == MediaKind.Unknown)
        {
            reasons.Add("unknown-extension");
        }

        if (reasons.Count == 0)
        {
            irregular = null!;
            return false;
        }

        var displayName = Path.GetFileName(filePath);
        var suggestedFileName = BuildSuggestedFileName(filePath, sniff.Kind, canonicalExtension);

        irregular = new IrregularMediaFile(
            filePath,
            displayName,
            declaredExtension,
            canonicalExtension,
            sniff.Kind,
            suggestedFileName,
            string.Join("|", reasons));

        return true;
    }

    public static string BuildSuggestedFileName(string filePath, MediaKind detectedKind, string detectedExtension)
    {
        var fileName = Path.GetFileName(filePath);
        var candidate = Path.GetFileNameWithoutExtension(fileName);

        while (SupportedMediaTypes.IsKnownExtension(Path.GetExtension(candidate)))
        {
            candidate = Path.GetFileNameWithoutExtension(candidate);
        }

        return candidate + SupportedMediaTypes.GetCanonicalExtension(detectedKind, detectedExtension);
    }
}
