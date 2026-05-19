using MauiMediaPlayer.Services;
using MauiMediaPlayer.Models;
#if WINDOWS
using MauiMediaPlayer.Platforms.Windows;
using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
#endif

namespace MauiMediaPlayer;

public partial class MainPage : ContentPage
{
	private const int NativeTapDelayMilliseconds = 280;
	private readonly NativeVideoPlaybackService _nativeVideoPlayback;
	private readonly PlaybackService _playback;
	private readonly IFullscreenService _fullscreen;
	private readonly DebugLogService _debugLog;
	private readonly FullPathDropService _fullPathDrop;
	private CancellationTokenSource? _nativeTapCancellation;
	private int _nativeTapCount;

	public MainPage(
		NativeVideoPlaybackService nativeVideoPlayback,
		PlaybackService playback,
		IFullscreenService fullscreen,
		DebugLogService debugLog,
		FullPathDropService fullPathDrop)
	{
		_nativeVideoPlayback = nativeVideoPlayback;
		_playback = playback;
		_fullscreen = fullscreen;
		_debugLog = debugLog;
		_fullPathDrop = fullPathDrop;
		InitializeComponent();
#if WINDOWS
		Loaded += OnPageLoadedForWindowsDrop;
#endif
		nativeVideoElement0.Loaded += OnNativeVideoElementLoaded;
		nativeVideoElement1.Loaded += OnNativeVideoElementLoaded;
		nativeVideoElement2.Loaded += OnNativeVideoElementLoaded;
		RegisterNativeVideoGestures();
	}

	private void OnNativeVideoElementLoaded(object? sender, EventArgs e)
	{
		if (sender == nativeVideoElement0)
		{
			nativeVideoElement0.Loaded -= OnNativeVideoElementLoaded;
			_nativeVideoPlayback.RegisterMediaElement(0, nativeVideoElement0);
		}
		else if (sender == nativeVideoElement1)
		{
			nativeVideoElement1.Loaded -= OnNativeVideoElementLoaded;
			_nativeVideoPlayback.RegisterMediaElement(1, nativeVideoElement1);
		}
		else if (sender == nativeVideoElement2)
		{
			nativeVideoElement2.Loaded -= OnNativeVideoElementLoaded;
			_nativeVideoPlayback.RegisterMediaElement(2, nativeVideoElement2);
		}
	}

	private void RegisterNativeVideoGestures()
	{
		RegisterNativeVideoGesture(nativeVideoElement0);
		RegisterNativeVideoGesture(nativeVideoElement1);
		RegisterNativeVideoGesture(nativeVideoElement2);
	}

	private void RegisterNativeVideoGesture(View view)
	{
		var tapGesture = new TapGestureRecognizer
		{
			NumberOfTapsRequired = 1
		};

		tapGesture.Tapped += OnNativeVideoTapped;
		view.GestureRecognizers.Add(tapGesture);
	}

	private async void OnNativeVideoTapped(object? sender, TappedEventArgs args)
	{
		_nativeTapCount++;
		_nativeTapCancellation?.Cancel();
		_nativeTapCancellation?.Dispose();
		_nativeTapCancellation = new CancellationTokenSource();
		var cancellationToken = _nativeTapCancellation.Token;

		try
		{
			await Task.Delay(NativeTapDelayMilliseconds, cancellationToken);
			var tapCount = _nativeTapCount;
			_nativeTapCount = 0;
			await HandleNativeCanvasTapAsync(tapCount);
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception exception)
		{
			_debugLog.Error($"Native video tap handling failed: {exception.Message}");
		}
	}

	private async Task HandleNativeCanvasTapAsync(int tapCount)
	{
		if (tapCount >= 3)
		{
			if (_fullscreen.IsFullscreen && _playback.Settings.SplitScreenMode == SplitScreenMode.Single)
			{
				await _playback.PreviousAsync();
			}

			return;
		}

		if (tapCount == 2)
		{
			if (_fullscreen.IsSupported)
			{
				await _fullscreen.ToggleAsync();
			}

			return;
		}

		if (_playback.Status == PlaybackStatus.Playing)
		{
			await _playback.PauseAsync();
		}
		else
		{
			await _playback.PlayAsync();
		}
	}

#if WINDOWS
	private void OnPageLoadedForWindowsDrop(object? sender, EventArgs e)
	{
		Loaded -= OnPageLoadedForWindowsDrop;

		if (Handler?.PlatformView is not FrameworkElement element)
		{
			return;
		}

		element.AllowDrop = true;
		element.DragOver += OnWindowsDragOver;
		element.Drop += OnWindowsDrop;
	}

	private static void OnWindowsDragOver(object sender, Microsoft.UI.Xaml.DragEventArgs args)
	{
		if (args.DataView.Contains(StandardDataFormats.StorageItems))
		{
			args.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
		}

		args.Handled = true;
	}

	private async void OnWindowsDrop(object sender, Microsoft.UI.Xaml.DragEventArgs args)
	{
		if (!args.DataView.Contains(StandardDataFormats.StorageItems))
		{
			return;
		}

		var items = await args.DataView.GetStorageItemsAsync();
		var paths = items
			.OfType<StorageFile>()
			.Select(static file => file.Path)
			.Where(static path => !string.IsNullOrWhiteSpace(path))
			.ToArray();

		if (paths.Length > 0)
		{
			await _fullPathDrop.NotifyPathsDroppedAsync(paths);
		}
	}
#endif
}
