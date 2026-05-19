using MauiMediaPlayer.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace MauiMediaPlayer.Services;

public sealed class MediaSourceService(DebugLogService debugLog) : IDisposable
{
    private static readonly TimeSpan DefaultGifFrameDelay = TimeSpan.FromMilliseconds(100);

    // Image data URI LRU cache. Sized by total byte footprint so a handful of huge
    // images don't starve a longer playlist of small ones. Hot path is GetImageDataUriAsync
    // which becomes synchronous-ish on cache hit (still returns Task because the public
    // contract is async). PrefetchImageAsync warms the cache speculatively from
    // background callers (peek-next-item-and-prefetch in Phase 3).
    private const long MaxCacheByteBudget = 64L * 1024 * 1024;
    private const long MaxIndividualEntryBytes = 20L * 1024 * 1024;
    private const int MaxPrefetchConcurrency = 2;
    private const long ThumbnailGenerationThresholdBytes = 1536L * 1024;
    private const int ThumbnailMaxEdgePixels = 1080;

    private readonly Dictionary<Guid, LinkedListNode<CacheEntry>> _cacheMap = [];
    private readonly Dictionary<Guid, LinkedListNode<CacheEntry>> _thumbnailCacheMap = [];
    private readonly LinkedList<CacheEntry> _thumbnailLruOrder = new();
    private readonly LinkedList<CacheEntry> _lruOrder = new();
    private readonly object _cacheLock = new();
    private readonly Dictionary<Guid, Task<string?>> _inFlightLoads = [];
    private readonly SemaphoreSlim _prefetchGate = new(MaxPrefetchConcurrency, MaxPrefetchConcurrency);
    private long _cachedBytes;
    private long _thumbnailCachedBytes;
    private bool _disposed;

    public async Task<string> GetThumbnailDataUriAsync(MediaItem item, CancellationToken cancellationToken = default)
    {
        if (item.Kind is not (MediaKind.Image or MediaKind.Gif))
        {
            return await GetImageDataUriAsync(item, cancellationToken);
        }

        if (TryGetCachedThumbnail(item.Id, out var cachedThumb))
        {
            return cachedThumb;
        }

        if (item.SizeBytes <= ThumbnailGenerationThresholdBytes)
        {
            return await GetImageDataUriAsync(item, cancellationToken);
        }

        var thumbUri = await LoadThumbnailDataUriAsync(item, cancellationToken);
        return thumbUri ?? await GetImageDataUriAsync(item, cancellationToken);
    }

    public async Task PrefetchThumbnailAsync(MediaItem item, CancellationToken cancellationToken = default)
    {
        if (item.Kind is not (MediaKind.Image or MediaKind.Gif))
        {
            return;
        }

        if (TryGetCachedThumbnail(item.Id, out _))
        {
            return;
        }

        try
        {
            await _prefetchGate.WaitAsync(cancellationToken);
            _ = await GetThumbnailDataUriAsync(item, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            debugLog.Trace($"Thumbnail prefetch failed for {item.DisplayName}: {exception.Message}");
        }
        finally
        {
            _prefetchGate.Release();
        }
    }

    public async Task<string> GetImageDataUriAsync(MediaItem item, CancellationToken cancellationToken = default)
    {
        if (TryGetCachedDataUri(item.Id, out var cached))
        {
            debugLog.Trace($"Image cache hit: {item.DisplayName} ({item.SizeBytes:N0} bytes); cachedBytes={_cachedBytes:N0}.");
            return cached;
        }

        // Do not use file:// URIs for <img> in the Blazor WebView — the host page uses a
        // virtual https origin, so local file URLs fail to paint even though we can cache them.
        // (IImageDisplayUriProvider remains available for a future custom-scheme path.)

        if (item.SizeBytes > MaxIndividualEntryBytes)
        {
            debugLog.Debug($"Oversized image using thumbnail for display: {item.DisplayName}; size={item.SizeBytes:N0}; ceiling={MaxIndividualEntryBytes:N0}.");
            return await GetThumbnailDataUriAsync(item, cancellationToken);
        }

        var dataUri = await LoadOrJoinInFlightAsync(item, cancellationToken);

        if (dataUri is null)
        {
            // Shouldn't happen on the GetImageDataUriAsync path (any load failure throws),
            // but the in-flight join can race with a cancellation that bypasses the cache.
            // Fall back to a direct read so the caller still gets a usable URI.
            return await LoadImageDataUriAsync(item, addToCache: false, cancellationToken);
        }

        return dataUri;
    }

    // Speculative warm: returns Task that completes when the image is cached (or fails
    // silently). Safe to fire-and-forget. Bounded by a semaphore so prefetch doesn't
    // contend with the user's active read on a slow disk.
    public async Task PrefetchImageAsync(MediaItem item, CancellationToken cancellationToken = default)
    {
        if (item.Kind is not (MediaKind.Image or MediaKind.Gif))
        {
            return;
        }

        if (TryGetCachedDataUri(item.Id, out _))
        {
            return;
        }

        if (item.SizeBytes > MaxIndividualEntryBytes)
        {
            debugLog.Trace($"Image prefetch skipped (over per-entry ceiling): {item.DisplayName}; size={item.SizeBytes:N0}; ceiling={MaxIndividualEntryBytes:N0}.");
            return;
        }

        try
        {
            await _prefetchGate.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            if (TryGetCachedDataUri(item.Id, out _))
            {
                return;
            }

            debugLog.Trace($"Image prefetch start: {item.DisplayName}; size={item.SizeBytes:N0}; cachedBytes={_cachedBytes:N0}.");
            await LoadOrJoinInFlightAsync(item, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            debugLog.Trace($"Image prefetch cancelled: {item.DisplayName}.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            debugLog.Trace($"Image prefetch failed for {item.DisplayName}: {exception.Message}");
        }
        finally
        {
            _prefetchGate.Release();
        }
    }

    public void InvalidateItem(Guid itemId)
    {
        lock (_cacheLock)
        {
            if (_cacheMap.TryGetValue(itemId, out var node))
            {
                _cachedBytes -= node.Value.Bytes;
                _lruOrder.Remove(node);
                _cacheMap.Remove(itemId);
                debugLog.Trace($"Image cache evict (invalidate): id={itemId}; cachedBytes={_cachedBytes:N0}.");
            }

            if (_thumbnailCacheMap.TryGetValue(itemId, out var thumbNode))
            {
                _thumbnailCachedBytes -= thumbNode.Value.Bytes;
                _thumbnailLruOrder.Remove(thumbNode);
                _thumbnailCacheMap.Remove(itemId);
            }
        }
    }

    // Drop cached entries that are no longer represented by any of the given playlist
    // ids. Called on playlist mutations so the cache doesn't permanently retain bytes
    // for removed items.
    public void RetainOnly(IReadOnlySet<Guid> liveItemIds)
    {
        if (liveItemIds is null)
        {
            return;
        }

        lock (_cacheLock)
        {
            if (_cacheMap.Count == 0 && _thumbnailCacheMap.Count == 0)
            {
                return;
            }

            var evicted = 0;

            foreach (var key in _cacheMap.Keys.ToArray())
            {
                if (liveItemIds.Contains(key))
                {
                    continue;
                }

                if (_cacheMap.TryGetValue(key, out var node))
                {
                    _cachedBytes -= node.Value.Bytes;
                    _lruOrder.Remove(node);
                    _cacheMap.Remove(key);
                    evicted++;
                }
            }

            foreach (var key in _thumbnailCacheMap.Keys.ToArray())
            {
                if (liveItemIds.Contains(key))
                {
                    continue;
                }

                if (_thumbnailCacheMap.TryGetValue(key, out var thumbNode))
                {
                    _thumbnailCachedBytes -= thumbNode.Value.Bytes;
                    _thumbnailLruOrder.Remove(thumbNode);
                    _thumbnailCacheMap.Remove(key);
                    evicted++;
                }
            }

            if (evicted > 0)
            {
                debugLog.Debug($"Image cache pruned to live playlist: evicted={evicted}; cachedBytes={_cachedBytes:N0}; thumbCachedBytes={_thumbnailCachedBytes:N0}.");
            }
        }
    }

    public void ClearCache()
    {
        lock (_cacheLock)
        {
            if (_cacheMap.Count == 0)
            {
                return;
            }

            debugLog.Debug($"Image cache cleared: entries={_cacheMap.Count}; cachedBytes={_cachedBytes:N0}.");
            _cacheMap.Clear();
            _lruOrder.Clear();
            _cachedBytes = 0;
        }
    }

    /// <summary>
    /// Returns a cached full-size or thumbnail data URI when available (synchronous).
    /// Used to paint image panes on the first frame after a swap without a loading state.
    /// </summary>
    public bool TryGetCachedDisplayUri(Guid itemId, out string dataUri)
    {
        if (TryGetCachedDataUri(itemId, out dataUri))
        {
            return true;
        }

        return TryGetCachedThumbnail(itemId, out dataUri);
    }

    private bool TryGetCachedDataUri(Guid itemId, out string dataUri)
    {
        lock (_cacheLock)
        {
            if (_cacheMap.TryGetValue(itemId, out var node))
            {
                _lruOrder.Remove(node);
                _lruOrder.AddFirst(node);
                dataUri = node.Value.DataUri;
                return true;
            }
        }

        dataUri = string.Empty;
        return false;
    }

    private bool TryGetCachedThumbnail(Guid itemId, out string dataUri)
    {
        lock (_cacheLock)
        {
            if (_thumbnailCacheMap.TryGetValue(itemId, out var node))
            {
                _thumbnailLruOrder.Remove(node);
                _thumbnailLruOrder.AddFirst(node);
                dataUri = node.Value.DataUri;
                return true;
            }
        }

        dataUri = string.Empty;
        return false;
    }

    private async Task<string?> LoadThumbnailDataUriAsync(MediaItem item, CancellationToken cancellationToken)
    {
        try
        {
            var dataUri = await Task.Run(() => GenerateThumbnailDataUri(item), cancellationToken);
            var memoryFootprintBytes = (long)dataUri.Length * sizeof(char);
            AddToThumbnailCache(item.Id, dataUri, memoryFootprintBytes);
            debugLog.Debug($"Thumbnail created for {item.DisplayName}; memory={memoryFootprintBytes:N0}.");
            return dataUri;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            debugLog.Trace($"Thumbnail generation failed for {item.DisplayName}: {exception.Message}");
            return null;
        }
    }

    private static string GenerateThumbnailDataUri(MediaItem item)
    {
        using var stream = OpenSharedReadStream(item.FilePath);
        using var image = SixLabors.ImageSharp.Image.Load(stream);
        image.Mutate(ctx => ctx.AutoOrient());

        var maxEdge = Math.Max(image.Width, image.Height);

        if (maxEdge > ThumbnailMaxEdgePixels)
        {
            var scale = ThumbnailMaxEdgePixels / (double)maxEdge;
            var width = Math.Max(1, (int)Math.Round(image.Width * scale));
            var height = Math.Max(1, (int)Math.Round(image.Height * scale));
            image.Mutate(ctx => ctx.Resize(width, height));
        }

        using var output = new MemoryStream();
        image.Save(output, new JpegEncoder { Quality = 82 });
        var base64 = Convert.ToBase64String(output.ToArray());
        return $"data:image/jpeg;base64,{base64}";
    }

    private void AddToThumbnailCache(Guid itemId, string dataUri, long byteWeight)
    {
        lock (_cacheLock)
        {
            if (_thumbnailCacheMap.TryGetValue(itemId, out var existing))
            {
                _thumbnailCachedBytes -= existing.Value.Bytes;
                _thumbnailLruOrder.Remove(existing);
                _thumbnailCacheMap.Remove(itemId);
            }

            var node = new LinkedListNode<CacheEntry>(new CacheEntry(itemId, dataUri, byteWeight));
            _thumbnailLruOrder.AddFirst(node);
            _thumbnailCacheMap[itemId] = node;
            _thumbnailCachedBytes += byteWeight;

            while (_thumbnailCachedBytes > MaxCacheByteBudget / 4 && _thumbnailLruOrder.Last is { } tail)
            {
                _thumbnailCachedBytes -= tail.Value.Bytes;
                _thumbnailCacheMap.Remove(tail.Value.ItemId);
                _thumbnailLruOrder.RemoveLast();
            }
        }
    }

    private Task<string?> LoadOrJoinInFlightAsync(MediaItem item, CancellationToken cancellationToken)
    {
        // Coalesce concurrent requests for the same item (e.g. EnsureImageSourcesAsync and
        // a Phase 3 prefetch firing at the same time). Whichever caller arrives first
        // performs the IO; subsequent callers await the same Task and share the result.
        Task<string?> task;
        var startedNew = false;

        lock (_cacheLock)
        {
            if (_inFlightLoads.TryGetValue(item.Id, out var existing))
            {
                task = existing;
            }
            else
            {
                task = LoadImageDataUriAndCacheAsync(item, cancellationToken);
                _inFlightLoads[item.Id] = task;
                startedNew = true;
            }
        }

        if (startedNew)
        {
            _ = task.ContinueWith(_ =>
            {
                lock (_cacheLock)
                {
                    _inFlightLoads.Remove(item.Id);
                }
            }, TaskScheduler.Default);
        }

        return task;
    }

    private async Task<string?> LoadImageDataUriAndCacheAsync(MediaItem item, CancellationToken cancellationToken)
    {
        try
        {
            var dataUri = await LoadImageDataUriAsync(item, addToCache: true, cancellationToken);
            return dataUri;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            debugLog.Error($"Image load failed for {item.DisplayName}: {exception.Message}");
            throw;
        }
    }

    private async Task<string> LoadImageDataUriAsync(MediaItem item, bool addToCache, CancellationToken cancellationToken)
    {
        debugLog.Trace($"Reading {item.Kind} bytes for display: {item.DisplayName} ({item.SizeBytes:N0} bytes).");

        var bytes = await ReadSharedFileBytesAsync(item.FilePath, cancellationToken);
        var mimeType = GetImageMimeType(item.Extension);
        var base64 = Convert.ToBase64String(bytes);
        var dataUri = $"data:{mimeType};base64,{base64}";

        // UTF-16 memory footprint of the string: each char is 2 bytes. Using char count
        // directly would underestimate cache pressure by 2x and lead to higher peak RAM
        // than the configured MaxCacheByteBudget implies.
        var memoryFootprintBytes = (long)dataUri.Length * sizeof(char);

        if (addToCache && bytes.Length <= MaxIndividualEntryBytes)
        {
            AddToCache(item.Id, dataUri, memoryFootprintBytes);
        }

        debugLog.Debug($"Created data URI for {item.DisplayName}; mime={mimeType}; bytes={bytes.Length:N0}; memory={memoryFootprintBytes:N0}; cached={addToCache && bytes.Length <= MaxIndividualEntryBytes}.");
        return dataUri;
    }

    private void AddToCache(Guid itemId, string dataUri, long byteWeight)
    {
        lock (_cacheLock)
        {
            if (_cacheMap.TryGetValue(itemId, out var existing))
            {
                _cachedBytes -= existing.Value.Bytes;
                _lruOrder.Remove(existing);
                _cacheMap.Remove(itemId);
            }

            var node = new LinkedListNode<CacheEntry>(new CacheEntry(itemId, dataUri, byteWeight));
            _lruOrder.AddFirst(node);
            _cacheMap[itemId] = node;
            _cachedBytes += byteWeight;

            while (_cachedBytes > MaxCacheByteBudget && _lruOrder.Last is { } tail)
            {
                _cachedBytes -= tail.Value.Bytes;
                _cacheMap.Remove(tail.Value.ItemId);
                _lruOrder.RemoveLast();
                debugLog.Trace($"Image cache evict (budget): id={tail.Value.ItemId}; cachedBytes={_cachedBytes:N0}; budget={MaxCacheByteBudget:N0}.");
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _prefetchGate.Dispose();
    }

    private sealed record CacheEntry(Guid ItemId, string DataUri, long Bytes);

    public async Task<TimeSpan?> GetGifAnimationDurationAsync(MediaItem item, CancellationToken cancellationToken = default)
    {
        if (item.Kind != MediaKind.Gif)
        {
            return null;
        }

        try
        {
            var duration = await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var stream = OpenSharedReadStream(item.FilePath);
                return GetGifAnimationDuration(stream);
            }, cancellationToken);

            if (duration is { TotalMilliseconds: > 0 })
            {
                debugLog.Debug($"GIF duration detected: {item.DisplayName}; duration={duration.Value.TotalSeconds:0.###}s.");
            }
            else
            {
                debugLog.Trace($"GIF duration unavailable: {item.DisplayName}.");
            }

            return duration;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or EndOfStreamException)
        {
            debugLog.Error($"Could not read GIF duration for {item.DisplayName}: {exception.Message}");
            return null;
        }
    }

    private static async Task<byte[]> ReadSharedFileBytesAsync(string filePath, CancellationToken cancellationToken)
    {
        await using var stream = OpenSharedReadStream(filePath);
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, cancellationToken);
        return memory.ToArray();
    }

    private static FileStream OpenSharedReadStream(string filePath)
    {
        return new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    private static TimeSpan? GetGifAnimationDuration(Stream stream)
    {
        Span<byte> header = stackalloc byte[13];
        ReadExactly(stream, header);

        if (!header[..6].SequenceEqual("GIF87a"u8) && !header[..6].SequenceEqual("GIF89a"u8))
        {
            return null;
        }

        if ((header[10] & 0b1000_0000) != 0)
        {
            var globalColorTableSize = 3 * (1 << ((header[10] & 0b0000_0111) + 1));
            SkipBytes(stream, globalColorTableSize);
        }

        var totalDelay = TimeSpan.Zero;
        var pendingFrameDelay = DefaultGifFrameDelay;
        var frameCount = 0;

        while (true)
        {
            var blockType = stream.ReadByte();

            if (blockType == -1 || blockType == 0x3B)
            {
                break;
            }

            switch (blockType)
            {
                case 0x21:
                    pendingFrameDelay = ReadExtensionBlock(stream) ?? pendingFrameDelay;
                    break;
                case 0x2C:
                    ReadImageDescriptor(stream);
                    totalDelay += pendingFrameDelay;
                    pendingFrameDelay = DefaultGifFrameDelay;
                    frameCount++;
                    break;
                default:
                    return frameCount > 0 ? totalDelay : null;
            }
        }

        return frameCount > 1 ? totalDelay : null;
    }

    private static TimeSpan? ReadExtensionBlock(Stream stream)
    {
        var label = stream.ReadByte();

        if (label == 0xF9)
        {
            Span<byte> graphicControl = stackalloc byte[6];
            ReadExactly(stream, graphicControl);

            var blockSize = graphicControl[0];
            var delayHundredths = graphicControl[2] | (graphicControl[3] << 8);

            if (blockSize != 4)
            {
                SkipSubBlocks(stream);
                return null;
            }

            return delayHundredths > 0
                ? TimeSpan.FromMilliseconds(delayHundredths * 10)
                : DefaultGifFrameDelay;
        }

        SkipSubBlocks(stream);
        return null;
    }

    private static void ReadImageDescriptor(Stream stream)
    {
        Span<byte> descriptor = stackalloc byte[9];
        ReadExactly(stream, descriptor);

        if ((descriptor[8] & 0b1000_0000) != 0)
        {
            var localColorTableSize = 3 * (1 << ((descriptor[8] & 0b0000_0111) + 1));
            SkipBytes(stream, localColorTableSize);
        }

        SkipBytes(stream, 1);
        SkipSubBlocks(stream);
    }

    private static void SkipSubBlocks(Stream stream)
    {
        while (true)
        {
            var blockSize = stream.ReadByte();

            if (blockSize <= 0)
            {
                return;
            }

            SkipBytes(stream, blockSize);
        }
    }

    private static void ReadExactly(Stream stream, Span<byte> buffer)
    {
        var totalRead = 0;

        while (totalRead < buffer.Length)
        {
            var read = stream.Read(buffer[totalRead..]);

            if (read == 0)
            {
                throw new EndOfStreamException();
            }

            totalRead += read;
        }
    }

    private static void SkipBytes(Stream stream, int count)
    {
        if (count <= 0)
        {
            return;
        }

        if (stream.CanSeek)
        {
            stream.Seek(count, SeekOrigin.Current);
            return;
        }

        Span<byte> buffer = stackalloc byte[Math.Min(1024, count)];
        var remaining = count;

        while (remaining > 0)
        {
            var read = stream.Read(buffer[..Math.Min(buffer.Length, remaining)]);

            if (read == 0)
            {
                throw new EndOfStreamException();
            }

            remaining -= read;
        }
    }

    private static string GetImageMimeType(string extension)
    {
        return extension.ToLowerInvariant() switch
        {
            ".bmp" => "image/bmp",
            ".gif" => "image/gif",
            ".heic" => "image/heic",
            ".heif" => "image/heif",
            ".jfif" => "image/jpeg",
            ".jpeg" => "image/jpeg",
            ".jpg" => "image/jpeg",
            ".png" => "image/png",
            ".svg" => "image/svg+xml",
            ".tif" => "image/tiff",
            ".tiff" => "image/tiff",
            ".webp" => "image/webp",
            _ => "application/octet-stream"
        };
    }
}
