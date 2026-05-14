using System.Text.Json;
using MauiMediaPlayer.Models;

namespace MauiMediaPlayer.Services;

public sealed class SettingsService
{
    private const string PlaybackSpeedKey = "playback-speed";
    private const string ImageDurationKey = "image-duration";
    private const string GifRepeatCountKey = "gif-repeat-count";
    private const string ShuffleKey = "shuffle";
    private const string LoopModeKey = "loop-mode";
    private const string RecursiveFolderScanKey = "recursive-folder-scan";
    private const string VolumeKey = "volume";
    private const string MutedKey = "muted";
    private const string SplitScreenModeKey = "split-screen-mode";
    private const string AlwaysShowVideoInSplitKey = "always-show-video-in-split";
    private const string IsolateVideoToCenterPanelKey = "isolate-video-to-center-panel";
    private const string RecentPathsKey = "recent-paths";
    private const string LastMediaPathsKey = "last-media-paths";

    public Task<PlaybackSettings> LoadAsync()
    {
        var settings = new PlaybackSettings
        {
            PlaybackSpeed = Preferences.Default.Get(PlaybackSpeedKey, PlaybackSettings.DefaultPlaybackSpeed),
            ImageDurationSeconds = Preferences.Default.Get(ImageDurationKey, PlaybackSettings.DefaultImageDurationSeconds),
            GifRepeatCount = Preferences.Default.Get(GifRepeatCountKey, PlaybackSettings.DefaultGifRepeatCount),
            Shuffle = Preferences.Default.Get(ShuffleKey, false),
            LoopMode = Enum.TryParse<LoopMode>(Preferences.Default.Get(LoopModeKey, LoopMode.All.ToString()), out var loopMode)
                    && Enum.IsDefined(loopMode)
                ? loopMode
                : LoopMode.All,
            RecursiveFolderScan = Preferences.Default.Get(RecursiveFolderScanKey, true),
            Volume = Preferences.Default.Get(VolumeKey, 1.0),
            Muted = Preferences.Default.Get(MutedKey, false),
            SplitScreenMode = Enum.TryParse<SplitScreenMode>(Preferences.Default.Get(SplitScreenModeKey, SplitScreenMode.Single.ToString()), out var splitScreenMode)
                ? splitScreenMode
                : SplitScreenMode.Single,
            AlwaysShowVideoInSplit = Preferences.Default.Get(AlwaysShowVideoInSplitKey, false),
            IsolateVideoToCenterPanel = Preferences.Default.Get(IsolateVideoToCenterPanelKey, false)
        };

        settings.PlaybackSpeed = Math.Clamp(settings.PlaybackSpeed, PlaybackSettings.MinPlaybackSpeed, PlaybackSettings.MaxPlaybackSpeed);
        settings.ImageDurationSeconds = Math.Clamp(settings.ImageDurationSeconds, PlaybackSettings.MinImageDurationSeconds, PlaybackSettings.MaxImageDurationSeconds);
        settings.GifRepeatCount = Math.Clamp(settings.GifRepeatCount, PlaybackSettings.MinGifRepeatCount, PlaybackSettings.MaxGifRepeatCount);
        settings.Volume = Math.Clamp(settings.Volume, 0, 1);
        settings.LoopMode = Enum.IsDefined(settings.LoopMode) ? settings.LoopMode : LoopMode.All;
        settings.SplitScreenMode = Enum.IsDefined(settings.SplitScreenMode) ? settings.SplitScreenMode : SplitScreenMode.Single;

        return Task.FromResult(settings);
    }

    public Task SaveAsync(PlaybackSettings settings)
    {
        Preferences.Default.Set(PlaybackSpeedKey, Math.Clamp(settings.PlaybackSpeed, PlaybackSettings.MinPlaybackSpeed, PlaybackSettings.MaxPlaybackSpeed));
        Preferences.Default.Set(ImageDurationKey, Math.Clamp(settings.ImageDurationSeconds, PlaybackSettings.MinImageDurationSeconds, PlaybackSettings.MaxImageDurationSeconds));
        Preferences.Default.Set(GifRepeatCountKey, Math.Clamp(settings.GifRepeatCount, PlaybackSettings.MinGifRepeatCount, PlaybackSettings.MaxGifRepeatCount));
        Preferences.Default.Set(ShuffleKey, settings.Shuffle);
        Preferences.Default.Set(LoopModeKey, (Enum.IsDefined(settings.LoopMode) ? settings.LoopMode : LoopMode.All).ToString());
        Preferences.Default.Set(RecursiveFolderScanKey, settings.RecursiveFolderScan);
        Preferences.Default.Set(VolumeKey, Math.Clamp(settings.Volume, 0, 1));
        Preferences.Default.Set(MutedKey, settings.Muted);
        Preferences.Default.Set(SplitScreenModeKey, (Enum.IsDefined(settings.SplitScreenMode) ? settings.SplitScreenMode : SplitScreenMode.Single).ToString());
        Preferences.Default.Set(AlwaysShowVideoInSplitKey, settings.AlwaysShowVideoInSplit);
        Preferences.Default.Set(IsolateVideoToCenterPanelKey, settings.IsolateVideoToCenterPanel);

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> GetRecentPathsAsync()
    {
        return GetPathListAsync(RecentPathsKey);
    }

    public Task<IReadOnlyList<string>> GetLastMediaPathsAsync()
    {
        return GetPathListAsync(LastMediaPathsKey);
    }

    public Task SaveLastMediaPathsAsync(IEnumerable<string> paths)
    {
        Preferences.Default.Set(LastMediaPathsKey, SerializePaths(paths));
        return Task.CompletedTask;
    }

    public async Task AddRecentPathsAsync(IEnumerable<string> paths)
    {
        var recentPaths = (await GetRecentPathsAsync()).ToList();

        foreach (var path in paths.Where(static path => !string.IsNullOrWhiteSpace(path)))
        {
            recentPaths.RemoveAll(existing => string.Equals(existing, path, StringComparison.OrdinalIgnoreCase));
            recentPaths.Insert(0, path);
        }

        Preferences.Default.Set(RecentPathsKey, SerializePaths(recentPaths));
    }

    private static Task<IReadOnlyList<string>> GetPathListAsync(string key)
    {
        var json = Preferences.Default.Get(key, string.Empty);

        if (string.IsNullOrWhiteSpace(json))
        {
            return Task.FromResult<IReadOnlyList<string>>([]);
        }

        try
        {
            var paths = JsonSerializer.Deserialize<List<string>>(json) ?? [];
            return Task.FromResult<IReadOnlyList<string>>(paths);
        }
        catch (JsonException)
        {
            return Task.FromResult<IReadOnlyList<string>>([]);
        }
    }

    private static string SerializePaths(IEnumerable<string> paths)
    {
        return JsonSerializer.Serialize(paths
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Where(static path => !IsTransientCachePath(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(10));
    }

    private static bool IsTransientCachePath(string path)
    {
        try
        {
            var cacheDirectory = Path.GetFullPath(FileSystem.CacheDirectory);
            var fullPath = Path.GetFullPath(path);
            return fullPath.StartsWith(cacheDirectory, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return true;
        }
    }
}
