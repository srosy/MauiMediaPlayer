using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace MauiMediaPlayer.Services;

public sealed class VideoMetadataService(DebugLogService debugLog)
{
    private const string CacheFileName = "video-metadata-cache.json";
    private readonly ConcurrentDictionary<string, TimeSpan> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _persistGate = new(1, 1);
    private bool _cacheLoaded;

    public bool TryGetDuration(string filePath, out TimeSpan duration)
    {
        EnsureCacheLoaded();
        return _cache.TryGetValue(filePath, out duration) && duration > TimeSpan.Zero;
    }

    public async Task<TimeSpan?> ProbeDurationAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        EnsureCacheLoaded();

        if (_cache.TryGetValue(filePath, out var cached) && cached > TimeSpan.Zero)
        {
            return cached;
        }

        try
        {
            var duration = await Task.Run(() => ProbeDurationCore(filePath), cancellationToken);

            if (duration is { TotalSeconds: > 0.5 })
            {
                _cache[filePath] = duration.Value;
                _ = PersistCacheAsync();
                debugLog.Trace($"Video metadata probed: {Path.GetFileName(filePath)}; duration={duration.Value.TotalSeconds:0.###}s.");
            }

            return duration;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            debugLog.Trace($"Video metadata probe failed for {Path.GetFileName(filePath)}: {exception.Message}");
            return null;
        }
    }

    private static TimeSpan? ProbeDurationCore(string filePath)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var file = TagLib.File.Create(filePath);
                var length = file.Properties.Duration;

                return length > TimeSpan.Zero ? length : null;
            }
            catch (IOException) when (attempt < 2)
            {
                Thread.Sleep(150 * (attempt + 1));
            }
        }

        return null;
    }

    private void EnsureCacheLoaded()
    {
        if (_cacheLoaded)
        {
            return;
        }

        _cacheLoaded = true;

        try
        {
            var path = GetCacheFilePath();

            if (!File.Exists(path))
            {
                return;
            }

            var json = File.ReadAllText(path);
            var entries = JsonSerializer.Deserialize<Dictionary<string, double>>(json);

            if (entries is null)
            {
                return;
            }

            foreach (var (key, seconds) in entries)
            {
                if (seconds > 0.5)
                {
                    _cache[key] = TimeSpan.FromSeconds(seconds);
                }
            }
        }
        catch (Exception exception)
        {
            debugLog.Trace($"Video metadata cache load skipped: {exception.Message}");
        }
    }

    private async Task PersistCacheAsync()
    {
        try
        {
            await _persistGate.WaitAsync();
            var directory = Path.GetDirectoryName(GetCacheFilePath());

            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var payload = _cache.ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value.TotalSeconds,
                StringComparer.OrdinalIgnoreCase);

            var json = JsonSerializer.Serialize(payload);
            await File.WriteAllTextAsync(GetCacheFilePath(), json);
        }
        catch (Exception exception)
        {
            debugLog.Trace($"Video metadata cache persist failed: {exception.Message}");
        }
        finally
        {
            _persistGate.Release();
        }
    }

    private static string GetCacheFilePath()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Mosaic",
            "cache");

        return Path.Combine(root, CacheFileName);
    }
}
