namespace MauiMediaPlayer.Models;

[Flags]
public enum PlaylistSourceFilters
{
    None = 0,
    Favorites = 1,
    Dislikes = 2,
    SuperLikes = 4,
    AllFilters = Favorites | Dislikes | SuperLikes
}
