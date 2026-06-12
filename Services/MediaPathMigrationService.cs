namespace MauiMediaPlayer.Services;

public sealed class MediaPathMigrationService(
    PlaylistService playlistService,
    FavoritesService favoritesService,
    DislikesService dislikesService,
    SuperLikesService superLikesService,
    VideoMetadataService videoMetadataService,
    SettingsService settingsService,
    DebugLogService debugLog)
{
    private const string LogPrefix = "FilenameRepair:";

    public async Task MigrateAllAsync(string oldPath, string newPath)
    {
        playlistService.UpdateFilePath(oldPath, newPath);
        favoritesService.MigratePath(oldPath, newPath);
        dislikesService.MigratePath(oldPath, newPath);
        superLikesService.MigratePath(oldPath, newPath);
        videoMetadataService.MigratePath(oldPath, newPath);
        await settingsService.MigratePathAsync(oldPath, newPath);
        debugLog.Debug($"{LogPrefix} migrated path references {Path.GetFileName(oldPath)} → {Path.GetFileName(newPath)}.");
    }
}
