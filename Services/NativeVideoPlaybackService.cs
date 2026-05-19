using System.Runtime.InteropServices;
using CommunityToolkit.Maui.Core;
using CommunityToolkit.Maui.Views;
using MauiMediaPlayer.Models;

namespace MauiMediaPlayer.Services;

public sealed class NativeVideoPlaybackService(DebugLogService debugLog, VideoMetadataService videoMetadata)
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
    // Shared watchdog interval — per-slot 250 ms polling caused ~12 WinRT property reads/sec
    // and flooded the debugger with first-chance COMException lines even when handled.
    private static readonly TimeSpan PlaybackWatchdogInterval = TimeSpan.FromSeconds(1);
    private const int StalledProgressPollsBeforeEnd = 8;
    private const int StalledProgressPollsBeforeChromeReconcile = 2;
    private const int StalledProgressPollsBeforePlayRetry = 5;
    private static readonly Rect HiddenBounds = new(-10000, -10000, 1, 1);
    private readonly VideoSlot[] _slots = Enumerable.Range(0, MaxSlots).Select(index => new VideoSlot(index)).ToArray();
    private PlaybackSettings _lastSettings = new();
    private CancellationTokenSource? _playbackWatchdogCancellation;

    public event Action<double, double>? ProgressChanged;

    public event Action<int, string?, int, string>? EndReached;

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

                var openedDuration = TryReadDuration(slot.MediaElement);
                debugLog.Info($"Native video media opened: slot={slot.Index}; file={Path.GetFileName(slot.CurrentPath)}; duration={openedDuration}.");
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
            _ = MainThread.InvokeOnMainThreadAsync(() => PublishProgress(slot, slot.PlaybackGeneration, args.Position, "position-changed"));
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

                if (slot.MediaElement is not null && slot.CurrentPath is not null
                    && TryReadPlaybackSnapshot(slot.MediaElement, out var position, out var elementDuration, out var state))
                {
                    var effectiveDuration = GetEffectiveDuration(slot, elementDuration);
                    ObserveSourceReadyForPlayback(slot, state, position, elementDuration);
                    ObserveStartupState(slot, state, position, "state-changed");
                    ObserveStoppedState(slot, state, position, effectiveDuration, elementDuration, "state-changed");
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
        var boundsUnchanged = slot.LastBounds == bounds;
        var needsChromeReconcile = slot.BoundsResyncPending || NeedsChromeReconciliation(slot, hasPlayableBounds, _overlaySuppressed);

        if (boundsUnchanged && !needsChromeReconcile)
        {
            return;
        }

        if (!boundsUnchanged)
        {
            slot.LastBounds = bounds;
            slot.HasPlayableBounds = hasPlayableBounds;
        }

        slot.BoundsResyncPending = false;

        if (!boundsUnchanged)
        {
            if (hasPlayableBounds)
            {
                slot.BoundsReady.TrySetResult();
            }
            else
            {
                slot.BoundsReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
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
            TrySetMediaElementChrome(
                slot.MediaElement,
                shouldExposeBounds ? bounds : HiddenBounds,
                shouldExposeBounds ? 1 : 0);

            // Bounds-arrives-after-Playing race: if the slot already observed Playing
            // while the JS bounds observer was still settling (initial layout, resize,
            // split-mode toggle), TryRevealSlotLocked refused to reveal because
            // HasPlayableBounds was still false. Now that we have real bounds, finish
            // the reveal here so the slot doesn't sit hidden forever.
            if (slot.PendingReveal && slot.HasObservedPlayback)
            {
                TryRevealSlotLocked(slot, "bounds-ready-after-playing");
            }
            else
            {
                ReconcileSlotChromeLocked(slot, "bounds-ready");
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

    // Fullscreen / split-layout transitions can leave MediaElements at HiddenBounds
    // while LastBounds still matches the live pane rect. The next bounds observer
    // callback would otherwise no-op and the slot stays invisible on a black pane.
    public void RequestLayoutBoundsResync()
    {
        foreach (var slot in _slots)
        {
            slot.BoundsResyncPending = true;
        }
    }

    private static bool NeedsChromeReconciliation(VideoSlot slot, bool hasPlayableBounds, bool overlaySuppressed)
    {
        if (!hasPlayableBounds)
        {
            return false;
        }

        return (slot.PendingReveal && slot.HasObservedPlayback)
            || (slot.IsVisible && !overlaySuppressed);
    }

    public async Task ShowAsync(int slotIndex)
    {
        if (!TryGetSlot(slotIndex, out var slot))
        {
            return;
        }

        if (!slot.ViewReady.Task.IsCompletedSuccessfully || !slot.HasPlayableBounds)
        {
            return;
        }

        if (!slot.IsVisible)
        {
            slot.IsVisible = true;
        }

        if (_overlaySuppressed)
        {
            // Same-source replay / loop arrived during a modal. Logical visibility is
            // restored but pixels stay suppressed; SuppressForOverlayAsync(false) will
            // expose them when the modal closes.
            return;
        }

        await MainThread.InvokeOnMainThreadAsync(() => ReconcileSlotChromeLocked(slot, "show-async"));
    }

    public Task ReconcilePlaybackSurfacesAsync()
    {
        return MainThread.InvokeOnMainThreadAsync(() =>
        {
            foreach (var slot in _slots)
            {
                if (slot.PlayWhenReady && slot.CurrentPath is not null)
                {
                    ReconcileSlotChromeLocked(slot, "playback-reconcile");
                }
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
                TrySetMediaElementChrome(slot.MediaElement, HiddenBounds, 0);
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

        TrySetMediaElementChrome(slot.MediaElement, slot.LastBounds, 1);
        debugLog.Trace($"Native video reveal: slot={slot.Index}; reason={reason}; generation={slot.PlaybackGeneration}; file={Path.GetFileName(slot.CurrentPath)}.");
    }

    // Re-applies native bounds/opacity when logical state says a slot should be visible
    // but the SwapChainPanel was left at HiddenBounds (common after image/video churn or
    // a bounds observer no-op). Safe to call frequently; cheap when already correct.
    private void ReconcileSlotChromeLocked(VideoSlot slot, string reason)
    {
        if (slot.MediaElement is null || !slot.HasPlayableBounds || _overlaySuppressed)
        {
            return;
        }

        if (slot.PendingReveal && slot.HasObservedPlayback)
        {
            TryRevealSlotLocked(slot, reason);
            return;
        }

        if (slot.IsVisible && slot.PlayWhenReady && !slot.EndSignaled)
        {
            TrySetMediaElementChrome(slot.MediaElement, slot.LastBounds, 1);
        }
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
                    TrySetMediaElementChrome(slot.MediaElement, HiddenBounds, 0);
                }
                else
                {
                    if (slot.IsVisible && slot.HasPlayableBounds)
                    {
                        TrySetMediaElementChrome(slot.MediaElement, slot.LastBounds, 1);
                    }
                    else if (slot.PlayWhenReady
                        && (slot.PendingReveal || slot.HasObservedPlayback)
                        && TryReadPlaybackSnapshot(slot.MediaElement, out _, out _, out var overlayState)
                        && overlayState == MediaElementState.Playing)
                    {
                        TryRevealSlotLocked(slot, "overlay-unsuppressed");
                    }
                }
            }

            if (!suppress)
            {
                foreach (var slot in _slots)
                {
                    if (slot.PlayWhenReady && !slot.EndSignaled && slot.MediaElement is not null)
                    {
                        PublishProgress(slot, slot.PlaybackGeneration, reason: "overlay-unsuppressed");
                    }
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

            // Stagger concurrent source opens in split layouts so the WinUI
            // MediaElement backend is less likely to drop Play() on a later slot.
            if (item?.Kind == MediaKind.Video)
            {
                for (var later = index + 1; later < items.Count; later++)
                {
                    if (items[later]?.Kind == MediaKind.Video)
                    {
                        await Task.Delay(75);
                        break;
                    }
                }
            }
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

        if (!TryGetSlot(slotIndex, out var slot))
        {
            return;
        }

        // Image/GIF slots that were already idle skip Stop/ClearValue — every image
        // swap was tearing down the native surface and nudging sibling video panes.
        var needsTeardown = slot.IsVisible || slot.PlayWhenReady || slot.CurrentPath is not null;
        if (!needsTeardown)
        {
            ClearPendingSlotPlayback(slot);
            return;
        }

        debugLog.Trace(item is null
            ? $"Native slot {slotIndex} cleared; no media item assigned."
            : $"Native slot {slotIndex} stopping video surface for {item.DisplayName} ({item.Kind}).");
        ClearPendingSlotPlayback(slot);

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
            slot.StalledProgressPollCount = 0;
            slot.LastProgressPositionSeconds = -1;
            StopProgressTimer(slot);
        }

        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            foreach (var slot in _slots)
            {
                TryInvokeMediaElement(slot.MediaElement, static element => element.Pause());
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

            try
            {
                if (primarySlot.MediaElement is not null
                    && TryReadPlaybackSnapshot(primarySlot.MediaElement, out _, out _, out var seekState)
                    && seekState == MediaElementState.Opening)
                {
                    return;
                }

                await primarySlot.MediaElement.SeekTo(TimeSpan.FromSeconds(Math.Max(0, seconds)), CancellationToken.None);
                PublishProgress(primarySlot);
            }
            catch (COMException exception)
            {
                debugLog.Trace($"Native video seek deferred: {exception.Message}");
            }
            catch (InvalidOperationException exception)
            {
                debugLog.Trace($"Native video seek deferred: {exception.Message}");
            }
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

        // Keep the outgoing video visible while the next file opens. Hiding exposed the
        // HTML spinner underlay for ~250–500ms on every swap; TryRevealSlotLocked still
        // gates the first frame of the new source until Playing is observed.
        await MainThread.InvokeOnMainThreadAsync(async () =>
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
                TryInvokeMediaElement(slot.MediaElement, element =>
                {
                    element.Stop();
                    // Clear the previous file so the decoder cannot surface its last
                    // frame when this slot is revealed after PendingReveal.
                    element.ClearValue(MediaElement.SourceProperty);
                    element.Source = MediaSource.FromFile(item.FilePath);
                });
                slot.CurrentPath = item.FilePath;
                slot.StalledProgressPollCount = 0;
                slot.LastProgressPositionSeconds = -1;
                // Probe after the native pipeline has had time to open the file (TagLib and
                // MediaElement both touch the path; probing immediately caused IOException).
                var probePath = item.FilePath;
                _ = videoMetadata.ProbeDurationAsync(probePath);
                _ = Task.Run(async () =>
                {
                    await Task.Delay(750);
                    await videoMetadata.ProbeDurationAsync(probePath);
                });
                PublishProgressResetIfPrimary(slot);
                var reason = isSourceChange ? "source-change" : "same-source-restart";
                debugLog.Info($"Native video media loaded: slot={slot.Index}; generation={generation}; reason={reason}; file={item.DisplayName}; path={item.FilePath}");
                PlaybackStarted?.Invoke(slot.Index, item.FilePath, generation);
                TryStartSlotPlayback(slot, settings, reason);
            }
            else
            {
                slot.EndSignaled = false;
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

                // Resume after pause (or a same-source refresh) while still parked on the
                // last frame: the next progress poll would immediately signal completion.
                TimeSpan cachedDuration = default;
                var hasCachedDuration = slot.CurrentPath is not null
                    && videoMetadata.TryGetDuration(slot.CurrentPath, out cachedDuration)
                    && cachedDuration.TotalSeconds > 0.5;
                var resumeAtEnd = slot.MediaElement is not null
                    && TryReadPlaybackSnapshot(slot.MediaElement, out var resumePosition, out var resumeDuration, out _)
                    && (
                        (resumeDuration.TotalSeconds > 0.5
                            && resumePosition.TotalSeconds >= resumeDuration.TotalSeconds - 0.5)
                        || (hasCachedDuration
                            && resumePosition.TotalSeconds >= cachedDuration.TotalSeconds - 0.5));

                slot.StalledProgressPollCount = 0;
                slot.LastProgressPositionSeconds = -1;
                slot.StoppedStateStartedAt = null;

                if (resumeAtEnd && slot.MediaElement is not null)
                {
                    await slot.MediaElement.SeekTo(TimeSpan.Zero, CancellationToken.None);
                    slot.ProgressEndCheckArmed = false;
                }
                else if (slot.MediaElement is not null
                    && TryReadPlaybackSnapshot(slot.MediaElement, out var frozenPosition, out var frozenDuration, out _)
                    && frozenDuration.TotalSeconds <= 0.5
                    && hasCachedDuration
                    && frozenPosition.TotalSeconds < cachedDuration.TotalSeconds - 1.5)
                {
                    var seekTarget = frozenPosition.TotalSeconds > 0.2 ? frozenPosition : TimeSpan.Zero;
                    await slot.MediaElement.SeekTo(seekTarget, CancellationToken.None);
                    slot.ProgressEndCheckArmed = false;
                }
                else
                {
                    slot.ProgressEndCheckArmed = true;
                }

                TryStartSlotPlayback(slot, settings, "same-source");
                ReconcileSlotChromeLocked(slot, "same-source");
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
            TryInvokeMediaElement(slot.MediaElement, static element =>
            {
                element.Stop();
                element.ClearValue(MediaElement.SourceProperty);
            });
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

        TryInvokeMediaElement(slot.MediaElement, element =>
        {
            element.Speed = settings.PlaybackSpeed;
            element.Volume = Math.Clamp(settings.Volume, 0, 1);
            element.ShouldMute = settings.Muted;
        });
    }

    private void TryStartSlotPlayback(VideoSlot slot, PlaybackSettings settings, string reason)
    {
        if (slot.MediaElement is null)
        {
            return;
        }

        ApplySettings(slot, settings);
        slot.StoppedStateStartedAt = null;
        slot.LastPlayRequestAt = DateTimeOffset.UtcNow;

        if (!TryInvokeMediaElement(slot.MediaElement, static element => element.Play()))
        {
            var retryNote = reason == "media-opened"
                ? "no further native retry is expected"
                : "waiting for media-opened retry";
            debugLog.Trace($"Native video play deferred for slot {slot.Index} ({reason}); {retryNote}; file={Path.GetFileName(slot.CurrentPath)}.");

            if (reason == "media-opened")
            {
                // ShouldAutoPlay + source-loaded retry will recover; don't advance the slot.
            }

            return;
        }

        StartProgressTimer(slot);
        debugLog.Trace($"Native video play requested: slot={slot.Index}; reason={reason}; file={Path.GetFileName(slot.CurrentPath)}.");
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

    private void PublishProgress(VideoSlot slot, int playbackGeneration, TimeSpan? position = null, string reason = "progress-poll")
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

        TimeSpan safePosition;
        TimeSpan elementDuration;
        MediaElementState currentState;

        if (TryReadPlaybackSnapshot(slot.MediaElement, out var polledPosition, out elementDuration, out currentState))
        {
            safePosition = position ?? polledPosition;
            slot.CacheSnapshot(safePosition, elementDuration, currentState);
        }
        else if (position.HasValue && slot.HasCachedSnapshot)
        {
            safePosition = position.Value;
            elementDuration = slot.CachedDuration;
            currentState = slot.CachedState;
        }
        else
        {
            return;
        }

        var safeDuration = GetEffectiveDuration(slot, elementDuration);

        if (Math.Abs(safePosition.TotalSeconds - slot.LastProgressPositionSeconds) < 0.05)
        {
            slot.StalledProgressPollCount++;
        }
        else
        {
            slot.StalledProgressPollCount = 0;
            slot.LastProgressPositionSeconds = safePosition.TotalSeconds;
        }

        if (slot.Index == 0)
        {
            var displayDuration = safeDuration.TotalSeconds > 0.5 ? safeDuration : elementDuration;
            ProgressChanged?.Invoke(
                Math.Max(0, safePosition.TotalSeconds),
                Math.Max(0, displayDuration.TotalSeconds));
        }

        PublishProgressHeartbeat(slot, safePosition, elementDuration, currentState);
        // Synthesized-open must run before the startup-state watchdog so the latter sees
        // the bumped LastPlayRequestAt and waits its full first-retry window before
        // firing a redundant retry. Order matters here.
        ObserveSourceReadyForPlayback(slot, currentState, safePosition, safeDuration);
        ObserveStartupState(slot, currentState, safePosition, reason);
        if (elementDuration.TotalSeconds <= 0.5 && safePosition.TotalSeconds >= 0.25)
        {
            TryScheduleDurationProbe(slot);
        }

        ObserveStoppedState(slot, currentState, safePosition, safeDuration, elementDuration, reason);
        ObserveStalledPlayback(slot, currentState, safePosition, safeDuration, reason);
        SignalEndIfProgressReachedDuration(slot, safePosition, safeDuration);
    }

    private void ObserveStalledPlayback(VideoSlot slot, MediaElementState state, TimeSpan position, TimeSpan duration, string reason)
    {
        if (!slot.PlayWhenReady || slot.EndSignaled || slot.CurrentPath is null || !slot.HasObservedPlayback)
        {
            return;
        }

        if (state != MediaElementState.Playing || slot.StalledProgressPollCount < StalledProgressPollsBeforeChromeReconcile)
        {
            return;
        }

        ReconcileSlotChromeLocked(slot, $"stall-reconcile-{reason}");

        if (slot.StalledProgressPollCount < StalledProgressPollsBeforePlayRetry
            || slot.StalledProgressPollCount % StalledProgressPollsBeforePlayRetry != 0)
        {
            return;
        }

        if (IsPositionBeforeKnownEnd(slot, position))
        {
            TryRecoverStalledMidFilePlayback(slot, position, reason);
            return;
        }

        debugLog.Trace($"Native video progress stalled; retrying play: slot={slot.Index}; polls={slot.StalledProgressPollCount}; position={position.TotalSeconds:0.###}s; file={Path.GetFileName(slot.CurrentPath)}.");
        TryStartSlotPlayback(slot, _lastSettings, "stall-recovery");
    }

    private static bool TryReadPlaybackSnapshot(
        MediaElement element,
        out TimeSpan position,
        out TimeSpan duration,
        out MediaElementState state)
    {
        position = TimeSpan.Zero;
        duration = TimeSpan.Zero;
        state = MediaElementState.None;

        try
        {
            state = element.CurrentState;

            // WinRT throws COMException if Position/Duration are read while Opening.
            if (state is MediaElementState.Opening or MediaElementState.Buffering)
            {
                return false;
            }

            position = element.Position;
            duration = element.Duration;
            return true;
        }
        catch (COMException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static TimeSpan TryReadDuration(MediaElement element)
    {
        return TryReadPlaybackSnapshot(element, out _, out var duration, out _)
            ? duration
            : TimeSpan.Zero;
    }

    private static bool TryInvokeMediaElement(MediaElement? element, Action<MediaElement> action)
    {
        if (element is null)
        {
            return false;
        }

        try
        {
            action(element);
            return true;
        }
        catch (COMException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool TrySetMediaElementChrome(MediaElement element, Rect bounds, double opacity)
    {
        try
        {
            AbsoluteLayout.SetLayoutBounds(element, bounds);
            element.Opacity = opacity;
            return true;
        }
        catch (COMException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private TimeSpan GetEffectiveDuration(VideoSlot slot, TimeSpan elementDuration)
    {
        if (elementDuration.TotalSeconds > 0.5)
        {
            return elementDuration;
        }

        if (slot.CurrentPath is not null
            && videoMetadata.TryGetDuration(slot.CurrentPath, out var cached)
            && cached.TotalSeconds > 0.5)
        {
            return cached;
        }

        return elementDuration;
    }

    private bool IsPositionBeforeKnownEnd(VideoSlot slot, TimeSpan position)
    {
        if (slot.CurrentPath is null)
        {
            return false;
        }

        if (!videoMetadata.TryGetDuration(slot.CurrentPath, out var cached) || cached.TotalSeconds <= 0.5)
        {
            return false;
        }

        return position.TotalSeconds < cached.TotalSeconds - 1.5;
    }

    private void TryRecoverStalledMidFilePlayback(VideoSlot slot, TimeSpan position, string reason)
    {
        if (slot.MediaElement is null || slot.CurrentPath is null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;

        if (now - slot.LastMidFileRecoveryAt < TimeSpan.FromSeconds(2))
        {
            return;
        }

        slot.LastMidFileRecoveryAt = now;
        slot.StoppedStateStartedAt = null;
        var seekTarget = position.TotalSeconds > 0.2 ? position : TimeSpan.Zero;
        debugLog.Trace($"Native video mid-file stall recovery: slot={slot.Index}; reason={reason}; seek={seekTarget.TotalSeconds:0.###}s; polls={slot.StalledProgressPollCount}; file={Path.GetFileName(slot.CurrentPath)}.");
        TryScheduleDurationProbe(slot);

        _ = MainThread.InvokeOnMainThreadAsync(async () =>
        {
            if (slot.MediaElement is null || slot.EndSignaled || !slot.PlayWhenReady)
            {
                return;
            }

            try
            {
                await slot.MediaElement.SeekTo(seekTarget, CancellationToken.None);
            }
            catch (COMException exception)
            {
                debugLog.Trace($"Native video mid-file seek deferred: slot={slot.Index}; {exception.Message}");
            }
            catch (InvalidOperationException exception)
            {
                debugLog.Trace($"Native video mid-file seek deferred: slot={slot.Index}; {exception.Message}");
            }

            slot.StalledProgressPollCount = 0;
            slot.LastProgressPositionSeconds = -1;
            TryStartSlotPlayback(slot, _lastSettings, "mid-file-stall-recovery");
        });
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

    private void ObserveStoppedState(VideoSlot slot, MediaElementState state, TimeSpan position, TimeSpan effectiveDuration, TimeSpan elementDuration, string reason)
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

        if (position.TotalSeconds > 0.5)
        {
            slot.HasObservedPlayback = true;
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

        // Duration unknown (common for WebM / slow metadata). Ignore spurious Stopped
        // at the start of open, but allow end-of-file when position shows real playback.
        if (effectiveDuration.TotalSeconds <= 0.5 && position.TotalSeconds < 1.0)
        {
            slot.StoppedStateStartedAt = null;
            return;
        }

        if (effectiveDuration.TotalSeconds > 0.5 && position.TotalSeconds < effectiveDuration.TotalSeconds - 1.0)
        {
            slot.StoppedStateStartedAt = null;
            return;
        }

        var hasReliableDuration = effectiveDuration.TotalSeconds > 0.5;

        var nearCachedEnd = !hasReliableDuration
            && slot.CurrentPath is not null
            && videoMetadata.TryGetDuration(slot.CurrentPath, out var cachedDuration)
            && cachedDuration.TotalSeconds > 0.5
            && position.TotalSeconds >= cachedDuration.TotalSeconds - 1.0;

        if (!hasReliableDuration && IsPositionBeforeKnownEnd(slot, position))
        {
            slot.StoppedStateStartedAt = null;
            TryScheduleDurationProbe(slot);

            if (slot.StalledProgressPollCount >= StalledProgressPollsBeforePlayRetry)
            {
                TryRecoverStalledMidFilePlayback(slot, position, reason);
            }

            return;
        }

        // Unknown duration (WebM): prefer TagLib near EOF. If metadata is still missing, allow
        // completion only while Stopped with a frozen position (not mid-playback Playing).
        var stalledUnknownEnd = !hasReliableDuration
            && !nearCachedEnd
            && !IsPositionBeforeKnownEnd(slot, position)
            && slot.StalledProgressPollCount >= StalledProgressPollsBeforeEnd
            && position.TotalSeconds >= 1.0;

        if (!hasReliableDuration && !nearCachedEnd && !stalledUnknownEnd)
        {
            slot.StoppedStateStartedAt = null;
            return;
        }

        var now = DateTimeOffset.UtcNow;

        if (slot.StoppedStateStartedAt is null)
        {
            slot.StoppedStateStartedAt = now;
            debugLog.Trace($"Native video stopped without completion event; waiting before signaling end: slot={slot.Index}; reason={reason}; position={position.TotalSeconds:0.###}s; duration={elementDuration.TotalSeconds:0.###}s; effective={effectiveDuration.TotalSeconds:0.###}s; cachedEnd={nearCachedEnd}; file={Path.GetFileName(slot.CurrentPath)}.");
            return;
        }

        if (now - slot.StoppedStateStartedAt.Value < StoppedStateCompletionGrace)
        {
            return;
        }

        debugLog.Info($"Native video stopped without MediaEnded; treating as complete: slot={slot.Index}; reason={reason}; position={position.TotalSeconds:0.###}s; duration={elementDuration.TotalSeconds:0.###}s; effective={effectiveDuration.TotalSeconds:0.###}s; cachedEnd={nearCachedEnd}; file={Path.GetFileName(slot.CurrentPath)}.");
        StopProgressTimer(slot);
        SignalEndReached(slot, "state-stopped");
    }

    private void PublishProgressHeartbeat(VideoSlot slot, TimeSpan position, TimeSpan duration, MediaElementState state)
    {
        var now = DateTimeOffset.UtcNow;

        if ((now - slot.LastProgressLogAt).TotalSeconds < 5)
        {
            return;
        }

        slot.LastProgressLogAt = now;
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
        slot.WatchdogActive = true;
        PublishProgress(slot, slot.PlaybackGeneration, reason: "playback-start");
        EnsurePlaybackWatchdog();
    }

    private void StopProgressTimer(VideoSlot slot)
    {
        slot.WatchdogActive = false;
        slot.HasCachedSnapshot = false;

        if (_slots.All(static s => !s.WatchdogActive))
        {
            _playbackWatchdogCancellation?.Cancel();
        }
    }

    private void EnsurePlaybackWatchdog()
    {
        if (_playbackWatchdogCancellation is not null)
        {
            return;
        }

        var watchdog = new CancellationTokenSource();
        _playbackWatchdogCancellation = watchdog;
        var cancellationToken = watchdog.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(PlaybackWatchdogInterval, cancellationToken);

                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    await MainThread.InvokeOnMainThreadAsync(RunPlaybackWatchdogTick);
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                if (ReferenceEquals(_playbackWatchdogCancellation, watchdog))
                {
                    watchdog.Dispose();
                    _playbackWatchdogCancellation = null;
                }
            }
        }, cancellationToken);
    }

    private void RunPlaybackWatchdogTick()
    {
        var anyActive = false;

        foreach (var slot in _slots)
        {
            if (!slot.PlayWhenReady || slot.EndSignaled || slot.MediaElement is null || slot.CurrentPath is null)
            {
                continue;
            }

            anyActive = true;

            if (slot.WatchdogActive || SlotNeedsWatchdogPoll(slot))
            {
                PublishProgress(slot, slot.PlaybackGeneration, reason: "watchdog");
            }
            else
            {
                ReconcileSlotChromeLocked(slot, "watchdog-idle");
            }
        }

        if (!anyActive)
        {
            _playbackWatchdogCancellation?.Cancel();
        }
    }

    private static bool SlotNeedsWatchdogPoll(VideoSlot slot)
    {
        if (!slot.SourceLoadAcknowledged
            || (!slot.HasObservedPlayback && slot.StartupRetryCount <= MaxStartupRetries))
        {
            return true;
        }

        return SlotHasUnreliableElementDuration(slot);
    }

    private static bool SlotHasUnreliableElementDuration(VideoSlot slot)
    {
        if (slot.MediaElement is null)
        {
            return false;
        }

        if (TryReadPlaybackSnapshot(slot.MediaElement, out _, out var elementDuration, out _))
        {
            return elementDuration.TotalSeconds <= 0.5;
        }

        return slot.HasCachedSnapshot && slot.CachedDuration.TotalSeconds <= 0.5;
    }

    private void TryScheduleDurationProbe(VideoSlot slot)
    {
        if (slot.CurrentPath is null)
        {
            return;
        }

        if (videoMetadata.TryGetDuration(slot.CurrentPath, out var cached) && cached.TotalSeconds > 0.5)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (now - slot.LastDurationProbeAt < TimeSpan.FromSeconds(2))
        {
            return;
        }

        slot.LastDurationProbeAt = now;
        _ = videoMetadata.ProbeDurationAsync(slot.CurrentPath);
    }

    private void SignalEndIfProgressReachedDuration(VideoSlot slot, TimeSpan position, TimeSpan duration)
    {
        if (!slot.PlayWhenReady || slot.EndSignaled || !slot.HasObservedPlayback)
        {
            return;
        }

        if (duration.TotalSeconds <= 0.5)
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
        _ = Task.Run(() => EndReached?.Invoke(slot.Index, completedPath, completedGeneration, reason));
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

        public bool WatchdogActive { get; set; }

        public bool HasCachedSnapshot { get; set; }

        public TimeSpan CachedDuration { get; set; }

        public MediaElementState CachedState { get; set; }

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

        public bool BoundsResyncPending { get; set; }

        public int PlaybackGeneration { get; set; }

        public int PlayRequestGeneration { get; set; }

        public int StartupRetryCount { get; set; }

        public DateTimeOffset LastProgressLogAt { get; set; } = DateTimeOffset.MinValue;

        public DateTimeOffset IgnoreEndedEventsUntil { get; set; } = DateTimeOffset.MinValue;

        public DateTimeOffset? StoppedStateStartedAt { get; set; }

        public int StalledProgressPollCount { get; set; }

        public DateTimeOffset LastDurationProbeAt { get; set; } = DateTimeOffset.MinValue;

        public double LastProgressPositionSeconds { get; set; } = -1;

        public DateTimeOffset? LastPlayRequestAt { get; set; }

        public DateTimeOffset LastMidFileRecoveryAt { get; set; } = DateTimeOffset.MinValue;

        public DateTimeOffset? PausedStateStartedAt { get; set; }

        public void CacheSnapshot(TimeSpan position, TimeSpan duration, MediaElementState state)
        {
            HasCachedSnapshot = true;
            CachedDuration = duration;
            CachedState = state;
        }
    }
}
