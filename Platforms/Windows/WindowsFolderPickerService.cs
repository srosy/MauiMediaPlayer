using MauiMediaPlayer.Services;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace MauiMediaPlayer.Platforms.Windows;

public sealed class WindowsFolderPickerService : IFolderPickerService
{
    public bool IsFolderPickingSupported => true;

    public async Task<string?> PickFolderAsync(CancellationToken cancellationToken = default)
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.VideosLibrary
        };

        picker.FileTypeFilter.Add("*");

        var window = Application.Current?.Windows.FirstOrDefault()?.Handler?.PlatformView as Microsoft.UI.Xaml.Window;

        if (window is not null)
        {
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(window));
        }

        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }
}
