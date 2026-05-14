namespace MauiMediaPlayer.Models;

public sealed class PlaybackSettings
{
    public const double MinPlaybackSpeed = 0.25;
    public const double MaxPlaybackSpeed = 3.0;
    public const double DefaultPlaybackSpeed = 1.0;
    public const double MinImageDurationSeconds = 1.0;
    public const double MaxImageDurationSeconds = 60.0;
    public const double DefaultImageDurationSeconds = 5.0;
    public const double RandomizedSideImageMinSeconds = 5.0;
    public const double RandomizedSideImageMaxSeconds = 10.0;
    public const int MinGifRepeatCount = 1;
    public const int MaxGifRepeatCount = 100;
    public const int DefaultGifRepeatCount = 5;

    public double PlaybackSpeed { get; set; } = DefaultPlaybackSpeed;

    public double ImageDurationSeconds { get; set; } = DefaultImageDurationSeconds;

    public int GifRepeatCount { get; set; } = DefaultGifRepeatCount;

    public bool Shuffle { get; set; }

    public LoopMode LoopMode { get; set; } = LoopMode.All;

    public bool RecursiveFolderScan { get; set; } = true;

    public double Volume { get; set; } = 1.0;

    public bool Muted { get; set; }

    public SplitScreenMode SplitScreenMode { get; set; } = SplitScreenMode.Single;

    public int SplitScreenCount => Math.Clamp((int)SplitScreenMode, 1, 3);

    public bool AlwaysShowVideoInSplit { get; set; }

    public bool IsolateVideoToCenterPanel { get; set; }

    public bool IsVideoIsolationActive =>
        AlwaysShowVideoInSplit
        && IsolateVideoToCenterPanel
        && SplitScreenMode == SplitScreenMode.TriSplit;

    public bool RequiresAtLeastOneVideo =>
        AlwaysShowVideoInSplit
        && SplitScreenCount > 1
        && !IsVideoIsolationActive;

    public MediaKindRequirement GetSlotKindRequirement(int slotIndex)
    {
        if (!IsVideoIsolationActive)
        {
            return MediaKindRequirement.Any;
        }

        return slotIndex == 1 ? MediaKindRequirement.VideoOnly : MediaKindRequirement.NonVideoOnly;
    }

    // When isolation is active in tri-split, slots 0 and 2 are the non-video side panes.
    // Randomizing their durations independently prevents their timers from firing on the
    // same boundary, so they don't load and swap in lockstep while the center video plays.
    public bool ShouldRandomizeImageDurationForSlot(int slotIndex) =>
        IsVideoIsolationActive && slotIndex != 1;

    public TimeSpan GetAdjustedImageDuration()
    {
        var safeSpeed = Math.Clamp(PlaybackSpeed, MinPlaybackSpeed, MaxPlaybackSpeed);
        var safeDuration = Math.Clamp(ImageDurationSeconds, MinImageDurationSeconds, MaxImageDurationSeconds);

        return TimeSpan.FromSeconds(safeDuration / safeSpeed);
    }

    public TimeSpan GetRandomizedSideImageDuration(Random random, out double rawSeconds)
    {
        ArgumentNullException.ThrowIfNull(random);

        var safeSpeed = Math.Clamp(PlaybackSpeed, MinPlaybackSpeed, MaxPlaybackSpeed);
        rawSeconds = RandomizedSideImageMinSeconds
            + random.NextDouble() * (RandomizedSideImageMaxSeconds - RandomizedSideImageMinSeconds);

        return TimeSpan.FromSeconds(rawSeconds / safeSpeed);
    }
}
