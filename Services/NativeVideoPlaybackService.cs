using CommunityToolkit.Maui.Core;
using CommunityToolkit.Maui.Views;
using MauiMediaPlayer.Models;

namespace MauiMediaPlayer.Services;

public sealed class NativeVideoPlaybackService(DebugLogService debugLog)
{
    private const int MaxSlots = 3;
    private const int MaxStartupRetries = 3;
    private static readonly TimeSpan StartupRetryDelay = TimeSpan.FromSeconds(1.5);
    // First-retry uses a shorter delay so a dropped initial Play() (Windows backend often
    // ignores Play() until the native pipeline finishes opening the source) becomes
    // visible to the watchdog faster. The synthesized-open path in
    // ObserveSourceReadyForPlayback should usually fire before this triggers; this is the
    // defensive fallback when the platform reports a non-zero position before duration.
    private static readonly TimeSpan FirstStartupRetryDelay = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan StoppedStateCompletionGrace = TimeSpan.FromSeconds(1);
    private static readonly Rect HiddenBounds = new(-10000, -10000, 1, 1);
    private readonly VideoSlot[] _slots = Enumerable.Range(0, MaxSlots).Select(index => new VideoSlot(index)).ToArray();
    private PlaybackSettings _lastSettings = new();

    public event Action<double, double>? ProgressChanged;

    public event Action<int, string?, int>? EndReached;

    public event Action<int, string, int>? PlaybackStarted;

    public string Status => "Native in-app media element is available.";

    public void RegisterMediaElement(int slotIndex, MediaElement mediaElement)
    {
        if (!TryGetSlot(slotIndex, out var slot))
        {
            return;
        }

        slot.MediaElement = mediaElement;
        mediaElement.Opacity = 0;
        // ShouldAutoPlay=true lets the platform start playback the moment the new
        // Source finishes opening. On the Windows backend this removes the dropped
        // initial Play() race during the Opening state (the explicit Play() we issue
        // below becomes a no-op backstop). Combined with the reveal-on-Playing gate
        // (TryRevealSlotLocked) the user never sees the still first frame: while the
        // pipeline is opening the surface stays at Opacity=0 / HiddenBounds, and the
        // moment StateChanged reports Playing we restore real bounds + Opacity=1.
        mediaElement.ShouldAutoPlay = true;
        mediaElement.ShouldLoopPlayback = false;
        mediaElement.ShouldShowPlaybackControls = false;
        AbsoluteLayout.SetLayoutBounds(mediaElement, HiddenBounds);

        mediaElement.MediaOpened += (_, _) =>
        {
            _ = MainThread.InvokeOnMainThreadAsync(() =>
            {
                if (slot.MediaElement is null)
                {
                    return;
                }

                if (slot.CurrentPath is null)
                {
                    debugLog.Trace($"Ignored native video media opened for an inactive slot: slot={slot.Index}.");
                    return;
                }

                debugLog.Info($"Native video media opened: slot={slot.Index}; file={Path.GetFileName(slot.CurrentPath)}; duration={slot.MediaElement.Duration}.");
                slot.SuppressNextEndedEvent = false;
                slot.EndSignaled = false;
                slot.StoppedStateStartedAt = null;

                // Mark the source as acknowledged so the synthesized-open path in
                // ObserveSourceReadyForPlayback never fires a duplicate Play() for this
                // source generation. Whichever signal (native MediaOpened or synthesized)
                // arrives first wins; the other becomes a no-op.
                var alreadyAcknowledged = slot.SourceLoadAcknowledged;
                slot.SourceLoadAcknowledged = true;
                PublishProgress(slot);

                if (slot.PlayWhenReady && !alreadyAcknowledged)
                {
                    TryStartSlotPlayback(slot, _lastSettings, "media-opened");
                }
            });
        };

        mediaElement.PositionChanged += (_, args) =>
        {
            _ = MainThread.InvokeOnMainThreadAsync(() => PublishProgress(slot, args.Position));
        };

        mediaElement.MediaEnded += (_, _) =>
        {
            _ = MainThread.InvokeOnMainThreadAsync(() =>
            {
                if (slot.SuppressNextEndedEvent)
                {
                    slot.SuppressNextEndedEvent = false;
                    debugLog.Trace($"Ignored native video ended event from an intentional stop: slot={slot.Index}; file={Path.GetFileName(slot.CurrentPath)}.");
                    return;
                }

                if (DateTimeOffset.UtcNow < slot.IgnoreEndedEventsUntil)
                {
                    debugLog.Trace($"Ignored native video ended event during source transition: slot={slot.Index}; file={Path.GetFileName(slot.CurrentPath)}.");
                    return;
                }

                if (!slot.PlayWhenReady || slot.CurrentPath is null)
                {
                    debugLog.Trace($"Ignored stale native video ended event: slot={slot.Index}; playWhenReady={slot.PlayWhenReady}; file={Path.GetFileName(slot.CurrentPath)}.");
                    return;
                }

                debugLog.Info($"Native video ended: slot={slot.Index}; file={Path.GetFileName(slot.CurrentPath)}.");
                StopProgressTimer(slot);
                SignalEndReached(slot, "native-ended");
            });
        };

        mediaElement.MediaFailed += (_, args) =>
        {
            _ = MainThread.InvokeOnMainThreadAsync(() =>
            {
                debugLog.Error($"Native video engine failed in slot {slot.Index} while playing {slot.CurrentPath}: {args.ErrorMessage}");
                StopProgressTimer(slot);

                if (slot.PlayWhenReady && slot.CurrentPath is not null)
                {
                    SignalEndReached(slot, "native-failed");
                }
            });
        };

        mediaElement.StateChanged += (_, args) =>
        {
            _ = MainThread.InvokeOnMainThreadAsync(() =>
            {
                debugLog.Trace($"Native video state changed: slot={slot.Index}; {args.PreviousState} -> {args.NewState}.");

                if (slot.MediaElement is not null && slot.CurrentPath is not null)
                {
                    ObserveSourceReadyForPlayback(slot, args.NewState, slot.MediaElement.Position, slot.MediaElement.Duration);
                    ObserveStartupState(slot, args.NewState, slot.MediaElement.Position, "state-changed");
                    ObserveStoppedState(slot, args.NewState, slot.MediaElement.Position, slot.MediaElement.Duration, "state-changed");
                }
            });
        };

        debugLog.Info($"Native in-app media element registered: slot={slot.Index}.");
        slot.ViewReady.TrySetResult();
        _ = TryPlayPendingSlotAsync(slot, "media-element-registered");
    }

    public async Task SetBoundsAsync(int slotIndex, double x, double y, double width, double height)
    {
        if (!TryGetSlot(slotIndex, out var slot))
        {
            return;
        }

        var bounds = new Rect(x, y, Math.Max(0, width), Math.Max(0, height));
        var hasPlayableBounds = bounds.Width > 1 && bounds.Height > 1;

        if (slot.LastBounds == bounds)
        {
            return;
        }

        slot.LastBounds = bounds;
        slot.HasPlayableBounds = hasPlayableBounds;

        if (hasPlayableBounds)
        {
            slot.BoundsReady.TrySetResult();
        }
        else
        {
            slot.BoundsReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            if (slot.MediaElement is null)
            {
                return;
            }

            // While a modal/dialog is suppressing native pixels, never reapply real
            // bounds — the SwapChainPanel would jump back on top of the overlay on
            // the next resize event. SuppressForOverlayAsync(false) is the single
            // place that restores LastBounds when the overlay closes.
            var shouldExposeBounds = slot.IsVisible && hasPlayableBounds && !_overlaySuppressed;
            AbsoluteLayout.SetLayoutBounds(slot.MediaElement, shouldExposeBounds ? bounds : HiddenBounds);

            if (shouldExposeBounds)
            {
                slot.MediaElement.Opacity = 1;
            }

            // Bounds-arrives-after-Playing race: if the slot already observed Playing
            // while the JS bounds observer was still settling (initial layout, resize,
            // split-mode toggle), TryRevealSlotLocked refused to reveal because
            // HasPlayableBounds was still false. Now that we have real bounds, finish
            // the reveal here so the slot doesn't sit hidden forever.
            if (slot.PendingReveal && slot.HasObservedPlayback)
            {
                TryRevealSlotLocked(slot, "bounds-ready-after-playing");
            }
        });

        if (hasPlayableBounds)
        {
            await TryPlayPendingSlotAsync(slot, "bounds-ready");
        }
    }

    public Task SetBoundsAsync(double x, double y, double width, double height)
    {
        return SetBoundsAsync(0, x, y, width, height);
    }

    public async Task ShowAsync(int slotIndex)
    {
        if (!TryGetSlot(slotIndex, out var slot) || slot.IsVisible)
        {
            return;
        }

        if (!slot.ViewReady.Task.IsCompletedSuccessfully || !slot.HasPlayableBounds)
        {
            return;
        }

        slot.IsVisible = true;

        if (_overlaySuppressed)
        {
            // Same-source replay / loop arrived during a modal. Logical visibility is
            // restored but pixels stay suppressed; SuppressForOverlayAsync(false) will
            // expose them when the modal closes.
            return;
        }

        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            if (slot.MediaElement is not null && slot.HasPlayableBounds)
            {
                AbsoluteLayout.SetLayoutBounds(slot.MediaElement, slot.LastBounds);
                slot.MediaElement.Opacity = 1;
            }
        });
    }

    public Task ShowAsync()
    {
        return ShowAsync(0);
    }

    public async Task HideAsync(int slotIndex)
    {
        if (!TryGetSlot(slotIndex, out var slot) || !slot.IsVisible)
        {
            return;
        }

        slot.IsVisible = false;
        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            if (slot.MediaElement is not null)
            {
                slot.MediaElement.Opacity = 0;
                AbsoluteLayout.SetLayoutBounds(slot.MediaElement, HiddenBounds);
            }
        });
    }

    // Reveals a slot whose source change was loaded under a hide-until-playing gate.
    // Called from the UI thread (StateChanged dispatches through MainThread). Idempotent:
    // a second call after the first reveal is a cheap no-op because PendingReveal is
    // already cleared. We deliberately do NOT call ShowAsync here because ShowAsync skips
    // when IsVisible is already true (it would never re-apply the latest bounds after a
    // resize during the hidden window).
    private void TryRevealSlotLocked(VideoSlot slot, string reason)
    {
        if (!slot.PendingReveal || slot.MediaElement is null)
        {
            return;
        }

        if (!slot.HasPlayableBounds)
        {
            // Wait for SetBoundsAsync to deliver real coordinates before we expose
            // the surface; otherwise we'd flash a 1x1 sliver at HiddenBounds.
            return;
        }

        slot.PendingReveal = false;
        slot.IsVisible = true;

        if (_overlaySuppressed)
        {
            // A modal/dialog is currently suppressing all native video pixels.
            // We still flip `IsVisible` so that SuppressForOverlayAsync(false) will
            // restore this slot when the modal closes, but we must NOT paint bounds
            // or opacity here — otherwise the freshly-Playing video would pop up
            // ABOVE the modal (the native overlay always wins over WebView HTML).
            debugLog.Trace($"Native video reveal deferred (overlay suppressed): slot={slot.Index}; reason={reason}; generation={slot.PlaybackGeneration}; file={Path.GetFileName(slot.CurrentPath)}.");
            return;
        }

        AbsoluteLayout.SetLayoutBounds(slot.MediaElement, slot.LastBounds);
        slot.MediaElement.Opacity = 1;
        debugLog.Trace($"Native video reveal: slot={slot.Index}; reason={reason}; generation={slot.PlaybackGeneration}; file={Path.GetFileName(slot.CurrentPath)}.");
    }

    public async Task HideAsync()
    {
        foreach (var slot in _slots)
        {
            await HideAsync(slot.Index);
        }
    }

    // Modal-overlay suppression. The native MediaElement on Windows is rendered
    // as a SwapChainPanel composited ABOVE the BlazorWebView, so any HTML overlay
    // (modal, dialog backdrop, etc.) is painted BEHIND the video. Without
    // suppression, opening the Keyboard Shortcuts / About / Add Media modals
    // appears to render "behind" the playing videos. We work around this by
    // dropping the native surface to Opacity=0 + HiddenBounds for the duration
    // of the overlay, then restoring it on close.
    //
    // We deliberately preserve `IsVisible` and `PendingReveal` rather than
    // round-tripping HideAsync/ShowAsync. That preserves the reveal-on-Playing
    // gate: if a slot is in PendingReveal when suppression begins, it stays
    // PendingReveal, so TryRevealSlotLocked still gates the first visible frame
    // on the platform reporting Playing — and a Playing arrival during
    // suppression simply flips the logical visibility without exposing pixels.
    // The opacity/bounds are then restored together on unsuppress.
    private volatile bool _overlaySuppressed;

    public Task SuppressForOverlayAsync(bool suppress)
    {
        if (_overlaySuppressed == suppress)
        {
            return Task.CompletedTask;
        }

        _overlaySuppressed = suppress;

        return MainThread.InvokeOnMainThreadAsync(() =>
        {
            foreach (var slot in _slots)
            {
                if (slot.MediaElement is null)
                {
                    continue;
                }

                if (suppress)
                {
                    slot.MediaElement.Opacity = 0;
                    AbsoluteLayout.SetLayoutBounds(slot.MediaElement, HiddenBounds);
                }
                else if (slot.IsVisible && slot.HasPlayableBounds)
                {
                    AbsoluteLayout.SetLayoutBounds(slot.MediaElement, slot.LastBounds);
                    slot.MediaElement.Opacity = 1;
                }
            }
        });
    }

    public Task PlayAsync(MediaItem item, PlaybackSettings settings)
    {
        return PlayAsync([item], settings);
    }

    public async Task PlayAsync(IReadOnlyList<MediaItem> items, PlaybackSettings settings)
    {
        _lastSettings = settings;

        for (var index = 0; index < MaxSlots; index++)
        {
            var item = index < items.Count ? items[index] : null;
            await PlaySlotAsync(index, item, settings);
        }
    }

    public async Task PlaySlotAsync(int slotIndex, MediaItem? item, PlaybackSettings settings, bool restartIfSameSource = false)
    {
        _lastSettings = settings;

        if (item?.Kind == MediaKind.Video)
        {
            await PlayVideoSlotAsync(slotIndex, item, settings, restartIfSameSource);
            return;
        }

        debugLog.Trace(item is null
            ? $"Native slot {slotIndex} cleared; no media item assigned."
            : $"Native slot {slotIndex} stopping video surface for {item.DisplayName} ({item.Kind}).");
        if (TryGetSlot(slotIndex, out var slot))
        {
            ClearPendingSlotPlayback(slot);
        }

        await StopSlotAsync(slotIndex, resetPrimaryProgress: slotIndex == 0);
        await HideAsync(slotIndex);
    }

    public async Task PauseAsync()
    {
        foreach (var slot in _slots)
        {
            slot.PlayWhenReady = false;
            slot.StoppedStateStartedAt = null;
            slot.PausedStateStartedAt = null;
            StopProgressTimer(slot);
        }

        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            foreach (var slot in _slots)
            {
                slot.MediaElement?.Pause();
            }

            PublishProgress(_slots[0]);
        });
    }

    public async Task StopAsync()
    {
        foreach (var slot in _slots)
        {
            await StopSlotAsync(slot.Index, resetPrimaryProgress: slot.Index == 0);
        }

        debugLog.Debug("Native video stopped.");
    }

    public async Task SeekAsync(double seconds)
    {
        var primarySlot = _slots[0];

        if (primarySlot.MediaElement is null)
        {
            return;
        }

        await MainThread.InvokeOnMainThreadAsync(async () =>
        {
            if (primarySlot.MediaElement is null)
            {
                return;
            }

            await primarySlot.MediaElement.SeekTo(TimeSpan.FromSeconds(Math.Max(0, seconds)), CancellationToken.None);
            PublishProgress(primarySlot);
        });
    }

    public void ApplySettings(PlaybackSettings settings)
    {
        _lastSettings = settings;

        foreach (var slot in _slots)
        {
            ApplySettings(slot, settings);
        }
    }

    private async Task PlayVideoSlotAsync(int slotIndex, MediaItem item, PlaybackSettings settings, bool restartIfSameSource)
    {
        if (!TryGetSlot(slotIndex, out var slot))
        {
            return;
        }

        var playRequestGeneration = QueuePendingSlotPlayback(slot, item, settings, restartIfSameSource);

        if (slot.MediaElement is null)
        {
            debugLog.Trace($"Native video play queued until slot {slotIndex} is registered: file={item.DisplayName}.");
            return;
        }

        if (!await WaitForPlayableSurfaceAsync(slot))
        {
            debugLog.Trace($"Native video play queued until slot {slotIndex} canvas is ready: file={item.DisplayName}.");
            return;
        }

        if (playRequestGeneration != slot.PlayRequestGeneration || slot.PendingItem?.Id != item.Id)
        {
            debugLog.Trace($"Discarding stale native video play request: slot={slotIndex}; file={item.DisplayName}.");
            return;
        }

        ClearPendingSlotPlayback(slot);

        var sameSource = string.Equals(slot.CurrentPath, item.FilePath, StringComparison.OrdinalIgnoreCase);
        var isSourceChange = !sameSource;

        // Phase 1 (reveal-on-Playing): when the file actually changes we keep the
        // MediaElement hidden (Opacity=0 + HiddenBounds) across the open-and-start
        // window so the user never sees the platform's "first decoded frame, still
        // paused" state. Same-source restarts (LoopMode.One, manual restart) stay
        // visible to avoid a flicker every loop. ShowAsync becomes the reveal trigger
        // fired by TryRevealSlotLocked once StateChanged reports Playing.
        if (isSourceChange)
        {
            await HideAsync(slotIndex);
        }

        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            if (playRequestGeneration != slot.PlayRequestGeneration)
            {
                debugLog.Trace($"Discarding stale native video UI play request: slot={slotIndex}; file={item.DisplayName}.");
                return;
            }

            slot.PlayWhenReady = true;

            if (isSourceChange || restartIfSameSource)
            {
                StopProgressTimer(slot);
                slot.SuppressNextEndedEvent = true;
                slot.EndSignaled = false;
                slot.ProgressEndCheckArmed = false;
                slot.HasObservedPlayback = false;
                slot.SourceLoadAcknowledged = false;
                slot.StoppedStateStartedAt = null;
                slot.PausedStateStartedAt = null;
                slot.StartupRetryCount = 0;
                slot.LastPlayRequestAt = null;
                slot.PlaybackGeneration++;
                slot.LastProgressLogAt = DateTimeOffset.MinValue;
                slot.IgnoreEndedEventsUntil = DateTimeOffset.UtcNow.AddMilliseconds(300);
                // Reveal gate: only arm for true source changes so same-source restarts
                // don't blink. The flag clears either via TryRevealSlotLocked when we
                // observe Playing, or via StopSlotAsync if the slot is torn down first.
                slot.PendingReveal = isSourceChange;
                var generation = slot.PlaybackGeneration;
                slot.MediaElement.Stop();
                slot.MediaElement.Source = MediaSource.FromFile(item.FilePath);
                slot.CurrentPath = item.FilePath;
                PublishProgressResetIfPrimary(slot);
                var reason = isSourceChange ? "source-change" : "same-source-restart";
                debugLog.Info($"Native video media loaded: slot={slot.Index}; generation={generation}; reason={reason}; file={item.DisplayName}; path={item.FilePath}");
                PlaybackStarted?.Invoke(slot.Index, item.FilePath, generation);
                TryStartSlotPlayback(slot, settings, reason);
            }
            else
            {
                slot.EndSignaled = false;
                slot.ProgressEndCheckArmed = true;
                slot.StoppedStateStartedAt = null;
                slot.PausedStateStartedAt = null;
                slot.StartupRetryCount = 0;
                slot.LastPlayRequestAt = null;
                // Same-source path reveals unconditionally via ShowAsync below, so any
                // stranded reveal flag from the original load is now redundant. Without
                // this clear, a later StateChanged=Playing would log a duplicate reveal
                // trace for an already-visible surface.
                slot.PendingReveal = false;
                debugLog.Trace($"Native video slot {slot.Index} continuing same source: generation={slot.PlaybackGeneration}; file={item.DisplayName}.");
                PlaybackStarted?.Invoke(slot.Index, item.FilePath, slot.PlaybackGeneration);
                TryStartSlotPlayback(slot, settings, "same-source");
            }
        });

        // Same-source continuation (no reveal gate): keep the previously-visible
        // surface visible. For source-change we wait for the reveal in
        // TryRevealSlotLocked so the user never sees the freeze-frame.
        if (!isSourceChange)
        {
            await ShowAsync(slotIndex);
        }
    }

    private async Task StopSlotAsync(int slotIndex, bool resetPrimaryProgress)
    {
        if (!TryGetSlot(slotIndex, out var slot))
        {
            return;
        }

        slot.PlayWhenReady = false;
        slot.PlayRequestGeneration++;
        ClearPendingSlotPlayback(slot);
        StopProgressTimer(slot);

        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            slot.SuppressNextEndedEvent = true;
            slot.EndSignaled = false;
            slot.ProgressEndCheckArmed = false;
            slot.HasObservedPlayback = false;
            slot.SourceLoadAcknowledged = false;
            slot.PendingReveal = false;
            slot.StoppedStateStartedAt = null;
            slot.PausedStateStartedAt = null;
            slot.StartupRetryCount = 0;
            slot.LastPlayRequestAt = null;
            slot.PlaybackGeneration++;
            slot.IgnoreEndedEventsUntil = DateTimeOffset.UtcNow.AddMilliseconds(300);
            slot.MediaElement?.Stop();
            slot.MediaElement?.ClearValue(MediaElement.SourceProperty);
            slot.CurrentPath = null;

            if (resetPrimaryProgress)
            {
                ProgressChanged?.Invoke(0, 0);
            }
        });
    }

    private void ApplySettings(VideoSlot slot, PlaybackSettings settings)
    {
        if (slot.MediaElement is null)
        {
            return;
        }

        slot.MediaElement.Speed = settings.PlaybackSpeed;
        slot.MediaElement.Volume = Math.Clamp(settings.Volume, 0, 1);
        slot.MediaElement.ShouldMute = settings.Muted;
    }

    private void TryStartSlotPlayback(VideoSlot slot, PlaybackSettings settings, string reason)
    {
        if (slot.MediaElement is null)
        {
            return;
        }

        try
        {
            ApplySettings(slot, settings);
            slot.StoppedStateStartedAt = null;
            slot.LastPlayRequestAt = DateTimeOffset.UtcNow;
            slot.MediaElement.Play();
            StartProgressTimer(slot);
            debugLog.Trace($"Native video play requested: slot={slot.Index}; reason={reason}; file={Path.GetFileName(slot.CurrentPath)}.");
        }
        catch (Exception exception)
        {
            var retryNote = reason == "media-opened"
                ? "no further native retry is expected"
                : "waiting for media-opened retry";
            debugLog.Error($"Native video play request failed for slot {slot.Index} ({reason}); {retryNote}. {exception.Message}");

            if (reason == "media-opened")
            {
                SignalEndReached(slot, "play-request-failed");
            }
        }
    }

    private async Task TryPlayPendingSlotAsync(VideoSlot slot, string reason)
    {
        if (slot.PendingItem is null || slot.MediaElement is null || !slot.HasPlayableBounds)
        {
            return;
        }

        var pendingItem = slot.PendingItem;
        var pendingSettings = slot.PendingSettings ?? _lastSettings;
        var restartIfSameSource = slot.PendingRestartIfSameSource;
        debugLog.Trace($"Retrying queued native video play: slot={slot.Index}; reason={reason}; file={pendingItem.DisplayName}.");
        await PlayVideoSlotAsync(slot.Index, pendingItem, pendingSettings, restartIfSameSource);
    }

    private static int QueuePendingSlotPlayback(VideoSlot slot, MediaItem item, PlaybackSettings settings, bool restartIfSameSource)
    {
        slot.PlayRequestGeneration++;
        slot.PendingItem = item;
        slot.PendingSettings = settings;
        slot.PendingRestartIfSameSource = restartIfSameSource;
        return slot.PlayRequestGeneration;
    }

    private static void ClearPendingSlotPlayback(VideoSlot slot)
    {
        slot.PendingItem = null;
        slot.PendingSettings = null;
        slot.PendingRestartIfSameSource = false;
    }

    private void PublishProgress(VideoSlot slot, TimeSpan? position = null)
    {
        PublishProgress(slot, slot.PlaybackGeneration, position);
    }

    private void PublishProgress(VideoSlot slot, int playbackGeneration, TimeSpan? position = null)
    {
        if (slot.MediaElement is null)
        {
            return;
        }

        if (playbackGeneration != slot.PlaybackGeneration)
        {
            return;
        }

        if (slot.CurrentPath is null)
        {
            return;
        }

        var safePosition = position ?? slot.MediaElement.Position;
        var safeDuration = slot.MediaElement.Duration;

        if (slot.Index == 0)
        {
            ProgressChanged?.Invoke(
                Math.Max(0, safePosition.TotalSeconds),
                Math.Max(0, safeDuration.TotalSeconds));
        }

        PublishProgressHeartbeat(slot, safePosition, safeDuration);
        // Synthesized-open must run before the startup-state watchdog so the latter sees
        // the bumped LastPlayRequestAt and waits its full first-retry window before
        // firing a redundant retry. Order matters here.
        ObserveSourceReadyForPlayback(slot, slot.MediaElement.CurrentState, safePosition, safeDuration);
        ObserveStartupState(slot, slot.MediaElement.CurrentState, safePosition, "progress-poll");
        ObserveStoppedState(slot, slot.MediaElement.CurrentState, safePosition, safeDuration, "progress-poll");
        SignalEndIfProgressReachedDuration(slot, safePosition, safeDuration);
    }

    // Synthesizes the native MediaOpened event from the progress-poll signal. On the
    // Windows CommunityToolkit.Maui.MediaElement backend, MediaOpened (and often
    // StateChanged) do not fire reliably, so the initial Play() issued immediately after
    // setting Source is dropped while the pipeline is still loading. This helper detects
    // the first poll where the platform reports valid metadata for the new source
    // (duration > 0.5s and position near 0 while not yet Playing) and re-issues Play()
    // exactly once per source generation, eliminating the ~1.5s "paused first frame"
    // window caused by waiting for the startup-retry watchdog.
    private void ObserveSourceReadyForPlayback(VideoSlot slot, MediaElementState state, TimeSpan position, TimeSpan duration)
    {
        if (slot.SourceLoadAcknowledged)
        {
            return;
        }

        if (!slot.PlayWhenReady || slot.EndSignaled || slot.HasObservedPlayback || slot.CurrentPath is null)
        {
            return;
        }

        if (DateTimeOffset.UtcNow < slot.IgnoreEndedEventsUntil)
        {
            return;
        }

        if (state is not (MediaElementState.Paused or MediaElementState.Stopped))
        {
            return;
        }

        // Duration > 0.5s: platform has demuxed the new source's header.
        // Position < 0.5s: guards against a stale tail from the previous source still
        // being reported (observed at 09:18:53 in the diagnostic log).
        if (duration.TotalSeconds <= 0.5 || position.TotalSeconds >= 0.5)
        {
            return;
        }

        slot.SourceLoadAcknowledged = true;

        var lastRequest = slot.LastPlayRequestAt;
        var latencyMs = lastRequest.HasValue
            ? Math.Max(0d, (DateTimeOffset.UtcNow - lastRequest.Value).TotalMilliseconds)
            : 0d;

        debugLog.Trace($"Native video source ready; re-issuing play: slot={slot.Index}; state={state}; position={position.TotalSeconds:0.###}s; duration={duration.TotalSeconds:0.###}s; latency={latencyMs:0}ms; file={Path.GetFileName(slot.CurrentPath)}.");
        TryStartSlotPlayback(slot, _lastSettings, "source-loaded");
    }

    private void ObserveStartupState(VideoSlot slot, MediaElementState state, TimeSpan position, string reason)
    {
        if (!slot.PlayWhenReady || slot.EndSignaled || slot.CurrentPath is null || DateTimeOffset.UtcNow < slot.IgnoreEndedEventsUntil)
        {
            return;
        }

        if (state == MediaElementState.Playing)
        {
            slot.HasObservedPlayback = true;
            slot.StartupRetryCount = 0;
            slot.PausedStateStartedAt = null;
            // The new source is actually rendering frames now. This is the right
            // instant to bring the surface back: any reveal earlier than here would
            // expose either a still first-frame (Windows backend hands us a decoded
            // frame during Opening) or a black flash before the first real frame.
            TryRevealSlotLocked(slot, "playing-observed");
            return;
        }

        if (slot.HasObservedPlayback && state == MediaElementState.Stopped)
        {
            // This is an end-of-playback candidate; let the stopped-state completion path own it.
            return;
        }

        if (state is not (MediaElementState.Paused or MediaElementState.Stopped))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var stateStartedAt = state == MediaElementState.Paused
            ? slot.PausedStateStartedAt ?? now
            : slot.LastPlayRequestAt ?? now;

        if (state == MediaElementState.Paused)
        {
            slot.PausedStateStartedAt ??= stateStartedAt;
        }

        var lastPlayRequestAt = slot.LastPlayRequestAt ?? now;
        slot.LastPlayRequestAt ??= lastPlayRequestAt;

        // Retry 1 uses a shorter delay so the dropped initial Play() recovers faster on
        // backends where MediaOpened is silent. Subsequent retries use the longer delay to
        // avoid hammering a genuinely slow-loading source.
        var effectiveDelay = slot.StartupRetryCount == 0 ? FirstStartupRetryDelay : StartupRetryDelay;

        if (now - lastPlayRequestAt < effectiveDelay || now - stateStartedAt < effectiveDelay)
        {
            return;
        }

        if (slot.StartupRetryCount >= MaxStartupRetries)
        {
            debugLog.Error($"Native video stayed {state} while playback was expected after {MaxStartupRetries} retries; advancing slot={slot.Index}; reason={reason}; position={position.TotalSeconds:0.###}s; file={Path.GetFileName(slot.CurrentPath)}.");
            StopProgressTimer(slot);
            SignalEndReached(slot, "startup-timeout");
            return;
        }

        slot.StartupRetryCount++;
        slot.PausedStateStartedAt = state == MediaElementState.Paused ? now : null;
        debugLog.Info($"Native video remained {state} while playback was expected; retrying play: slot={slot.Index}; retry={slot.StartupRetryCount}/{MaxStartupRetries}; reason={reason}; position={position.TotalSeconds:0.###}s; file={Path.GetFileName(slot.CurrentPath)}.");
        TryStartSlotPlayback(slot, _lastSettings, $"startup-retry-{slot.StartupRetryCount}");
    }

    private void ObserveStoppedState(VideoSlot slot, MediaElementState state, TimeSpan position, TimeSpan duration, string reason)
    {
        if (!slot.PlayWhenReady || slot.EndSignaled || slot.CurrentPath is null || DateTimeOffset.UtcNow < slot.IgnoreEndedEventsUntil)
        {
            slot.StoppedStateStartedAt = null;
            return;
        }

        if (state == MediaElementState.Playing)
        {
            slot.HasObservedPlayback = true;
            slot.StoppedStateStartedAt = null;
            return;
        }

        if (state != MediaElementState.Stopped)
        {
            slot.StoppedStateStartedAt = null;
            return;
        }

        if (!slot.HasObservedPlayback)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;

        if (slot.StoppedStateStartedAt is null)
        {
            slot.StoppedStateStartedAt = now;
            debugLog.Trace($"Native video stopped without completion event; waiting before signaling end: slot={slot.Index}; reason={reason}; position={position.TotalSeconds:0.###}s; duration={duration.TotalSeconds:0.###}s; file={Path.GetFileName(slot.CurrentPath)}.");
            return;
        }

        if (now - slot.StoppedStateStartedAt.Value < StoppedStateCompletionGrace)
        {
            return;
        }

        debugLog.Info($"Native video stopped without MediaEnded; treating as complete: slot={slot.Index}; reason={reason}; position={position.TotalSeconds:0.###}s; duration={duration.TotalSeconds:0.###}s; file={Path.GetFileName(slot.CurrentPath)}.");
        StopProgressTimer(slot);
        SignalEndReached(slot, "state-stopped");
    }

    private void PublishProgressHeartbeat(VideoSlot slot, TimeSpan position, TimeSpan duration)
    {
        var now = DateTimeOffset.UtcNow;

        if ((now - slot.LastProgressLogAt).TotalSeconds < 5)
        {
            return;
        }

        slot.LastProgressLogAt = now;
        var state = slot.MediaElement?.CurrentState.ToString() ?? "Unknown";
        debugLog.Trace($"Native video progress: slot={slot.Index}; position={position.TotalSeconds:0.###}s; duration={duration.TotalSeconds:0.###}s; state={state}; generation={slot.PlaybackGeneration}; endArmed={slot.ProgressEndCheckArmed}; playWhenReady={slot.PlayWhenReady}; endSignaled={slot.EndSignaled}; file={Path.GetFileName(slot.CurrentPath)}.");
    }

    private void PublishProgressResetIfPrimary(VideoSlot slot)
    {
        if (slot.Index == 0)
        {
            ProgressChanged?.Invoke(0, 0);
        }
    }

    private void StartProgressTimer(VideoSlot slot)
    {
        StopProgressTimer(slot);
        var playbackGeneration = slot.PlaybackGeneration;
        PublishProgress(slot, playbackGeneration);
        slot.ProgressCancellation = new CancellationTokenSource();
        var cancellationToken = slot.ProgressCancellation.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(250, cancellationToken);
                    await MainThread.InvokeOnMainThreadAsync(() =>
                    {
                        if (!cancellationToken.IsCancellationRequested)
                        {
                            PublishProgress(slot, playbackGeneration);
                        }
                    });
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, cancellationToken);
    }

    private static void StopProgressTimer(VideoSlot slot)
    {
        slot.ProgressCancellation?.Cancel();
        slot.ProgressCancellation?.Dispose();
        slot.ProgressCancellation = null;
    }

    private void SignalEndIfProgressReachedDuration(VideoSlot slot, TimeSpan position, TimeSpan duration)
    {
        if (!slot.PlayWhenReady || slot.EndSignaled || duration.TotalSeconds <= 0.5)
        {
            return;
        }

        if (!slot.ProgressEndCheckArmed)
        {
            if (position.TotalSeconds > 0.05 && position.TotalSeconds < duration.TotalSeconds - 0.5)
            {
                slot.ProgressEndCheckArmed = true;
                debugLog.Trace($"Native video progress end-check armed: slot={slot.Index}; position={position.TotalSeconds:0.###}s; duration={duration.TotalSeconds:0.###}s.");
            }

            return;
        }

        if (position.TotalSeconds >= duration.TotalSeconds - 0.25)
        {
            debugLog.Trace($"Native video reached duration by progress polling: slot={slot.Index}; position={position.TotalSeconds:0.###}s; duration={duration.TotalSeconds:0.###}s.");
            StopProgressTimer(slot);
            SignalEndReached(slot, "progress-duration");
        }
    }

    private void SignalEndReached(VideoSlot slot, string reason)
    {
        if (slot.EndSignaled)
        {
            return;
        }

        slot.EndSignaled = true;
        slot.PlayWhenReady = false;
        var completedPath = slot.CurrentPath;
        var completedGeneration = slot.PlaybackGeneration;
        debugLog.Debug($"Native video completion signaled: slot={slot.Index}; generation={completedGeneration}; reason={reason}; file={Path.GetFileName(completedPath)}.");
        _ = Task.Run(() => EndReached?.Invoke(slot.Index, completedPath, completedGeneration));
    }

    private static async Task<bool> WaitForPlayableSurfaceAsync(VideoSlot slot)
    {
        try
        {
            await slot.ViewReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await slot.BoundsReady.Task.WaitAsync(TimeSpan.FromSeconds(2));
            return slot.HasPlayableBounds;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private bool TryGetSlot(int slotIndex, out VideoSlot slot)
    {
        if (slotIndex < 0 || slotIndex >= _slots.Length)
        {
            slot = _slots[0];
            return false;
        }

        slot = _slots[slotIndex];
        return true;
    }

    private sealed class VideoSlot(int index)
    {
        public int Index { get; } = index;

        public TaskCompletionSource ViewReady { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource BoundsReady { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public MediaElement? MediaElement { get; set; }

        public string? CurrentPath { get; set; }

        public MediaItem? PendingItem { get; set; }

        public PlaybackSettings? PendingSettings { get; set; }

        public bool PendingRestartIfSameSource { get; set; }

        public CancellationTokenSource? ProgressCancellation { get; set; }

        public Rect LastBounds { get; set; } = HiddenBounds;

        public bool IsVisible { get; set; }

        public bool HasPlayableBounds { get; set; }

        public bool PlayWhenReady { get; set; }

        public bool EndSignaled { get; set; }

        public bool SuppressNextEndedEvent { get; set; }

        public bool ProgressEndCheckArmed { get; set; }

        public bool HasObservedPlayback { get; set; }

        // True after we have observed the platform reporting valid metadata for the
        // currently-assigned source (duration > 0.5s while position is near 0). Used to
        // synthesize the missing MediaOpened event on backends (notably Windows) where
        // that native event does not fire reliably, so we can re-issue Play() the moment
        // the source is actually ready instead of waiting for the startup-retry watchdog.
        public bool SourceLoadAcknowledged { get; set; }

        // Set when PlayVideoSlotAsync starts a TRUE source change (not same-source
        // restart). The MediaElement stays at Opacity=0/HiddenBounds during the open
        // window so the user never sees the "first decoded frame, still paused"
        // artifact. Cleared by TryRevealSlotLocked when MediaElementState reaches
        // Playing for this generation, or by StopSlotAsync if the slot is torn down
        // before that happens.
        public bool PendingReveal { get; set; }

        public int PlaybackGeneration { get; set; }

        public int PlayRequestGeneration { get; set; }

        public int StartupRetryCount { get; set; }

        public DateTimeOffset LastProgressLogAt { get; set; } = DateTimeOffset.MinValue;

        public DateTimeOffset IgnoreEndedEventsUntil { get; set; } = DateTimeOffset.MinValue;

        public DateTimeOffset? StoppedStateStartedAt { get; set; }

        public DateTimeOffset? LastPlayRequestAt { get; set; }

        public DateTimeOffset? PausedStateStartedAt { get; set; }
    }
}
