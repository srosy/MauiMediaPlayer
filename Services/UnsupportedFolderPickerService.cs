namespace MauiMediaPlayer.Services;

public sealed class UnsupportedFolderPickerService : IFolderPickerService
{
    public bool IsFolderPickingSupported => false;

    public Task<string?> PickFolderAsync(CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("Folder picking is not available on this platform yet. Add files directly instead.");
    }
}
