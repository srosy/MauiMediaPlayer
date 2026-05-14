namespace MauiMediaPlayer.Services;

public interface IFolderPickerService
{
    bool IsFolderPickingSupported { get; }

    Task<string?> PickFolderAsync(CancellationToken cancellationToken = default);
}
