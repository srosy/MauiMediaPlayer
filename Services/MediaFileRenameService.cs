namespace MauiMediaPlayer.Services;

public sealed class MediaFileRenameService(DebugLogService debugLog)
{
    private const string LogPrefix = "FilenameRepair:";

    public Task<RenameFileResult> TryRenameAsync(string sourcePath, string targetFileName, CancellationToken cancellationToken = default)
    {
        return Task.Run(() => TryRenameCore(sourcePath, targetFileName), cancellationToken);
    }

    private RenameFileResult TryRenameCore(string sourcePath, string targetFileName)
    {
        try
        {
            var directory = Path.GetDirectoryName(sourcePath);

            if (string.IsNullOrWhiteSpace(directory))
            {
                return RenameFileResult.Failed("Could not resolve the file directory.");
            }

            var resolvedTargetFileName = ResolveCollisionSafeFileName(directory, targetFileName, sourcePath);
            var targetPath = Path.Combine(directory, resolvedTargetFileName);

            if (string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(targetPath), StringComparison.OrdinalIgnoreCase))
            {
                return RenameFileResult.Succeeded(sourcePath, targetPath, renamed: false);
            }

            File.Move(sourcePath, targetPath);
            debugLog.Info($"{LogPrefix} renamed {Path.GetFileName(sourcePath)} → {resolvedTargetFileName}.");
            return RenameFileResult.Succeeded(sourcePath, targetPath, renamed: true);
        }
        catch (IOException exception)
        {
            debugLog.Debug($"{LogPrefix} rename failed for {Path.GetFileName(sourcePath)}: {exception.Message}");
            return RenameFileResult.Failed(exception.Message);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or NotSupportedException)
        {
            debugLog.Debug($"{LogPrefix} rename failed for {Path.GetFileName(sourcePath)}: {exception.Message}");
            return RenameFileResult.Failed(exception.Message);
        }
    }

    private static string ResolveCollisionSafeFileName(string directory, string targetFileName, string sourcePath)
    {
        var candidate = targetFileName;
        var baseName = Path.GetFileNameWithoutExtension(targetFileName);
        var extension = Path.GetExtension(targetFileName);
        var suffix = 1;
        var sourceFullPath = Path.GetFullPath(sourcePath);

        while (IsBlockingCollision(directory, candidate, sourceFullPath))
        {
            candidate = $"{baseName} ({suffix}){extension}";
            suffix++;
        }

        return candidate;
    }

    private static bool IsBlockingCollision(string directory, string candidateFileName, string sourceFullPath)
    {
        var candidatePath = Path.GetFullPath(Path.Combine(directory, candidateFileName));

        if (string.Equals(candidatePath, sourceFullPath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return File.Exists(candidatePath);
    }
}

public sealed record RenameFileResult(bool Success, string SourcePath, string TargetPath, bool Renamed, string? ErrorMessage)
{
    public static RenameFileResult Succeeded(string sourcePath, string targetPath, bool renamed) =>
        new(true, sourcePath, targetPath, renamed, null);

    public static RenameFileResult Failed(string errorMessage) =>
        new(false, string.Empty, string.Empty, false, errorMessage);
}
