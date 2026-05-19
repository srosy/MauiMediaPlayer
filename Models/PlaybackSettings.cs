using System.Globalization;
using System.Text;

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
    public const int MinFavoriteShuffleWeight = 1;
    public const int MaxFavoriteShuffleWeight = 5;
    public const int DefaultFavoriteShuffleWeight = 3;

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

    public SideAdvanceMode SideAdvanceMode { get; set; } = SideAdvanceMode.Independent;

    public bool SharedSideShuffleBag { get; set; }

    public PlaylistSource PlaylistSource { get; set; } = PlaylistSource.AllLibrary;

    public bool BoostFavoritesInShuffle { get; set; } = true;

    public int FavoriteShuffleWeight { get; set; } = DefaultFavoriteShuffleWeight;

    public PlaybackMediaKinds EnabledMediaKinds { get; set; } = PlaybackMediaKinds.All;

    public bool IsMediaKindEnabled(MediaKind kind) => kind switch
    {
        MediaKind.Image => EnabledMediaKinds.HasFlag(PlaybackMediaKinds.Images),
        MediaKind.Video => EnabledMediaKinds.HasFlag(PlaybackMediaKinds.Videos),
        MediaKind.Gif => EnabledMediaKinds.HasFlag(PlaybackMediaKinds.Gifs),
        MediaKind.Unknown => IsAllMediaKindsEnabled,
        _ => false
    };

    public bool IsAllMediaKindsEnabled => (EnabledMediaKinds & PlaybackMediaKinds.All) == PlaybackMediaKinds.All;

    public static PlaybackMediaKinds NormalizeEnabledMediaKinds(PlaybackMediaKinds kinds) =>
        kinds == PlaybackMediaKinds.None ? PlaybackMediaKinds.All : kinds & PlaybackMediaKinds.All;

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

    /// <summary>
    /// Single-line snapshot of persisted settings plus derived playback behaviour for the debug log.
    /// </summary>
    public string FormatForDebugLog()
    {
        var adjustedImage = GetAdjustedImageDuration();
        var builder = new StringBuilder(384);
        builder.Append(CultureInfo.InvariantCulture, $"speed={PlaybackSpeed:0.##}");
        builder.Append(CultureInfo.InvariantCulture, $"; imageSeconds={ImageDurationSeconds:0.##}");
        builder.Append(CultureInfo.InvariantCulture, $"; adjustedImageDuration={adjustedImage.TotalSeconds:0.##}s");
        builder.Append(CultureInfo.InvariantCulture, $"; gifRepeats={GifRepeatCount}");
        builder.Append(CultureInfo.InvariantCulture, $"; loop={LoopMode}");
        builder.Append(CultureInfo.InvariantCulture, $"; shuffle={Shuffle}");
        builder.Append(CultureInfo.InvariantCulture, $"; playlistSource={PlaylistSource}");
        builder.Append(CultureInfo.InvariantCulture, $"; enabledMediaKinds={EnabledMediaKinds}");
        builder.Append(CultureInfo.InvariantCulture, $"; boostFavorites={BoostFavoritesInShuffle}");
        builder.Append(CultureInfo.InvariantCulture, $"; favoriteWeight={FavoriteShuffleWeight}");
        builder.Append(CultureInfo.InvariantCulture, $"; recursiveFolderScan={RecursiveFolderScan}");
        builder.Append(CultureInfo.InvariantCulture, $"; split={SplitScreenMode}");
        builder.Append(CultureInfo.InvariantCulture, $"; splitCount={SplitScreenCount}");
        builder.Append(CultureInfo.InvariantCulture, $"; alwaysShowVideo={AlwaysShowVideoInSplit}");
        builder.Append(CultureInfo.InvariantCulture, $"; isolateCenter={IsolateVideoToCenterPanel}");
        builder.Append(CultureInfo.InvariantCulture, $"; videoIsolationActive={IsVideoIsolationActive}");
        builder.Append(CultureInfo.InvariantCulture, $"; requiresAtLeastOneVideo={RequiresAtLeastOneVideo}");
        builder.Append(CultureInfo.InvariantCulture, $"; volume={Volume:0.##}");
        builder.Append(CultureInfo.InvariantCulture, $"; muted={Muted}");

        if (IsVideoIsolationActive)
        {
            builder.Append(CultureInfo.InvariantCulture,
                $"; sideImageRandomization={RandomizedSideImageMinSeconds:0}-{RandomizedSideImageMaxSeconds:0}s");
        }

        for (var slotIndex = 0; slotIndex < SplitScreenCount; slotIndex++)
        {
            builder.Append(CultureInfo.InvariantCulture, $"; slot{slotIndex + 1}Requirement={GetSlotKindRequirement(slotIndex)}");
            if (ShouldRandomizeImageDurationForSlot(slotIndex))
            {
                builder.Append(CultureInfo.InvariantCulture, $"; slot{slotIndex + 1}ImageDuration=randomized");
            }
        }

        return builder.ToString();
    }

    /// <summary>Short snapshot for high-frequency TRACE lines (slot refresh).</summary>
    public string FormatForDebugLogCompact()
    {
        var adjustedImage = GetAdjustedImageDuration();
        var builder = new StringBuilder(160);
        builder.Append(CultureInfo.InvariantCulture, $"cfg={SplitScreenMode}");
        builder.Append(CultureInfo.InvariantCulture, $",shuffle={Shuffle}");
        builder.Append(CultureInfo.InvariantCulture, $",{adjustedImage.TotalSeconds:0.##}s");
        builder.Append(CultureInfo.InvariantCulture, $",vidIso={(IsVideoIsolationActive ? 1 : 0)}");
        builder.Append(CultureInfo.InvariantCulture, $",sideAdv={SideAdvanceMode}");
        builder.Append(CultureInfo.InvariantCulture, $",sharedSideBag={SharedSideShuffleBag}");
        builder.Append(CultureInfo.InvariantCulture, $",playlistSrc={PlaylistSource}");
        return builder.ToString();
    }

    public void ApplyCentreVideoLayoutPreset()
    {
        SplitScreenMode = SplitScreenMode.TriSplit;
        AlwaysShowVideoInSplit = true;
        IsolateVideoToCenterPanel = true;
        Shuffle = true;
    }
}
