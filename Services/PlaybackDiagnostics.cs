namespace MauiMediaPlayer.Services;

internal static class PlaybackDiagnostics
{
    public static string FormatTimeline(TimeSpan position, TimeSpan duration)
    {
        var pos = Math.Max(0, position.TotalSeconds);
        var dur = Math.Max(0, duration.TotalSeconds);

        if (dur <= 0.5)
        {
            return $"pos={pos:0.###}s; dur=unknown";
        }

        var remain = Math.Max(0, dur - pos);
        var pct = Math.Clamp(pos / dur * 100, 0, 100);
        return $"pos={pos:0.###}s; dur={dur:0.###}s; remain={remain:0.###}s; pct={pct:0.#}%";
    }

    public static string FormatWallElapsed(DateTimeOffset? startedAt)
    {
        if (startedAt is null)
        {
            return "wall=—";
        }

        var elapsed = DateTimeOffset.UtcNow - startedAt.Value;
        return $"wall={elapsed.TotalSeconds:0.###}s";
    }
}
