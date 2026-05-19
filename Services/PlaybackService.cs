using MauiMediaPlayer.Models;

namespace MauiMediaPlayer.Services;

public sealed class PlaybackService(
    PlaylistService playlistService,
    SettingsService settingsService,
    FavoritesService favoritesService,
    DebugLogService debugLog,
    NativeVideoPlaybackService nativeVideoPlayback,
    MediaSourceService mediaSourceService,
    ToastService toasts) : IAsyncDisposable
{
    private const int MaxSlots = 3;
    // Stagger multi-split image timer firings so three panes don't refresh in one frame.
    private static readonly int[] MultiSplitTimerStaggerMs = [0, 75, 150];
    private readonly CancellationTokenSource?[] _slotTimers = new CancellationTokenSource?[MaxSlots];
    private readonly Guid?[] _slotTimerItemIds = new Guid?[MaxSlots];
    private readonly int[] _slotIndices = Enumerable.Repeat(-1, MaxSlots).ToArray();
    private readonly int[] _slotTimerGenerations = new int[MaxSlots];
    private readonly int?[] _slotNativeGenerations = new int?[MaxSlots];
    // Rate-limits the "constraint unsatisfiable" trace per slot so a degenerate playlist
    // (e.g., AlwaysShowVideo on with zero videos) doesn't flood the log on every advance.
    // Records the slot's current index at the time of the last warning; we only re-warn
    // when the slot's effective index changes.
    private readonly int[] _slotLastUnsatisfiableTraceIndex = Enumerable.Repeat(-1, MaxSlots).ToArray();
    // Per-slot shuffle bags used in split modes when Shuffle is on. Each slot maintains
    // an independent Fisher-Yates-shuffled queue of playlist indices so the two side
    // panes (and the primary slot) advance through their own randomized order instead
    // of stride-walking the playlist in lockstep. The bag is filtered by the slot's
    // current MediaKindRequirement at dequeue time and is regenerated lazily once it
    // empties (LoopMode.All) or stops the slot (LoopMode.Off). _slotShuffleQueueInitialized
    // separates "first fill" from "natural exhaustion" so LoopMode.Off can stop cleanly.
    private readonly Queue<int>?[] _slotShuffleQueues = new Queue<int>?[MaxSlots];
    private readonly bool[] _slotShuffleQueueInitialized = new bool[MaxSlots];
    // Guards the relationship between _slotBasePlaylistIndex, _slotIndices, and
    // playlistService.CurrentIndex so EnsureSlotIndices never observes a transient
    // inconsistent state while CompleteSlotAsync (background thread) is aligning slot 0
    // with the playlist. Uses Monitor (reentrant) so EnsureSlotIndices may invoke
    // ResetSlotIndicesFromCurrent while still holding the lock.
    private readonly object _slotStateLock = new();
    private bool _initialized;
    private bool _slotIndicesInitialized;
    private int _slotBasePlaylistIndex = -1;
    private Guid? _lastPlayedItemId;
    private bool _videoTakeoverHintShown;
    private bool _favoritesEmptyNotified;
    private bool _mediaKindsEmptyNotified;

    public event Action? Changed;

    public int FavoriteCountInPlaylist => favoritesService.CountInPlaylist(playlistService.Items.Select(static item => item.FilePath));

    public PlaybackSettings Settings { get; private set; } = new();

    public PlaybackStatus Status { get; private set; } = PlaybackStatus.Stopped;

    public TimeSpan VideoPosition { get; private set; }

    public TimeSpan VideoDuration { get; private set; }

    public MediaItem? CurrentItem => playlistService.CurrentItem;

    public IReadOnlyList<MediaItem> VisibleItems => GetVisibleItems();

    public IReadOnlyList<VisibleMediaSlot> VisiblePanes => GetVisiblePanes();

    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        Settings = await settingsService.LoadAsync();
        _initialized = true;
        nativeVideoPlayback.ProgressChanged += UpdateVideoProgress;
        nativeVideoPlayback.EndReached += OnNativeVideoEnded;
        nativeVideoPlayback.PlaybackStarted += OnNativeVideoStarted;
        // Phase 2 invalidation: catch playlist mutations (Remove of a non-current item,
        // for example) that don't flow through RefreshPlaylistAsync so the cache doesn't
        // hold bytes for items that left the playlist. Cheap O(cache-size) sweep.
        playlistService.Changed += PruneImageCacheToPlaylist;
        favoritesService.Changed += OnFavoritesChanged;
        SyncPlaylistShuffleCallbacks();
        LogActiveConfiguration("settings-loaded");
        NotifyChanged();
    }

    public async Task PlayAsync()
    {
        await InitializeAsync();

        if (!playlistService.HasItems)
        {
            debugLog.Debug("Play requested with an empty playlist.");
            Status = PlaybackStatus.Stopped;
            VideoPosition = TimeSpan.Zero;
            VideoDuration = TimeSpan.Zero;
            StopAllSlotTimers();
            NotifyChanged();
            return;
        }

        SnapPlaylistCurrentToEligible();

        if (GetEligiblePlaylistIndices(MediaKindRequirement.Any).Count == 0)
        {
            NotifyFavoritesOnlyEmptyIfNeeded();
            NotifyMediaKindsFilterEmptyIfNeeded();
            await StopAsync();
            return;
        }

        EnsureSlotIndices();
        var primaryItem = GetSlotItem(0);

        if (_lastPlayedItemId != primaryItem?.Id || Status == PlaybackStatus.Stopped)
        {
            VideoPosition = TimeSpan.Zero;
            VideoDuration = TimeSpan.Zero;
            _lastPlayedItemId = primaryItem?.Id;
        }

        Status = PlaybackStatus.Playing;
        debugLog.Info($"Playback started: {primaryItem?.DisplayName} ({primaryItem?.Kind}).");
        LogActiveConfiguration("playback-started");
        NotifyChanged();

        await PlayVisibleSlotsAsync(forceRestartTimers: true);
        nativeVideoPlayback.RequestLayoutBoundsResync();
        await nativeVideoPlayback.ReconcilePlaybackSurfacesAsync();
    }

    public async Task PauseAsync()
    {
        if (Status != PlaybackStatus.Playing)
        {
            return;
        }

        Status = PlaybackStatus.Paused;
        debugLog.Info($"Playback paused: {GetSlotItem(0)?.DisplayName}.");
        LogActiveConfiguration("playback-paused");
        StopAllSlotTimers();
        await nativeVideoPlayback.PauseAsync();
        NotifyChanged();
    }

    public async Task StopAsync()
    {
        Status = PlaybackStatus.Stopped;
        debugLog.Info("Playback stopped.");
        LogActiveConfiguration("playback-stopped");
        VideoPosition = TimeSpan.Zero;

        lock (_slotStateLock)
        {
            Array.Fill(_slotIndices, -1);
            Array.Fill(_slotNativeGenerations, null);
            Array.Fill(_slotLastUnsatisfiableTraceIndex, -1);
            ResetSlotShuffleBagsLocked();
            _slotIndicesInitialized = false;
            _slotBasePlaylistIndex = -1;
        }

        StopAllSlotTimers();
        await nativeVideoPlayback.StopAsync();
        NotifyChanged();
    }

    public async Task NextAsync()
    {
        StopAllSlotTimers();
        debugLog.Debug("Next requested.");

        if (playlistService.MoveNext(Settings.Shuffle, Settings.LoopMode))
        {
            ResetSlotIndicesFromCurrent();
            await PlayAsync();
            return;
        }

        NotifyFavoritesOnlyEmptyIfNeeded();
        NotifyMediaKindsFilterEmptyIfNeeded();
        await StopAsync();
    }

    public async Task PreviousAsync()
    {
        StopAllSlotTimers();
        debugLog.Debug("Previous requested.");

        if (playlistService.MovePrevious(Settings.Shuffle))
        {
            ResetSlotIndicesFromCurrent();
            await PlayAsync();
            return;
        }

        NotifyFavoritesOnlyEmptyIfNeeded();
        NotifyMediaKindsFilterEmptyIfNeeded();
    }

    public async Task RandomAsync()
    {
        StopAllSlotTimers();
        debugLog.Debug("Random requested.");

        var eligible = GetEligiblePlaylistIndices(MediaKindRequirement.Any);

        if (eligible.Count == 0)
        {
            NotifyFavoritesOnlyEmptyIfNeeded();
            NotifyMediaKindsFilterEmptyIfNeeded();
            return;
        }

        if (!playlistService.MoveRandomAmong(eligible))
        {
            return;
        }

        ResetSlotIndicesFromCurrent();
        await PlayAsync();
    }

    public async Task SelectAsync(MediaItem item)
    {
        StopAllSlotTimers();
        debugLog.Info($"Playlist item selected: {item.DisplayName} ({item.Kind}).");

        if (playlistService.SetCurrent(item))
        {
            // Respect the user's click on slot 0; sibling slots still satisfy their constraints.
            ResetSlotIndicesFromCurrentRespectingSelection();
            await PlayAsync();
        }
    }

    public Task CompleteCurrentAsync()
    {
        return CompleteSlotAsync(0);
    }

    // Manually advance a specific slot to its next item, regardless of LoopMode.One
    // (which would otherwise restart the current item on natural completion). Used
    // by the per-pane Skip button so the user can step a single slot forward without
    // resetting the others.
    public Task SkipSlotAsync(int slotIndex)
    {
        if (slotIndex < 0 || slotIndex >= MaxSlots)
        {
            return Task.CompletedTask;
        }

        if (Status != PlaybackStatus.Playing)
        {
            debugLog.Trace($"Skip ignored for slot {slotIndex + 1}; status={Status}.");
            return Task.CompletedTask;
        }

        if (!IsActiveSlot(slotIndex))
        {
            debugLog.Trace($"Skip ignored for slot {slotIndex + 1}; slot is inactive.");
            return Task.CompletedTask;
        }

        debugLog.Debug($"Skip requested for slot {slotIndex + 1}.");
        return CompleteSlotAsync(slotIndex, expectedPath: null, playbackGeneration: null, forceAdvance: true);
    }

    public async Task UpdateSettingsAsync(Action<PlaybackSettings> update)
    {
        var previousShuffle = Settings.Shuffle;
        var previousSplitCount = Settings.SplitScreenCount;
        var previousLoopMode = Settings.LoopMode;
        var previousAlwaysShowVideo = Settings.AlwaysShowVideoInSplit;
        var previousIsolateVideo = Settings.IsolateVideoToCenterPanel;
        var previousSideAdvanceMode = Settings.SideAdvanceMode;
        var previousSharedSideBag = Settings.SharedSideShuffleBag;
        var previousVolume = Settings.Volume;
        var previousMuted = Settings.Muted;
        var previousSpeed = Settings.PlaybackSpeed;
        var previousImageDuration = Settings.ImageDurationSeconds;
        var previousGifRepeats = Settings.GifRepeatCount;
        var previousPlaylistSource = Settings.PlaylistSource;
        var previousEnabledMediaKinds = Settings.EnabledMediaKinds;
        var previousBoostFavorites = Settings.BoostFavoritesInShuffle;
        var previousFavoriteWeight = Settings.FavoriteShuffleWeight;
        update(Settings);
        Settings.EnabledMediaKinds = PlaybackSettings.NormalizeEnabledMediaKinds(Settings.EnabledMediaKinds);
        Settings.PlaybackSpeed = Math.Clamp(Settings.PlaybackSpeed, PlaybackSettings.MinPlaybackSpeed, PlaybackSettings.MaxPlaybackSpeed);
        Settings.ImageDurationSeconds = Math.Clamp(Settings.ImageDurationSeconds, PlaybackSettings.MinImageDurationSeconds, PlaybackSettings.MaxImageDurationSeconds);
        Settings.GifRepeatCount = Math.Clamp(Settings.GifRepeatCount, PlaybackSettings.MinGifRepeatCount, PlaybackSettings.MaxGifRepeatCount);
        Settings.Volume = Math.Clamp(Settings.Volume, 0, 1);
        Settings.LoopMode = Enum.IsDefined(Settings.LoopMode) ? Settings.LoopMode : LoopMode.All;
        Settings.SplitScreenMode = Enum.IsDefined(Settings.SplitScreenMode) ? Settings.SplitScreenMode : SplitScreenMode.Single;
        Settings.PlaylistSource = Enum.IsDefined(Settings.PlaylistSource) ? Settings.PlaylistSource : PlaylistSource.AllLibrary;
        Settings.FavoriteShuffleWeight = Math.Clamp(
            Settings.FavoriteShuffleWeight,
            PlaybackSettings.MinFavoriteShuffleWeight,
            PlaybackSettings.MaxFavoriteShuffleWeight);

        await settingsService.SaveAsync(Settings);
        SyncPlaylistShuffleCallbacks();
        LogActiveConfiguration("settings-updated");

        if (Settings.Shuffle != previousShuffle)
        {
            playlistService.ResetShufflePlayback();
            ResetSlotIndicesFromCurrent();
            debugLog.Info(Settings.Shuffle ? "Shuffle enabled." : "Shuffle disabled.");
        }
        else if (Settings.PlaylistSource != previousPlaylistSource
            || Settings.EnabledMediaKinds != previousEnabledMediaKinds
            || Settings.BoostFavoritesInShuffle != previousBoostFavorites
            || Settings.FavoriteShuffleWeight != previousFavoriteWeight)
        {
            InvalidateShuffleState();
        }

        var playlistEligibilityChanged = Settings.PlaylistSource != previousPlaylistSource
            || Settings.EnabledMediaKinds != previousEnabledMediaKinds;

        if (playlistEligibilityChanged)
        {
            _favoritesEmptyNotified = false;
            _mediaKindsEmptyNotified = false;
            NotifyFavoritesOnlyEmptyIfNeeded();
            NotifyMediaKindsFilterEmptyIfNeeded();
            SnapPlaylistCurrentToEligible();
            ResetSlotIndicesFromCurrent();
        }

        if (Settings.SplitScreenCount != previousSplitCount || Settings.LoopMode != previousLoopMode)
        {
            ResetSlotIndicesFromCurrent();
        }

        if (Settings.AlwaysShowVideoInSplit != previousAlwaysShowVideo
            || Settings.IsolateVideoToCenterPanel != previousIsolateVideo)
        {
            ResetSlotIndicesFromCurrent();
            debugLog.Info($"Split video constraint changed. alwaysShowVideo={Settings.AlwaysShowVideoInSplit}; isolateCenter={Settings.IsolateVideoToCenterPanel}.");
        }

        if (Settings.SideAdvanceMode != previousSideAdvanceMode)
        {
            StopSlotTimer(0);
            StopSlotTimer(2);
            debugLog.Info($"Side advance mode changed: {Settings.SideAdvanceMode}.");
        }

        if (Settings.SharedSideShuffleBag != previousSharedSideBag)
        {
            lock (_slotStateLock)
            {
                ResetSlotShuffleBagsLocked();
            }

            debugLog.Info($"Shared side shuffle bag {(Settings.SharedSideShuffleBag ? "enabled" : "disabled")}.");
        }

        nativeVideoPlayback.ApplySettings(Settings);

        var requiresVisibleSlotRefresh = Settings.Shuffle != previousShuffle
            || Settings.SplitScreenCount != previousSplitCount
            || Settings.LoopMode != previousLoopMode
            || Settings.AlwaysShowVideoInSplit != previousAlwaysShowVideo
            || Settings.IsolateVideoToCenterPanel != previousIsolateVideo
            || Settings.SideAdvanceMode != previousSideAdvanceMode
            || Settings.SharedSideShuffleBag != previousSharedSideBag
            || Settings.PlaylistSource != previousPlaylistSource
            || Settings.EnabledMediaKinds != previousEnabledMediaKinds
            || Math.Abs(Settings.PlaybackSpeed - previousSpeed) > 0.001
            || Math.Abs(Settings.ImageDurationSeconds - previousImageDuration) > 0.001
            || Settings.GifRepeatCount != previousGifRepeats;

        var onlyVolumeOrMuteChanged = !requiresVisibleSlotRefresh
            && (Math.Abs(Settings.Volume - previousVolume) > 0.001 || Settings.Muted != previousMuted);

        if (Status == PlaybackStatus.Playing)
        {
            if (playlistEligibilityChanged && GetEligiblePlaylistIndices(MediaKindRequirement.Any).Count == 0)
            {
                await StopAsync();
            }
            else if (requiresVisibleSlotRefresh)
            {
                await PlayVisibleSlotsAsync(forceRestartTimers: true);
            }
            else if (!onlyVolumeOrMuteChanged)
            {
                await EnsureSlotTimersAsync(forceRestartTimers: true);
            }
        }

        NotifyChanged();
    }

    public void UpdateVideoProgress(double positionSeconds, double durationSeconds)
    {
        VideoPosition = TimeSpan.FromSeconds(Math.Max(0, positionSeconds));
        VideoDuration = TimeSpan.FromSeconds(Math.Max(0, durationSeconds));
        NotifyChanged();
    }

    public async Task SeekAsync(double seconds)
    {
        var maxSeconds = VideoDuration.TotalSeconds > 0 ? VideoDuration.TotalSeconds : Math.Max(0, seconds);
        var safeSeconds = Math.Clamp(seconds, 0, maxSeconds);
        VideoPosition = TimeSpan.FromSeconds(safeSeconds);
        debugLog.Trace($"Seek requested: {safeSeconds:0.##}s.");

        if (GetSlotItem(0)?.Kind == MediaKind.Video)
        {
            await nativeVideoPlayback.SeekAsync(safeSeconds);
        }

        NotifyChanged();
    }

    public async Task RefreshPlaylistAsync()
    {
        ResetSlotIndicesFromCurrent();
        PruneImageCacheToPlaylist();

        if (Status == PlaybackStatus.Playing)
        {
            debugLog.Debug("Refreshing playback after playlist order/content change.");
            await PlayVisibleSlotsAsync(forceRestartTimers: true);
        }

        NotifyChanged();
    }

    private void PruneImageCacheToPlaylist()
    {
        // Free cache bytes for items that are no longer in the playlist (sort/shuffle
        // preserve ids so this is a no-op for ordering changes; it only evicts on
        // genuine removals or playlist replacement).
        var liveIds = new HashSet<Guid>();

        foreach (var item in playlistService.Items)
        {
            liveIds.Add(item.Id);
        }

        mediaSourceService.RetainOnly(liveIds);
    }

    private void OnNativeVideoEnded(int slotIndex, string? completedPath, int playbackGeneration, string reason)
    {
        if (reason is "native-failed" or "play-request-failed" or "startup-timeout")
        {
            var label = string.IsNullOrWhiteSpace(completedPath)
                ? "This video"
                : Path.GetFileName(completedPath);
            toasts.Error($"Could not play {label}. Skipping.");
        }

        _ = CompleteSlotAsync(slotIndex, completedPath, playbackGeneration);
    }

    private void OnNativeVideoStarted(int slotIndex, string path, int playbackGeneration)
    {
        if (!IsActiveSlot(slotIndex))
        {
            return;
        }

        var item = GetSlotItem(slotIndex);

        if (item is not null && string.Equals(item.FilePath, path, StringComparison.OrdinalIgnoreCase))
        {
            _slotNativeGenerations[slotIndex] = playbackGeneration;
            debugLog.Trace($"Tracked native video generation: slot={slotIndex + 1}; generation={playbackGeneration}; item={item.DisplayName}.");
        }
    }

    private Task CompleteSlotAsync(int slotIndex)
    {
        return CompleteSlotAsync(slotIndex, expectedPath: null, playbackGeneration: null);
    }

    private async Task CompleteSlotAsync(int slotIndex, string? expectedPath, int? playbackGeneration, bool forceAdvance = false)
    {
        if (Status != PlaybackStatus.Playing || !IsActiveSlot(slotIndex))
        {
            return;
        }

        var item = GetSlotItem(slotIndex);

        if (item is null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(expectedPath) && !string.Equals(item.FilePath, expectedPath, StringComparison.OrdinalIgnoreCase))
        {
            debugLog.Trace($"Ignored stale slot {slotIndex + 1} completion: generation={playbackGeneration}; completed={Path.GetFileName(expectedPath)}; current={item.DisplayName}.");
            return;
        }

        if (playbackGeneration is not null
            && _slotNativeGenerations[slotIndex] is not null
            && _slotNativeGenerations[slotIndex] != playbackGeneration)
        {
            debugLog.Trace($"Ignored stale slot {slotIndex + 1} native generation: completed={playbackGeneration}; current={_slotNativeGenerations[slotIndex]}.");
            return;
        }

        if (playbackGeneration is not null && _slotNativeGenerations[slotIndex] is null)
        {
            debugLog.Trace($"Accepting slot {slotIndex + 1} completion with missing native generation because path still matches: completed={playbackGeneration}; item={item.DisplayName}.");
        }

        if (!forceAdvance && Settings.LoopMode == LoopMode.One)
        {
            debugLog.Debug($"Slot {slotIndex + 1} completed; loop-one restarting {item.DisplayName}.");

            if (slotIndex == 0)
            {
                VideoPosition = TimeSpan.Zero;
                VideoDuration = TimeSpan.Zero;
            }

            await PlaySingleSlotAsync(slotIndex, forceRestartTimer: true, restartVideoIfSameSource: true);
            return;
        }

        if (Settings.Shuffle && Settings.SplitScreenCount == 1)
        {
            debugLog.Debug($"Slot {slotIndex + 1} completed; shuffle advancing {item.DisplayName}.");

            if (GetEligiblePlaylistIndices(MediaKindRequirement.Any).Count == 0)
            {
                NotifyFavoritesOnlyEmptyIfNeeded();
                NotifyMediaKindsFilterEmptyIfNeeded();
                await StopAsync();
                return;
            }

            if (!playlistService.MoveNext(true, Settings.LoopMode))
            {
                await StopAsync();
                return;
            }

            ResetSlotIndicesFromCurrent();
            var shuffledItem = GetSlotItem(0);

            if (shuffledItem is not null)
            {
                _lastPlayedItemId = shuffledItem.Id;
                VideoPosition = TimeSpan.Zero;
                VideoDuration = TimeSpan.Zero;
            }

            await PlaySingleSlotAsync(slotIndex, forceRestartTimer: true, restartVideoIfSameSource: true);
            NotifyChanged();
            return;
        }

        var previousIndex = _slotIndices[slotIndex];
        debugLog.Debug($"Slot {slotIndex + 1} completed; advancing index={previousIndex}; item={item.DisplayName}.");

        if (!AdvanceSlotIndex(slotIndex))
        {
            debugLog.Debug($"Slot {slotIndex + 1} has no next item; stopping slot timer.");
            StopSlotTimer(slotIndex);
            _slotIndices[slotIndex] = -1;
            _slotNativeGenerations[slotIndex] = null;
            await nativeVideoPlayback.PlaySlotAsync(slotIndex, null, Settings);

            if (!HasVisibleSlotItems())
            {
                await StopAsync();
                return;
            }

            NotifyChanged();
            return;
        }

        var nextItem = GetSlotItem(slotIndex);
        debugLog.Info($"Slot {slotIndex + 1} advanced: {previousIndex} -> {_slotIndices[slotIndex]}; item={nextItem?.DisplayName} ({nextItem?.Kind}).");
        _slotNativeGenerations[slotIndex] = null;

        if (slotIndex == 1 && Settings.SideAdvanceMode == SideAdvanceMode.OnCentreChange)
        {
            await AdvanceCoupledSideSlotsAsync();
        }

        if (slotIndex == 0)
        {
            var primaryItem = GetSlotItem(0);

            if (primaryItem is not null)
            {
                // Update _slotBasePlaylistIndex and playlistService.CurrentIndex atomically so
                // EnsureSlotIndices (called from the UI thread during render) never sees a
                // transient mismatch and erroneously triggers ResetSlotIndicesFromCurrent.
                // Without this lock, the UI thread could observe base=newIndex while
                // CurrentIndex still holds the previous value (or vice versa), reset the
                // sibling slot indices, and silently strand their timers.
                lock (_slotStateLock)
                {
                    _slotBasePlaylistIndex = _slotIndices[0];
                    playlistService.SetCurrent(primaryItem);
                }

                _lastPlayedItemId = primaryItem.Id;
                VideoPosition = TimeSpan.Zero;
                VideoDuration = TimeSpan.Zero;
            }
        }

        await PlaySingleSlotAsync(slotIndex, forceRestartTimer: true, restartVideoIfSameSource: true);
        NotifyChanged();
    }

    private async Task AdvanceCoupledSideSlotsAsync()
    {
        if (Settings.SideAdvanceMode != SideAdvanceMode.OnCentreChange || Settings.SplitScreenCount < 3)
        {
            return;
        }

        foreach (var sideSlot in new[] { 0, 2 })
        {
            if (!IsActiveSlot(sideSlot))
            {
                continue;
            }

            var previousIndex = _slotIndices[sideSlot];
            if (!AdvanceSlotIndex(sideSlot))
            {
                StopSlotTimer(sideSlot);
                continue;
            }

            debugLog.Info($"Side slot {sideSlot + 1} coupled to centre change: {previousIndex} -> {_slotIndices[sideSlot]}; item={GetSlotItem(sideSlot)?.DisplayName}.");
            await PlaySingleSlotAsync(sideSlot, forceRestartTimer: true, restartVideoIfSameSource: true);
        }

        NotifyChanged();
    }

    private TimeSpan GetSlotTimerStagger(int slotIndex)
    {
        if (Settings.SplitScreenCount <= 1 || slotIndex < 0 || slotIndex >= MultiSplitTimerStaggerMs.Length)
        {
            return TimeSpan.Zero;
        }

        return TimeSpan.FromMilliseconds(MultiSplitTimerStaggerMs[slotIndex]);
    }

    private async Task PlayVisibleSlotsAsync(bool forceRestartTimers)
    {
        EnsureSlotIndices();
        debugLog.Trace($"Refreshing all visible slots. forceRestartTimers={forceRestartTimers}; slots={DescribeVisibleSlots()}; {Settings.FormatForDebugLogCompact()}.");

        // Phase 3 (a): prime cache for currently-visible images BEFORE any awaits so
        // the read races Blazor's first render. MediaSourceService coalesces in-flight
        // loads with MediaViewport.LoadImageSourceAsync, so the redundant call collapses
        // to a single disk read; we just give it a head start.
        for (var slotIndex = 0; slotIndex < MaxSlots; slotIndex++)
        {
            if (!IsActiveSlot(slotIndex))
            {
                continue;
            }

            var current = GetSlotItem(slotIndex);
            if (current is { Kind: MediaKind.Image or MediaKind.Gif })
            {
                _ = SafePrefetchAsync(current, slotIndex);
            }
        }

        for (var slotIndex = 0; slotIndex < MaxSlots; slotIndex++)
        {
            var item = IsActiveSlot(slotIndex) ? GetSlotItem(slotIndex) : null;
            await nativeVideoPlayback.PlaySlotAsync(slotIndex, item, Settings);
        }

        await EnsureSlotTimersAsync(forceRestartTimers);

        // Phase 3 (b): prime the NEXT swap for every active slot so the first
        // transition (which would otherwise be a cold cache miss) is warm too. Video
        // peeks return null and noop.
        for (var slotIndex = 0; slotIndex < MaxSlots; slotIndex++)
        {
            if (IsActiveSlot(slotIndex))
            {
                TriggerPrefetchForSlot(slotIndex);
            }
        }
    }

    private async Task PlaySingleSlotAsync(int slotIndex, bool forceRestartTimer, bool restartVideoIfSameSource)
    {
        if (!IsActiveSlot(slotIndex))
        {
            debugLog.Trace($"Ignoring play request for inactive slot {slotIndex + 1}.");
            return;
        }

        var item = GetSlotItem(slotIndex);
        debugLog.Trace($"Refreshing slot {slotIndex + 1}. forceRestartTimer={forceRestartTimer}; restartVideoIfSameSource={restartVideoIfSameSource}; index={_slotIndices[slotIndex]}; item={item?.DisplayName} ({item?.Kind}); {Settings.FormatForDebugLogCompact()}.");
        await nativeVideoPlayback.PlaySlotAsync(slotIndex, item, Settings, restartVideoIfSameSource);
        await EnsureSlotTimerAsync(slotIndex, forceRestartTimer);
    }

    private async Task EnsureSlotTimersAsync(bool forceRestartTimers)
    {
        for (var slotIndex = 0; slotIndex < MaxSlots; slotIndex++)
        {
            if (!IsActiveSlot(slotIndex))
            {
                StopSlotTimer(slotIndex);
                continue;
            }

            await EnsureSlotTimerAsync(slotIndex, forceRestartTimers);
        }
    }

    private async Task EnsureSlotTimerAsync(int slotIndex, bool forceRestartTimer)
    {
        var item = GetSlotItem(slotIndex);

        if (item is null)
        {
            debugLog.Trace($"Slot {slotIndex + 1} timer stopped; no assigned item.");
            StopSlotTimer(slotIndex);
            return;
        }

        if (item.Kind == MediaKind.Video)
        {
            debugLog.Trace($"Slot {slotIndex + 1} timer stopped; video completion is handled by native playback. item={item.DisplayName}.");
            StopSlotTimer(slotIndex);
            // Phase 3 (video->image gap): when this slot is showing a video, its next
            // swap (driven by native MediaEnded) is NOT routed through StartSlotTimerAsync,
            // so the prefetch there never fires. Without this, the first frame after the
            // video would land on a cold cache and flash the "Loading image..." placeholder.
            // The video typically plays for seconds, giving prefetch plenty of head start.
            TriggerPrefetchForSlot(slotIndex);
            return;
        }

        if (item.IsTimedVisual)
        {
            if (Settings.SideAdvanceMode == SideAdvanceMode.OnCentreChange
                && Settings.SplitScreenCount >= 3
                && slotIndex is 0 or 2)
            {
                StopSlotTimer(slotIndex);
                debugLog.Trace($"Slot {slotIndex + 1} timer deferred; advances with centre pane. item={item.DisplayName}.");
                return;
            }

            if (forceRestartTimer || _slotTimerItemIds[slotIndex] != item.Id)
            {
                await StartSlotTimerAsync(slotIndex, item);
            }
            else
            {
                debugLog.Trace($"Slot {slotIndex + 1} timer already active for {item.DisplayName}.");
            }

            return;
        }

        debugLog.Trace($"Slot {slotIndex + 1} timer stopped; unsupported timed behavior for {item.DisplayName} ({item.Kind}).");
        StopSlotTimer(slotIndex);
    }

    private async Task StartSlotTimerAsync(int slotIndex, MediaItem item)
    {
        StopSlotTimer(slotIndex);
        var timerGeneration = ++_slotTimerGenerations[slotIndex];
        var duration = await GetTimedVisualDurationAsync(slotIndex, item);

        if (Status != PlaybackStatus.Playing || GetSlotItem(slotIndex)?.Id != item.Id || _slotTimerGenerations[slotIndex] != timerGeneration)
        {
            debugLog.Trace($"Slot {slotIndex + 1} timer skipped for {item.DisplayName}; item changed while calculating duration.");
            return;
        }

        var cancellation = new CancellationTokenSource();
        _slotTimers[slotIndex] = cancellation;
        _slotTimerItemIds[slotIndex] = item.Id;
        var cancellationToken = cancellation.Token;
        debugLog.Trace($"Slot {slotIndex + 1} timer started for {item.DisplayName}; generation={timerGeneration}; duration={duration.TotalSeconds:0.##}s.");

        // Phase 3: warm the cache for the slot's next item as soon as we know how long
        // we have. Even at the lower bound of 5s (randomized side-pane duration), the
        // background read+base64 typically completes well before the swap, so the next
        // render hits a cached data URI instead of the "Loading image..." placeholder.
        TriggerPrefetchForSlot(slotIndex);

        var stagger = GetSlotTimerStagger(slotIndex);

        _ = Task.Run(async () =>
        {
            try
            {
                if (stagger > TimeSpan.Zero)
                {
                    await Task.Delay(stagger, cancellationToken);
                }

                await Task.Delay(duration, cancellationToken);

                if (!cancellationToken.IsCancellationRequested
                    && _slotTimerGenerations[slotIndex] == timerGeneration
                    && GetSlotItem(slotIndex)?.Id == item.Id)
                {
                    debugLog.Trace($"Slot {slotIndex + 1} timer fired for {item.DisplayName}; generation={timerGeneration}.");
                    await CompleteSlotAsync(slotIndex, item.FilePath, playbackGeneration: null);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, cancellationToken);
    }

    private async Task<TimeSpan> GetTimedVisualDurationAsync(int slotIndex, MediaItem item)
    {
        if (item.Kind != MediaKind.Gif)
        {
            if (Settings.ShouldRandomizeImageDurationForSlot(slotIndex))
            {
                var randomized = Settings.GetRandomizedSideImageDuration(Random.Shared, out var rawSeconds);
                debugLog.Trace($"Slot {slotIndex + 1} randomized side-image duration: raw={rawSeconds:0.##}s; effective={randomized.TotalSeconds:0.##}s; item={item.DisplayName}.");
                return randomized;
            }

            return Settings.GetAdjustedImageDuration();
        }

        var repeatCount = Math.Clamp(Settings.GifRepeatCount, PlaybackSettings.MinGifRepeatCount, PlaybackSettings.MaxGifRepeatCount);
        var gifDuration = await mediaSourceService.GetGifAnimationDurationAsync(item);
        var singleLoopDuration = gifDuration is { TotalMilliseconds: > 0 }
            ? gifDuration.Value
            : TimeSpan.FromSeconds(Math.Clamp(Settings.ImageDurationSeconds, PlaybackSettings.MinImageDurationSeconds, PlaybackSettings.MaxImageDurationSeconds));
        var totalDuration = TimeSpan.FromMilliseconds(singleLoopDuration.TotalMilliseconds * repeatCount);

        debugLog.Trace($"GIF timer calculated for {item.DisplayName}; singleLoop={singleLoopDuration.TotalSeconds:0.###}s; repeats={repeatCount}; total={totalDuration.TotalSeconds:0.###}s.");
        return totalDuration;
    }

    private IReadOnlyList<MediaItem> GetVisibleItems()
    {
        return GetVisiblePanes().Select(static pane => pane.Item).ToArray();
    }

    private IReadOnlyList<VisibleMediaSlot> GetVisiblePanes()
    {
        EnsureSlotIndices();

        if (!playlistService.HasItems)
        {
            return [];
        }

        var items = new List<VisibleMediaSlot>(Settings.SplitScreenCount);

        for (var slotIndex = 0; slotIndex < Settings.SplitScreenCount; slotIndex++)
        {
            var item = GetSlotItem(slotIndex);

            if (item is not null)
            {
                items.Add(new VisibleMediaSlot(slotIndex, item));
            }
        }

        return items;
    }

    private MediaItem? GetSlotItem(int slotIndex)
    {
        if (slotIndex < 0 || slotIndex >= MaxSlots || _slotIndices[slotIndex] < 0 || _slotIndices[slotIndex] >= playlistService.Items.Count)
        {
            return null;
        }

        return playlistService.Items[_slotIndices[slotIndex]];
    }

    private void EnsureSlotIndices()
    {
        // Synchronize with CompleteSlotAsync's slot 0 alignment block (and other writers
        // below) so we cannot observe _slotBasePlaylistIndex and playlistService.CurrentIndex
        // mid-update. Without this lock, a UI render could see a transient mismatch and
        // wrongly clobber the per-slot indices for sibling panes.
        lock (_slotStateLock)
        {
            if (!_slotIndicesInitialized || _slotBasePlaylistIndex != playlistService.CurrentIndex)
            {
                ResetSlotIndicesFromCurrent();
            }
        }
    }

    private void ResetSlotIndicesFromCurrent()
    {
        ResetSlotIndicesFromCurrentCore(allowSlot0Move: true);
    }

    // Variant used by SelectAsync so the user's clicked item stays in slot 0 even if it
    // violates slot 0's constraint. Sibling slots are still re-positioned to satisfy
    // their constraints around the clicked item.
    private void ResetSlotIndicesFromCurrentRespectingSelection()
    {
        ResetSlotIndicesFromCurrentCore(allowSlot0Move: false);
    }

    private void ResetSlotIndicesFromCurrentCore(bool allowSlot0Move)
    {
        // Reentrant via Monitor so EnsureSlotIndices may invoke us while still holding the
        // lock, and external callers (NextAsync, RandomAsync, RefreshPlaylistAsync, etc.) are
        // serialized against concurrent UI reads on _slotIndices / _slotBasePlaylistIndex.
        lock (_slotStateLock)
        {
            Array.Fill(_slotIndices, -1);
            Array.Fill(_slotNativeGenerations, null);
            Array.Fill(_slotLastUnsatisfiableTraceIndex, -1);
            ResetSlotShuffleBagsLocked();

            if (!playlistService.HasItems || playlistService.CurrentIndex < 0)
            {
                _slotIndicesInitialized = false;
                _slotBasePlaylistIndex = -1;
                return;
            }

            var slotCount = Settings.SplitScreenCount;
            _slotBasePlaylistIndex = playlistService.CurrentIndex;

            for (var slotIndex = 0; slotIndex < slotCount; slotIndex++)
            {
                var candidateIndex = playlistService.CurrentIndex + slotIndex;

                if (candidateIndex < playlistService.Items.Count)
                {
                    _slotIndices[slotIndex] = candidateIndex;
                }
                else if (Settings.LoopMode == LoopMode.All)
                {
                    _slotIndices[slotIndex] = candidateIndex % playlistService.Items.Count;
                }
            }

            _slotIndicesInitialized = true;
            ApplySlotConstraintsLocked(allowSlot0Move);
            AlignSlotsToEligibilityLocked();
        }
    }

    private bool AdvanceSlotIndex(int slotIndex)
    {
        // Acquire _slotStateLock for the entire body. This guards the new constraint helpers
        // (TryFindNextIndex / AnyActiveSlotIsVideo assert the lock is held), serializes the
        // _slotIndices mutation with EnsureSlotIndices on the UI thread, and is reentrant so
        // any future caller that already holds the lock is safe.
        lock (_slotStateLock)
        {
            if (!IsActiveSlot(slotIndex) || _slotIndices[slotIndex] < 0 || playlistService.Items.Count == 0)
            {
                return false;
            }

            var items = playlistService.Items;
            var count = items.Count;
            var stride = Settings.SplitScreenCount;
            var allowWrap = Settings.LoopMode == LoopMode.All;

            // Shuffle path runs BEFORE the stride wrap calculation: under LoopMode.Off the
            // stride math returns false as soon as _slotIndices[slot] + stride >= count,
            // which would stop the slot even though its independent shuffle bag may still
            // have unvisited indices. Computing shuffle first lets the bag drain naturally
            // (LoopMode.All refills it; LoopMode.Off lets it end the slot once truly
            // exhausted) regardless of where the stride math would have landed.
            // Single-pane shuffle (stride <= 1) is still handled by CompleteSlotAsync via
            // playlistService.MoveNext(shuffle:true) and is intentionally skipped here.
            if (Settings.Shuffle && stride > 1)
            {
                var shuffleRequirement = ComputeAdvanceRequirementForSlot(slotIndex);

                if (TryAdvanceFromShuffleLocked(slotIndex, shuffleRequirement, out var shuffleIndex))
                {
                    _slotIndices[slotIndex] = shuffleIndex;
                    return true;
                }

                // Bag was fully drained under LoopMode.Off — stop the slot. We intentionally
                // do NOT fall back to the stride path here: that would resurrect items the
                // user has already seen this cycle and contradict the shuffle ordering they
                // selected. We only fall through when the bag wasn't exhausted (degenerate
                // constraint case) so the legacy stride/constraint walk can place the slot.
                if (!allowWrap && _slotShuffleQueueInitialized[slotIndex])
                {
                    debugLog.Trace($"Slot {slotIndex + 1} shuffle bag exhausted under LoopMode.Off; stopping slot.");
                    return false;
                }
            }

            var rawCandidate = _slotIndices[slotIndex] + stride;

            int strideCandidate;
            if (rawCandidate < count)
            {
                strideCandidate = rawCandidate;
            }
            else if (allowWrap)
            {
                strideCandidate = rawCandidate % count;
            }
            else
            {
                return false;
            }

            if (!Settings.AlwaysShowVideoInSplit || stride <= 1)
            {
                return TryAdvanceSlotToIndex(slotIndex, strideCandidate, MediaKindRequirement.Any);
            }

            if (Settings.IsVideoIsolationActive)
            {
                var requirement = Settings.GetSlotKindRequirement(slotIndex);

                if (TryAdvanceSlotToIndex(slotIndex, strideCandidate, requirement))
                {
                    return true;
                }

                if (TryFindNextIndex(slotIndex, strideCandidate + 1, requirement, out var found))
                {
                    debugLog.Trace($"Slot {slotIndex + 1} stride candidate index={strideCandidate} ({items[strideCandidate].Kind}) violates {requirement}; walked to index={found} ({items[found].Kind}).");
                    _slotIndices[slotIndex] = found;
                    return true;
                }

                if (TryAdvanceSlotToIndex(slotIndex, strideCandidate, MediaKindRequirement.Any))
                {
                    MaybeWarnUnsatisfiable(slotIndex, requirement);
                    return true;
                }

                return false;
            }

            if (Settings.RequiresAtLeastOneVideo)
            {
                if (items[strideCandidate].Kind == MediaKind.Video
                    || AnyActiveSlotIsVideo(exceptSlotIndex: slotIndex))
                {
                    if (TryAdvanceSlotToIndex(slotIndex, strideCandidate, MediaKindRequirement.Any))
                    {
                        return true;
                    }
                }

                if (TryFindNextIndex(slotIndex, strideCandidate + 1, MediaKindRequirement.VideoOnly, out var found))
                {
                    debugLog.Trace($"Slot {slotIndex + 1} taking over video role; walked from index={strideCandidate} ({items[strideCandidate].Kind}) to index={found} (Video).");
                    _slotIndices[slotIndex] = found;
                    return true;
                }

                if (TryAdvanceSlotToIndex(slotIndex, strideCandidate, MediaKindRequirement.Any))
                {
                    MaybeWarnUnsatisfiable(slotIndex, MediaKindRequirement.VideoOnly);
                    return true;
                }

                return false;
            }

            return TryAdvanceSlotToIndex(slotIndex, strideCandidate, MediaKindRequirement.Any);
        }
    }

    // Predicts the MediaItem that AdvanceSlotIndex would assign to this slot on the
    // next completion, WITHOUT mutating any state (shuffle bag, slot indices, etc.).
    // Used by the background prefetch path so the image cache can be warmed before the
    // swap is rendered. Returns null when no reliable prediction is possible (e.g. the
    // shuffle bag is empty and would need a LoopMode.All refill — we don't simulate
    // the refill because the RNG outcome would diverge from the real advance).
    public MediaItem? PeekNextItemForSlot(int slotIndex)
    {
        lock (_slotStateLock)
        {
            if (!IsActiveSlot(slotIndex) || _slotIndices[slotIndex] < 0 || playlistService.Items.Count == 0)
            {
                return null;
            }

            var items = playlistService.Items;
            var count = items.Count;
            var stride = Settings.SplitScreenCount;
            var allowWrap = Settings.LoopMode == LoopMode.All;
            var currentIndex = _slotIndices[slotIndex];

            // Shuffle bag peek: scan queued indices for the first valid candidate using
            // the same filters TryAdvanceFromShuffleLocked applies. Queue<T>.GetEnumerator
            // walks the FIFO order without consuming entries, so the real advance still
            // sees the same head when it runs.
            if (Settings.Shuffle && stride > 1)
            {
                var requirement = ComputeAdvanceRequirementForSlot(slotIndex);
                var queue = GetShuffleQueueLocked(slotIndex);

                if (queue is { Count: > 0 })
                {
                    foreach (var candidate in queue)
                    {
                        if (candidate < 0 || candidate >= count)
                        {
                            continue;
                        }

                        if (!MatchesRequirement(items[candidate].Kind, requirement))
                        {
                            continue;
                        }

                        if (currentIndex == candidate)
                        {
                            continue;
                        }

                        if (HasSlotCollisionLocked(slotIndex, candidate))
                        {
                            continue;
                        }

                        return items[candidate];
                    }
                }

                // Bag exhausted (or no satisfying candidate). Don't pretend we know
                // what a refill will produce; the prefetch caller simply skips this
                // cycle and the cache warms on demand. Stride fallback would only kick
                // in on the next real advance for degenerate constraint scenarios.
                return null;
            }

            var rawCandidate = currentIndex + stride;
            int strideCandidate;
            if (rawCandidate < count)
            {
                strideCandidate = rawCandidate;
            }
            else if (allowWrap)
            {
                strideCandidate = rawCandidate % count;
            }
            else
            {
                return null;
            }

            if (!IsPlaylistIndexEligibleForSource(strideCandidate))
            {
                if (TryFindNextIndex(slotIndex, strideCandidate, MediaKindRequirement.Any, out var eligiblePeek))
                {
                    return items[eligiblePeek];
                }

                return null;
            }

            if (!Settings.AlwaysShowVideoInSplit || stride <= 1)
            {
                return items[strideCandidate];
            }

            if (Settings.IsVideoIsolationActive)
            {
                var requirement = Settings.GetSlotKindRequirement(slotIndex);

                if (requirement == MediaKindRequirement.Any
                    || MatchesRequirement(items[strideCandidate].Kind, requirement))
                {
                    return items[strideCandidate];
                }

                if (TryFindNextIndex(slotIndex, strideCandidate + 1, requirement, out var found))
                {
                    return items[found];
                }

                return items[strideCandidate];
            }

            if (Settings.RequiresAtLeastOneVideo)
            {
                if (items[strideCandidate].Kind == MediaKind.Video
                    || AnyActiveSlotIsVideo(exceptSlotIndex: slotIndex))
                {
                    return items[strideCandidate];
                }

                if (TryFindNextIndex(slotIndex, strideCandidate + 1, MediaKindRequirement.VideoOnly, out var found))
                {
                    return items[found];
                }

                return items[strideCandidate];
            }

            return items[strideCandidate];
        }
    }

    private void TriggerPrefetchForSlot(int slotIndex)
    {
        // Peek what AdvanceSlotIndex would assign on this slot's next swap and warm the
        // image cache for it. Best-effort: peek returning null (e.g. video slot peek,
        // shuffle bag empty, LoopMode.Off near end) just skips this cycle. The cache hit
        // on the real swap removes the "Loading image..." placeholder for that pane.
        var peek = PeekNextItemForSlot(slotIndex);

        if (peek is null)
        {
            return;
        }

        if (peek.Kind is not (MediaKind.Image or MediaKind.Gif))
        {
            // Videos open through MediaElement and aren't part of the data-URI cache.
            // Future enhancement could prime a hidden MediaElement here.
            return;
        }

        _ = SafePrefetchAsync(peek, slotIndex);
    }

    private async Task SafePrefetchAsync(MediaItem item, int slotIndex)
    {
        try
        {
            await mediaSourceService.PrefetchThumbnailAsync(item);
            await mediaSourceService.PrefetchImageAsync(item);
        }
        catch (Exception ex)
        {
            debugLog.Trace($"Prefetch fault for slot {slotIndex + 1}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // Returns the kind requirement a slot must satisfy when advancing under the active
    // settings. Mirrors the constraint branches in AdvanceSlotIndex so the shuffle path
    // and the stride path agree on which items are eligible.
    private MediaKindRequirement ComputeAdvanceRequirementForSlot(int slotIndex)
    {
        AssertSlotStateLockHeld();

        if (!Settings.AlwaysShowVideoInSplit || Settings.SplitScreenCount <= 1)
        {
            return MediaKindRequirement.Any;
        }

        if (Settings.IsVideoIsolationActive)
        {
            return Settings.GetSlotKindRequirement(slotIndex);
        }

        if (Settings.RequiresAtLeastOneVideo)
        {
            return AnyActiveSlotIsVideo(exceptSlotIndex: slotIndex)
                ? MediaKindRequirement.Any
                : MediaKindRequirement.VideoOnly;
        }

        return MediaKindRequirement.Any;
    }

    // Pulls the next playlist index from the slot's independent shuffle bag, refilling
    // the bag (Fisher-Yates) when it empties. Skips indices that don't match the
    // requirement, collide with another active slot's current index, or would replay
    // this slot's own current item. Returns false if the bag has been fully consumed
    // (LoopMode.Off) or no satisfying candidate exists after two refill cycles.
    // Caller MUST hold _slotStateLock.
    private bool TryAdvanceFromShuffleLocked(int slotIndex, MediaKindRequirement requirement, out int foundIndex)
    {
        AssertSlotStateLockHeld();
        foundIndex = -1;

        if (slotIndex < 0 || slotIndex >= MaxSlots)
        {
            return false;
        }

        var items = playlistService.Items;
        var count = items.Count;

        if (count == 0)
        {
            return false;
        }

        var allowWrap = Settings.LoopMode == LoopMode.All;
        var queue = GetShuffleQueueLocked(slotIndex);

        // Bounded by two refills: the first either resolves to a candidate or empties the
        // bag, the second covers wrap-around in LoopMode.All. Anything beyond means every
        // matching item is currently taken by a sibling or is this slot itself, which
        // for typical playlists indicates a genuinely degenerate case (e.g., only one
        // non-video item under NonVideoOnly with 3 panes). In that case we yield false
        // and let the stride fallback handle the placement.
        for (var refillsLeft = 2; refillsLeft >= 0; refillsLeft--)
        {
            if (queue.Count == 0)
            {
                if (_slotShuffleQueueInitialized[slotIndex] && !allowWrap)
                {
                    debugLog.Trace($"Slot {slotIndex + 1} shuffle bag exhausted (LoopMode.Off); deferring to stride/stop.");
                    return false;
                }

                if (refillsLeft <= 0)
                {
                    return false;
                }

                RefillSlotShuffleQueueLocked(slotIndex);

                if (queue.Count == 0)
                {
                    return false;
                }
            }

            while (queue.Count > 0)
            {
                var candidate = queue.Dequeue();

                if (candidate < 0 || candidate >= count)
                {
                    continue;
                }

                if (!MatchesRequirement(items[candidate].Kind, requirement))
                {
                    continue;
                }

                if (_slotIndices[slotIndex] == candidate)
                {
                    continue;
                }

                if (HasSlotCollisionLocked(slotIndex, candidate))
                {
                    continue;
                }

                debugLog.Trace($"Slot {slotIndex + 1} shuffle advance: picked index={candidate} ({items[candidate].Kind}); requirement={requirement}; bagRemaining={queue.Count}.");
                foundIndex = candidate;
                return true;
            }
        }

        return false;
    }

    // Fisher-Yates shuffles ALL playlist indices into the slot's bag. Filtering by
    // requirement happens at dequeue time so a per-slot bag stays correct across
    // requirement changes (e.g., RequiresAtLeastOneVideo flipping between Any and
    // VideoOnly as siblings advance) without forcing a full bag rebuild every advance.
    // Caller MUST hold _slotStateLock.
    private void RefillSlotShuffleQueueLocked(int slotIndex)
    {
        AssertSlotStateLockHeld();

        if (Settings.SharedSideShuffleBag && slotIndex == 2 && Settings.SplitScreenCount >= 3)
        {
            RefillSlotShuffleQueueLocked(0);
            _slotShuffleQueueInitialized[2] = _slotShuffleQueueInitialized[0];
            return;
        }

        var queue = GetShuffleQueueLocked(slotIndex);
        queue.Clear();

        var count = playlistService.Items.Count;

        if (count == 0)
        {
            _slotShuffleQueueInitialized[slotIndex] = true;
            return;
        }

        var requirement = ComputeAdvanceRequirementForSlot(slotIndex);
        var entries = BuildWeightedShuffleEntriesLocked(requirement);

        for (var i = entries.Count - 1; i > 0; i--)
        {
            var j = Random.Shared.Next(i + 1);
            (entries[i], entries[j]) = (entries[j], entries[i]);
        }

        foreach (var idx in entries)
        {
            queue.Enqueue(idx);
        }

        _slotShuffleQueueInitialized[slotIndex] = true;
        debugLog.Trace($"Slot {slotIndex + 1} shuffle bag refilled: size={queue.Count}; loopMode={Settings.LoopMode}; playlistSource={Settings.PlaylistSource}.");
    }

    // Caller MUST hold _slotStateLock.
    private bool HasSlotCollisionLocked(int slotIndex, int candidate)
    {
        AssertSlotStateLockHeld();

        var slotCount = Settings.SplitScreenCount;

        for (var other = 0; other < slotCount; other++)
        {
            if (other == slotIndex)
            {
                continue;
            }

            if (_slotIndices[other] == candidate)
            {
                return true;
            }
        }

        return false;
    }

    // Caller MUST hold _slotStateLock.
    private void ResetSlotShuffleBagsLocked()
    {
        AssertSlotStateLockHeld();

        for (var i = 0; i < MaxSlots; i++)
        {
            _slotShuffleQueues[i]?.Clear();
            _slotShuffleQueueInitialized[i] = false;
        }
    }

    [System.Diagnostics.Conditional("DEBUG")]
    private void AssertSlotStateLockHeld()
    {
        if (!System.Threading.Monitor.IsEntered(_slotStateLock))
        {
            throw new InvalidOperationException("_slotStateLock must be held by the calling thread.");
        }
    }

    private static bool MatchesRequirement(MediaKind kind, MediaKindRequirement requirement) => requirement switch
    {
        MediaKindRequirement.VideoOnly => kind == MediaKind.Video,
        MediaKindRequirement.NonVideoOnly => kind != MediaKind.Video,
        _ => true
    };

    // Linear forward scan with optional wrap (LoopMode.All) starting at startInclusive.
    // Returns the first index whose kind satisfies the requirement and is not currently
    // held by another active slot. Caller MUST hold _slotStateLock.
    private bool TryAdvanceSlotToIndex(int slotIndex, int preferredIndex, MediaKindRequirement requirement)
    {
        AssertSlotStateLockHeld();

        var items = playlistService.Items;
        var count = items.Count;

        if (preferredIndex < 0 || preferredIndex >= count)
        {
            return false;
        }

        if (IsPlaylistIndexEligibleForSource(preferredIndex)
            && MatchesRequirement(items[preferredIndex].Kind, requirement)
            && !HasSlotCollisionLocked(slotIndex, preferredIndex))
        {
            _slotIndices[slotIndex] = preferredIndex;
            return true;
        }

        if (TryFindNextIndex(slotIndex, preferredIndex, requirement, out var found))
        {
            _slotIndices[slotIndex] = found;
            return true;
        }

        return false;
    }

    private bool TryFindNextIndex(int slotIndex, int startInclusive, MediaKindRequirement requirement, out int foundIndex)
    {
        AssertSlotStateLockHeld();
        foundIndex = -1;

        var items = playlistService.Items;
        var count = items.Count;

        if (count == 0)
        {
            return false;
        }

        var allowWrap = Settings.LoopMode == LoopMode.All;
        var slotCount = Settings.SplitScreenCount;

        var start = startInclusive;

        if (start < 0)
        {
            start = 0;
        }
        else if (start >= count)
        {
            if (!allowWrap)
            {
                return false;
            }

            start %= count;
        }

        for (var step = 0; step < count; step++)
        {
            var candidate = start + step;

            if (candidate >= count)
            {
                if (!allowWrap)
                {
                    return false;
                }

                candidate %= count;
            }

            var collision = false;

            for (var other = 0; other < slotCount; other++)
            {
                if (other == slotIndex)
                {
                    continue;
                }

                if (_slotIndices[other] == candidate)
                {
                    collision = true;
                    break;
                }
            }

            if (collision)
            {
                continue;
            }

            if (!IsPlaylistIndexEligibleForSource(candidate))
            {
                continue;
            }

            if (MatchesRequirement(items[candidate].Kind, requirement))
            {
                foundIndex = candidate;
                return true;
            }
        }

        return false;
    }

    private void OnFavoritesChanged()
    {
        SyncPlaylistShuffleCallbacks();
        InvalidateShuffleState();
        ApplyPlaylistEligibilityToCurrentState();
        NotifyChanged();
    }

    private void ApplyPlaylistEligibilityToCurrentState()
    {
        if (!NeedsPlaylistIndexFilter() || !playlistService.HasItems)
        {
            return;
        }

        SnapPlaylistCurrentToEligible();

        if (GetEligiblePlaylistIndices(MediaKindRequirement.Any).Count == 0)
        {
            if (Status == PlaybackStatus.Playing)
            {
                _ = StopAsync();
            }

            return;
        }

        if (Status == PlaybackStatus.Playing)
        {
            ResetSlotIndicesFromCurrent();
            _ = PlayVisibleSlotsAsync(forceRestartTimers: true);
            return;
        }

        lock (_slotStateLock)
        {
            if (_slotIndicesInitialized)
            {
                AlignSlotsToEligibilityLocked();
            }
        }
    }

    private void SyncPlaylistShuffleCallbacks()
    {
        playlistService.ShuffleIndexFilter = NeedsPlaylistIndexFilter()
            ? IsPlaylistIndexEligibleForSource
            : null;
        playlistService.ShuffleIndexWeight = GetShuffleWeightForIndex;
    }

    private bool NeedsPlaylistIndexFilter() =>
        Settings.PlaylistSource == PlaylistSource.FavoritesOnly
        || !Settings.IsAllMediaKindsEnabled;

    private void InvalidateShuffleState()
    {
        lock (_slotStateLock)
        {
            ResetSlotShuffleBagsLocked();
        }

        playlistService.RebuildShuffleQueue();
    }

    private void NotifyFavoritesOnlyEmptyIfNeeded()
    {
        if (Settings.PlaylistSource != PlaylistSource.FavoritesOnly || FavoriteCountInPlaylist > 0)
        {
            return;
        }

        if (_favoritesEmptyNotified)
        {
            return;
        }

        _favoritesEmptyNotified = true;
        toasts.Info("No favorites in this library. Heart items on a pane or switch back to All.");
    }

    private void NotifyMediaKindsFilterEmptyIfNeeded()
    {
        if (Settings.IsAllMediaKindsEnabled || !playlistService.HasItems)
        {
            return;
        }

        if (GetEligiblePlaylistIndices(MediaKindRequirement.Any).Count > 0)
        {
            return;
        }

        if (_mediaKindsEmptyNotified)
        {
            return;
        }

        _mediaKindsEmptyNotified = true;
        toasts.Info("No items match the selected media types. Enable Images, Videos, or GIFs in the playlist.");
    }

    private void SnapPlaylistCurrentToEligible()
    {
        if (!playlistService.HasItems)
        {
            return;
        }

        var currentIndex = playlistService.CurrentIndex;

        if (currentIndex >= 0 && IsPlaylistIndexEligibleForSource(currentIndex))
        {
            return;
        }

        var items = playlistService.Items;

        for (var index = 0; index < items.Count; index++)
        {
            if (IsPlaylistIndexEligibleForSource(index))
            {
                playlistService.SetCurrentIndex(index);
                return;
            }
        }
    }

    private bool IsPlaylistIndexEligibleForSource(int index)
    {
        var items = playlistService.Items;

        if (index < 0 || index >= items.Count)
        {
            return false;
        }

        if (!Settings.IsMediaKindEnabled(items[index].Kind))
        {
            return false;
        }

        if (Settings.PlaylistSource == PlaylistSource.FavoritesOnly)
        {
            return favoritesService.IsFavorite(items[index].FilePath);
        }

        return true;
    }

    private int GetShuffleWeightForIndex(int index)
    {
        if (Settings.PlaylistSource != PlaylistSource.AllLibrary
            || !Settings.Shuffle
            || !Settings.BoostFavoritesInShuffle)
        {
            return 1;
        }

        var items = playlistService.Items;

        if (index < 0 || index >= items.Count)
        {
            return 1;
        }

        return favoritesService.IsFavorite(items[index].FilePath)
            ? Settings.FavoriteShuffleWeight
            : 1;
    }

    private List<int> GetEligiblePlaylistIndices(MediaKindRequirement requirement)
    {
        var items = playlistService.Items;
        var eligible = new List<int>(items.Count);

        for (var index = 0; index < items.Count; index++)
        {
            if (!IsPlaylistIndexEligibleForSource(index))
            {
                continue;
            }

            if (!MatchesRequirement(items[index].Kind, requirement))
            {
                continue;
            }

            eligible.Add(index);
        }

        return eligible;
    }

    private List<int> BuildWeightedShuffleEntriesLocked(MediaKindRequirement requirement)
    {
        AssertSlotStateLockHeld();
        var entries = new List<int>();
        var items = playlistService.Items;

        for (var index = 0; index < items.Count; index++)
        {
            if (!IsPlaylistIndexEligibleForSource(index))
            {
                continue;
            }

            if (!MatchesRequirement(items[index].Kind, requirement))
            {
                continue;
            }

            var weight = GetShuffleWeightForIndex(index);

            for (var copy = 0; copy < weight; copy++)
            {
                entries.Add(index);
            }
        }

        return entries;
    }

    private void AlignSlotsToEligibilityLocked()
    {
        AssertSlotStateLockHeld();

        if (!NeedsPlaylistIndexFilter())
        {
            return;
        }

        for (var slotIndex = 0; slotIndex < Settings.SplitScreenCount; slotIndex++)
        {
            if (!IsActiveSlot(slotIndex) || _slotIndices[slotIndex] < 0)
            {
                continue;
            }

            if (IsPlaylistIndexEligibleForSource(_slotIndices[slotIndex]))
            {
                continue;
            }

            if (TryFindNextIndex(slotIndex, _slotIndices[slotIndex], MediaKindRequirement.Any, out var found))
            {
                _slotIndices[slotIndex] = found;
            }
            else
            {
                _slotIndices[slotIndex] = -1;
            }
        }
    }

    // Returns true if any active slot other than exceptSlotIndex currently points to a Video.
    // Caller MUST hold _slotStateLock.
    private bool AnyActiveSlotIsVideo(int? exceptSlotIndex)
    {
        AssertSlotStateLockHeld();

        var slotCount = Settings.SplitScreenCount;
        var items = playlistService.Items;
        var count = items.Count;

        for (var i = 0; i < slotCount; i++)
        {
            if (exceptSlotIndex.HasValue && exceptSlotIndex.Value == i)
            {
                continue;
            }

            var idx = _slotIndices[i];

            if (idx < 0 || idx >= count)
            {
                continue;
            }

            if (items[idx].Kind == MediaKind.Video)
            {
                return true;
            }
        }

        return false;
    }

    // Walks every active slot after a stride-based placement and re-positions any slot that
    // violates the active constraint. Used by ResetSlotIndicesFromCurrent and by the
    // Select-respecting reset variant. When allowSlot0Move is false (SelectAsync), slot 0
    // stays on the user's clicked item even if it violates its requirement; siblings still
    // satisfy their constraints around it. Caller MUST hold _slotStateLock.
    private void ApplySlotConstraintsLocked(bool allowSlot0Move)
    {
        AssertSlotStateLockHeld();

        if (!_slotIndicesInitialized || !playlistService.HasItems)
        {
            return;
        }

        var slotCount = Settings.SplitScreenCount;

        if (slotCount <= 1 || !Settings.AlwaysShowVideoInSplit)
        {
            return;
        }

        var items = playlistService.Items;

        if (Settings.IsVideoIsolationActive)
        {
            for (var slot = 0; slot < slotCount; slot++)
            {
                if (slot == 0 && !allowSlot0Move)
                {
                    continue;
                }

                var requirement = Settings.GetSlotKindRequirement(slot);

                if (requirement == MediaKindRequirement.Any)
                {
                    continue;
                }

                var idx = _slotIndices[slot];

                if (idx >= 0 && idx < items.Count && MatchesRequirement(items[idx].Kind, requirement))
                {
                    continue;
                }

                var startFrom = idx < 0 ? 0 : idx + 1;

                if (TryFindNextIndex(slot, startFrom, requirement, out var found))
                {
                    debugLog.Trace($"Slot {slot + 1} post-pass moved index={idx} -> index={found} to satisfy {requirement}.");
                    _slotIndices[slot] = found;

                    if (slot == 0)
                    {
                        var primary = GetSlotItem(0);

                        if (primary is not null)
                        {
                            _slotBasePlaylistIndex = found;
                            playlistService.SetCurrent(primary);
                        }
                    }
                }
                else
                {
                    MaybeWarnUnsatisfiable(slot, requirement);
                }
            }

            return;
        }

        if (Settings.RequiresAtLeastOneVideo)
        {
            if (AnyActiveSlotIsVideo(exceptSlotIndex: null))
            {
                return;
            }

            // When click is respected on slot 0, prefer rotating the video role onto a
            // sibling so the user's selection stays put. Otherwise, walk slot 0 forward.
            if (!allowSlot0Move)
            {
                for (var slot = 1; slot < slotCount; slot++)
                {
                    var siblingIdx = _slotIndices[slot];
                    var siblingStart = siblingIdx < 0 ? 0 : siblingIdx + 1;

                    if (TryFindNextIndex(slot, siblingStart, MediaKindRequirement.VideoOnly, out var siblingFound))
                    {
                        debugLog.Trace($"Slot {slot + 1} took over video role; moved from index={siblingIdx} to index={siblingFound} (selection respected on slot 1).");
                        _slotIndices[slot] = siblingFound;
                        MaybeShowVideoTakeoverHint();
                        return;
                    }
                }

                MaybeWarnUnsatisfiable(0, MediaKindRequirement.VideoOnly);
                return;
            }

            var slot0Idx = _slotIndices[0];
            var slot0Start = slot0Idx < 0 ? 0 : slot0Idx + 1;

            if (TryFindNextIndex(0, slot0Start, MediaKindRequirement.VideoOnly, out var slot0Found))
            {
                debugLog.Trace($"Slot 1 took over video role; moved from index={slot0Idx} to index={slot0Found}.");
                _slotIndices[0] = slot0Found;
                MaybeShowVideoTakeoverHint();

                var primary = GetSlotItem(0);

                if (primary is not null)
                {
                    _slotBasePlaylistIndex = slot0Found;
                    playlistService.SetCurrent(primary);
                }
            }
            else
            {
                MaybeWarnUnsatisfiable(0, MediaKindRequirement.VideoOnly);
            }
        }
    }

    private void MaybeWarnUnsatisfiable(int slotIndex, MediaKindRequirement requirement)
    {
        if (slotIndex < 0 || slotIndex >= MaxSlots)
        {
            return;
        }

        var currentIdx = _slotIndices[slotIndex];

        if (_slotLastUnsatisfiableTraceIndex[slotIndex] == currentIdx)
        {
            return;
        }

        _slotLastUnsatisfiableTraceIndex[slotIndex] = currentIdx;
        debugLog.Trace($"Slot {slotIndex + 1} {requirement} constraint cannot be satisfied; falling back to stride candidate {currentIdx}.");
    }

    private Queue<int> GetShuffleQueueLocked(int slotIndex)
    {
        AssertSlotStateLockHeld();

        if (Settings.SharedSideShuffleBag && slotIndex == 2 && Settings.SplitScreenCount >= 3)
        {
            return _slotShuffleQueues[0] ??= new Queue<int>();
        }

        return _slotShuffleQueues[slotIndex] ??= new Queue<int>();
    }

    private void MaybeShowVideoTakeoverHint()
    {
        if (_videoTakeoverHintShown)
        {
            return;
        }

        _videoTakeoverHintShown = true;
        toasts.Info("A side pane took over video playback so your selection stays visible in the center.");
    }

    private void LogActiveConfiguration(string reason)
    {
        debugLog.Debug(
            $"Active configuration ({reason}). status={Status}; playlistItems={playlistService.Items.Count}; playlistPosition={playlistService.CounterText}; {Settings.FormatForDebugLog()}; visibleSlots={DescribeVisibleSlots()}.");
    }

    public string DescribeVisibleSlotsForDiagnostics() => DescribeVisibleSlots();

    private string DescribeVisibleSlots()
    {
        return string.Join("; ", Enumerable.Range(0, Settings.SplitScreenCount)
            .Select(slotIndex =>
            {
                var item = GetSlotItem(slotIndex);
                return $"slot={slotIndex + 1},index={_slotIndices[slotIndex]},item={item?.DisplayName ?? "(none)"},kind={item?.Kind.ToString() ?? "(none)"}";
            }));
    }

    private bool HasVisibleSlotItems()
    {
        return Enumerable.Range(0, Settings.SplitScreenCount).Any(slotIndex => GetSlotItem(slotIndex) is not null);
    }

    private bool IsActiveSlot(int slotIndex)
    {
        return slotIndex >= 0 && slotIndex < Settings.SplitScreenCount && slotIndex < MaxSlots;
    }

    private void StopSlotTimer(int slotIndex)
    {
        if (slotIndex < 0 || slotIndex >= MaxSlots)
        {
            return;
        }

        if (_slotTimers[slotIndex] is not null)
        {
            debugLog.Trace($"Slot {slotIndex + 1} timer cancelled.");
        }

        _slotTimerGenerations[slotIndex]++;
        _slotTimers[slotIndex]?.Cancel();
        _slotTimers[slotIndex]?.Dispose();
        _slotTimers[slotIndex] = null;
        _slotTimerItemIds[slotIndex] = null;
    }

    private void StopAllSlotTimers()
    {
        for (var slotIndex = 0; slotIndex < MaxSlots; slotIndex++)
        {
            StopSlotTimer(slotIndex);
        }
    }

    private void NotifyChanged()
    {
        Changed?.Invoke();
    }

    public ValueTask DisposeAsync()
    {
        StopAllSlotTimers();
        nativeVideoPlayback.ProgressChanged -= UpdateVideoProgress;
        nativeVideoPlayback.EndReached -= OnNativeVideoEnded;
        nativeVideoPlayback.PlaybackStarted -= OnNativeVideoStarted;
        playlistService.Changed -= PruneImageCacheToPlaylist;
        favoritesService.Changed -= OnFavoritesChanged;
        return ValueTask.CompletedTask;
    }
}

public sealed record VisibleMediaSlot(int SlotIndex, MediaItem Item);
