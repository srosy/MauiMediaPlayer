using Microsoft.Extensions.Logging;
using CommunityToolkit.Maui;
using MauiMediaPlayer.Services;

#if WINDOWS
using MauiMediaPlayer.Platforms.Windows;
#endif

namespace MauiMediaPlayer;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.UseMauiCommunityToolkitMediaElement(isAndroidForegroundServiceEnabled: false)
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
			});

		builder.Services.AddMauiBlazorWebView();
		builder.Services.AddSingleton<MainPage>();
		builder.Services.AddSingleton<SettingsService>();
		builder.Services.AddSingleton<MediaLibraryService>();
		builder.Services.AddSingleton<FavoritesService>();
		builder.Services.AddSingleton<PlaylistService>();
		builder.Services.AddSingleton<NativeVideoPlaybackService>();
		builder.Services.AddSingleton<PlaybackService>();
		builder.Services.AddSingleton<DebugLogService>();
		builder.Services.AddSingleton<VideoMetadataService>();
		builder.Services.AddSingleton<MediaSourceService>();
		builder.Services.AddSingleton<FullPathDropService>();
		builder.Services.AddSingleton<ToastService>();
		builder.Services.AddSingleton<WindowTitleService>();
		builder.Services.AddSingleton<PopoverStateService>();
#if WINDOWS
		builder.Services.AddSingleton<IFolderPickerService, WindowsFolderPickerService>();
		builder.Services.AddSingleton<IFullscreenService, WindowsFullscreenService>();
		builder.Services.AddSingleton<IFileSystemActionsService, WindowsFileSystemActionsService>();
		builder.Services.AddSingleton<IImageDisplayUriProvider, WindowsImageDisplayUriProvider>();
		builder.Services.AddSingleton<WindowsSystemMediaTransportService>();
#else
		builder.Services.AddSingleton<IFolderPickerService, UnsupportedFolderPickerService>();
		builder.Services.AddSingleton<IFullscreenService, NoOpFullscreenService>();
		builder.Services.AddSingleton<IFileSystemActionsService, NoOpFileSystemActionsService>();
		builder.Services.AddSingleton<IImageDisplayUriProvider, NoOpImageDisplayUriProvider>();
#endif

#if DEBUG
		builder.Services.AddBlazorWebViewDeveloperTools();
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}
}
