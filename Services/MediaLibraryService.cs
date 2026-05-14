using MauiMediaPlayer.Models;

namespace MauiMediaPlayer.Services;

public sealed class MediaLibraryService(IFolderPickerService folderPickerService, DebugLogService debugLog)
{
    private const int MaxCachedPickedFiles = 50;

    public bool SupportsFolderPicking => folderPickerService.IsFolderPickingSupported;

    public async Task<IReadOnlyList<string>> PickFilePathsAsync(CancellationToken cancellationToken = default)
    {
        var fileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
        {
            [DevicePlatform.Android] = ["image/*", "video/*"],
            [DevicePlatform.iOS] = ["public.image", "public.movie"],
            [DevicePlatform.MacCatalyst] = ["public.image", "public.movie"],
            [DevicePlatform.WinUI] = SupportedMediaTypes.AllExtensions.ToArray()
        });

        var files = await FilePicker.Default.PickMultipleAsync(new PickOptions
        {
            PickerTitle = "Select media files",
            FileTypes = fileTypes
        }) ?? [];

        var paths = new List<string>();

        foreach (var file in files.Where(static file => file is not null))
        {
            if (file!.FullPath is { Length: > 0 } fullPath && File.Exists(fullPath))
            {
                paths.Add(fullPath);
                continue;
            }

            var cachedPath = await CachePickedFileAsync(file, cancellationToken);

            if (!string.IsNullOrWhiteSpace(cachedPath))
            {
                paths.Add(cachedPath);
            }
        }

        return paths;
    }

    public async Task<string?> PickFolderPathAsync(CancellationToken cancellationToken = default)
    {
        return await folderPickerService.PickFolderAsync(cancellationToken);
    }

    public async Task<MediaScanResult> PickFilesAsync(CancellationToken cancellationToken = default)
    {
        var paths = await PickFilePathsAsync(cancellationToken);
        return LoadFromPaths(paths, recursive: false);
    }

    public async Task<MediaScanResult> PickFolderAsync(bool recursive, CancellationToken cancellationToken = default)
    {
        var folderPath = await PickFolderPathAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return MediaScanResult.Empty;
        }

        return LoadFromPaths([folderPath], recursive);
    }

    public MediaScanResult LoadFromPaths(IEnumerable<string> paths, bool recursive)
    {
        var items = new List<MediaItem>();
        var skippedFiles = new List<SkippedMediaFile>();
        var distinctPaths = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        debugLog.Info($"Scanning {distinctPaths.Length} path(s); recursive={recursive}.");

        foreach (var path in distinctPaths)
        {
            if (File.Exists(path))
            {
                debugLog.Trace($"Scanning file: {path}");
                AddFile(path, sourceFolder: null, items, skippedFiles);
                continue;
            }

            if (Directory.Exists(path))
            {
                debugLog.Trace($"Scanning folder: {path}");
                AddFolder(path, recursive, items, skippedFiles);
                continue;
            }

            skippedFiles.Add(new SkippedMediaFile(path, "File or folder was not found."));
        }

        debugLog.Info($"Scan complete. Loaded={items.Count}; skipped={skippedFiles.Count}.");
        return new MediaScanResult(items, skippedFiles);
    }

    private static void AddFolder(string folderPath, bool recursive, List<MediaItem> items, List<SkippedMediaFile> skippedFiles)
    {
        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = recursive,
                IgnoreInaccessible = true
            };

            foreach (var filePath in Directory.EnumerateFiles(folderPath, "*", options))
            {
                TryAddFile(filePath, folderPath, items, skippedFiles);
            }
        }
        catch (UnauthorizedAccessException)
        {
            skippedFiles.Add(new SkippedMediaFile(folderPath, "Access denied while scanning folder."));
        }
        catch (IOException exception)
        {
            skippedFiles.Add(new SkippedMediaFile(folderPath, exception.Message));
        }
    }

    private static void AddFile(string filePath, string? sourceFolder, List<MediaItem> items, List<SkippedMediaFile> skippedFiles)
    {
        TryAddFile(filePath, sourceFolder, items, skippedFiles);
    }

    private static void TryAddFile(string filePath, string? sourceFolder, List<MediaItem> items, List<SkippedMediaFile> skippedFiles)
    {
        var kind = SupportedMediaTypes.GetKind(filePath);

        if (kind == MediaKind.Unknown)
        {
            skippedFiles.Add(new SkippedMediaFile(filePath, "Unsupported file extension."));
            return;
        }

        try
        {
            items.Add(MediaItem.FromFile(filePath, kind, sourceFolder));
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException or IOException)
        {
            skippedFiles.Add(new SkippedMediaFile(filePath, exception.Message));
        }
    }

    private async Task<string?> CachePickedFileAsync(FileResult file, CancellationToken cancellationToken)
    {
        try
        {
            var cacheFolder = Path.Combine(FileSystem.CacheDirectory, "PickedMedia");
            Directory.CreateDirectory(cacheFolder);
            CleanupPickedMediaCache(cacheFolder);

            var fileName = GetSafeFileName(file.FileName);
            var cachedPath = Path.Combine(cacheFolder, $"{Guid.NewGuid():N}_{fileName}");

            await using var source = await file.OpenReadAsync();
            await using var destination = File.Create(cachedPath);
            await source.CopyToAsync(destination, cancellationToken);

            debugLog.Debug($"Cached picked media file for local playback: {file.FileName}; path={cachedPath}");
            return cachedPath;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            debugLog.Error($"Could not cache picked media file {file.FileName}: {exception.Message}");
            return null;
        }
    }

    private static string GetSafeFileName(string? fileName)
    {
        var safeName = string.IsNullOrWhiteSpace(fileName) ? "picked-media" : fileName;

        foreach (var invalidCharacter in Path.GetInvalidFileNameChars())
        {
            safeName = safeName.Replace(invalidCharacter, '_');
        }

        return safeName;
    }

    private void CleanupPickedMediaCache(string cacheFolder)
    {
        try
        {
            var files = Directory
                .EnumerateFiles(cacheFolder)
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ToArray();

            foreach (var file in files.Skip(MaxCachedPickedFiles))
            {
                file.Delete();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            debugLog.Trace($"Could not clean picked media cache: {exception.Message}");
        }
    }
}
