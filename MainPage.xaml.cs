using MauiMediaPlayer.Services;
using MauiMediaPlayer.Models;
#if WINDOWS
using MauiMediaPlayer.Platforms.Windows;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
#endif

namespace MauiMediaPlayer;

public partial class MainPage : ContentPage
{
	private const int NativeTapDelayMilliseconds = 280;
	private const int NativeVideoSlotCount = 3;
	private readonly NativeVideoPlaybackService _nativeVideoPlayback;
	private readonly NativePaneSwapDragBridgeService _nativePaneSwapBridge;
	private readonly PlaybackService _playback;
	private readonly IFullscreenService _fullscreen;
	private readonly DebugLogService _debugLog;
	private readonly FullPathDropService _fullPathDrop;
	private readonly View[] _nativeVideoElements;
	private readonly double?[] _nativePressClientX = new double?[NativeVideoSlotCount];
	private readonly double?[] _nativePressClientY = new double?[NativeVideoSlotCount];
	private readonly bool[] _nativePointerHooked = new bool[NativeVideoSlotCount];
	private readonly bool[] _nativePanSessionActive = new bool[NativeVideoSlotCount];
	private readonly bool[] _nativePanPending = new bool[NativeVideoSlotCount];
	private const double NativePaneDragThresholdPx = 8;
	private CancellationTokenSource? _nativeTapCancellation;

	public MainPage(
		NativeVideoPlaybackService nativeVideoPlayback,
		NativePaneSwapDragBridgeService nativePaneSwapBridge,
		PlaybackService playback,
		IFullscreenService fullscreen,
		DebugLogService debugLog,
		FullPathDropService fullPathDrop)
	{
		_nativeVideoPlayback = nativeVideoPlayback;
		_nativePaneSwapBridge = nativePaneSwapBridge;
		_playback = playback;
		_fullscreen = fullscreen;
		_debugLog = debugLog;
		_fullPathDrop = fullPathDrop;
		InitializeComponent();
		_nativeVideoElements = [nativeVideoElement0, nativeVideoElement1, nativeVideoElement2];
#if WINDOWS
		Loaded += OnPageLoadedForWindowsDrop;
#endif
		nativeVideoElement0.Loaded += OnNativeVideoElementLoaded;
		nativeVideoElement1.Loaded += OnNativeVideoElementLoaded;
		nativeVideoElement2.Loaded += OnNativeVideoElementLoaded;
		RegisterNativeVideoGestures();
		_nativePaneSwapBridge.NativeDragArmed += CancelPendingNativeTap;
	}

	private void CancelPendingNativeTap()
	{
		_nativeTapCancellation?.Cancel();
		_nativeTapCount = 0;
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
		for (var slotIndex = 0; slotIndex < NativeVideoSlotCount; slotIndex++)
		{
			RegisterNativeVideoGesture(_nativeVideoElements[slotIndex], slotIndex);
		}
	}

	private void RegisterNativeVideoGesture(View view, int slotIndex)
	{
		var tapGesture = new TapGestureRecognizer
		{
			NumberOfTapsRequired = 1
		};

		tapGesture.Tapped += OnNativeVideoTapped;
		view.GestureRecognizers.Add(tapGesture);

		var panGesture = new PanGestureRecognizer();
		panGesture.PanUpdated += (_, args) => OnNativeVideoPanUpdated(slotIndex, args);
		view.GestureRecognizers.Add(panGesture);

#if WINDOWS
		view.HandlerChanged += (_, _) => AttachWindowsPointerTracking(view, slotIndex);
#endif
	}

	private int _nativeTapCount;

#if WINDOWS
	private void AttachWindowsPointerTracking(View view, int slotIndex)
	{
		if (_nativePointerHooked[slotIndex] || view.Handler?.PlatformView is not FrameworkElement element)
		{
			return;
		}

		_nativePointerHooked[slotIndex] = true;
		element.PointerPressed += (_, args) => OnNativeVideoPointerPressed(slotIndex, element, args);
	}

	private void OnNativeVideoPointerPressed(int slotIndex, FrameworkElement target, PointerRoutedEventArgs args)
	{
		if (!_nativePaneSwapBridge.CanDrag)
		{
			return;
		}

		var local = args.GetCurrentPoint(target).Position;
		var bounds = _nativeVideoPlayback.GetLastBounds(slotIndex);
		_nativePressClientX[slotIndex] = bounds.X + local.X;
		_nativePressClientY[slotIndex] = bounds.Y + local.Y;
	}
#endif

	private async void OnNativeVideoPanUpdated(int slotIndex, PanUpdatedEventArgs args)
	{
		if (!_nativePaneSwapBridge.CanDrag)
		{
			return;
		}

		try
		{
			switch (args.StatusType)
			{
				case GestureStatus.Started:
					_nativePanPending[slotIndex] = true;
					_nativePanSessionActive[slotIndex] = false;
					break;

				case GestureStatus.Running:
					if (!_nativePanPending[slotIndex])
					{
						return;
					}

					var runningX = ResolveNativePressClientX(slotIndex) + args.TotalX;
					var runningY = ResolveNativePressClientY(slotIndex) + args.TotalY;
					var startX = ResolveNativePressClientX(slotIndex);
					var startY = ResolveNativePressClientY(slotIndex);
					var dx = runningX - startX;
					var dy = runningY - startY;

					if (!_nativePanSessionActive[slotIndex])
					{
						if (dx * dx + dy * dy < NativePaneDragThresholdPx * NativePaneDragThresholdPx)
						{
							return;
						}

						_nativePanSessionActive[slotIndex] = true;
						await _nativePaneSwapBridge.TryStartNativePanAsync(slotIndex, startX, startY);
					}

					await _nativePaneSwapBridge.UpdateNativePanAsync(slotIndex, runningX, runningY);
					break;

				case GestureStatus.Completed:
				case GestureStatus.Canceled:
					if (!_nativePanPending[slotIndex])
					{
						return;
					}

					_nativePanPending[slotIndex] = false;

					if (_nativePanSessionActive[slotIndex])
					{
						var finalX = ResolveNativePressClientX(slotIndex) + args.TotalX;
						var finalY = ResolveNativePressClientY(slotIndex) + args.TotalY;
						await _nativePaneSwapBridge.CompleteNativePanAsync(
							slotIndex,
							finalX,
							finalY,
							args.StatusType == GestureStatus.Canceled);
					}

					_nativePanSessionActive[slotIndex] = false;
					_nativePressClientX[slotIndex] = null;
					_nativePressClientY[slotIndex] = null;
					break;
			}
		}
		catch (Exception exception)
		{
			_debugLog.Error($"Native video pan handling failed for slot {slotIndex + 1}: {exception.Message}");
		}
	}

	private double ResolveNativePressClientX(int slotIndex)
	{
		if (_nativePressClientX[slotIndex] is { } stored)
		{
			return stored;
		}

		var bounds = _nativeVideoPlayback.GetLastBounds(slotIndex);
		return bounds.X + (bounds.Width * 0.5);
	}

	private double ResolveNativePressClientY(int slotIndex)
	{
		if (_nativePressClientY[slotIndex] is { } stored)
		{
			return stored;
		}

		var bounds = _nativeVideoPlayback.GetLastBounds(slotIndex);
		return bounds.Y + (bounds.Height * 0.5);
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
