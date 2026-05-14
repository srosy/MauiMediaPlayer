using MauiMediaPlayer.Services;
using Microsoft.UI.Windowing;
using WinRT.Interop;

namespace MauiMediaPlayer.Platforms.Windows;

public sealed class WindowsFullscreenService : IFullscreenService
{
    public event Action? FullscreenChanged;

    public bool IsSupported => true;

    public bool IsFullscreen { get; private set; }

    public Task ToggleAsync()
    {
        return IsFullscreen ? ExitAsync() : EnterAsync();
    }

    public Task ExitAsync()
    {
        var appWindow = GetAppWindow();

        if (appWindow is null)
        {
            return Task.CompletedTask;
        }

        appWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
        IsFullscreen = false;
        FullscreenChanged?.Invoke();
        return Task.CompletedTask;
    }

    private Task EnterAsync()
    {
        var appWindow = GetAppWindow();

        if (appWindow is null)
        {
            return Task.CompletedTask;
        }

        appWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        IsFullscreen = true;
        FullscreenChanged?.Invoke();
        return Task.CompletedTask;
    }

    private static AppWindow? GetAppWindow()
    {
        var window = Application.Current?.Windows.FirstOrDefault()?.Handler?.PlatformView as Microsoft.UI.Xaml.Window;

        if (window is null)
        {
            return null;
        }

        var windowHandle = WindowNative.GetWindowHandle(window);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(windowHandle);
        return AppWindow.GetFromWindowId(windowId);
    }
}
