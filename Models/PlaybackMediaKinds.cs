namespace MauiMediaPlayer.Models;

[Flags]
public enum PlaybackMediaKinds
{
    None = 0,
    Images = 1,
    Videos = 2,
    Gifs = 4,
    All = Images | Videos | Gifs
}
