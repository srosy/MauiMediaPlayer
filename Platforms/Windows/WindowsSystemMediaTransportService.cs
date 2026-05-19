#if WINDOWS
using System.Runtime.InteropServices;
using MauiMediaPlayer.Models;
using MauiMediaPlayer.Services;
using Windows.Media;

namespace MauiMediaPlayer.Platforms.Windows;

/// <summary>
/// Hooks System Media Transport Controls after the WinUI window exists.
/// Must not call <see cref="SystemMediaTransportControls.GetForCurrentView"/> from the ctor —
/// MAUI resolves this singleton while creating <see cref="MainPage"/> inside
/// <c>Application.CreateWindow</c>, before a current view / window handle is available.
/// </summary>
public sealed class WindowsSystemMediaTransportService : IDisposable
{
    private readonly PlaybackService _playback;
    private SystemMediaTransportControls? _controls;
    private bool _initialized;
    private bool _disposed;

    public WindowsSystemMediaTransportService(PlaybackService playback, IFullscreenService fullscreen)
    {
        _ = fullscreen;
        _playback = playback;
        _playback.Changed += OnPlaybackChanged;
    }

    /// <summary>
    /// Binds SMTC to the current view. Safe to call multiple times until it succeeds.
    /// </summary>
    public bool TryInitialize()
    {
        if (_disposed || _initialized)
        {
            return _initialized;
        }

        try
        {
            var controls = SystemMediaTransportControls.GetForCurrentView();
            controls.IsEnabled = true;
            controls.IsPlayEnabled = true;
            controls.IsPauseEnabled = true;
            controls.IsNextEnabled = true;
            controls.IsPreviousEnabled = true;
            controls.ButtonPressed += OnButtonPressed;

            _controls = controls;
            _initialized = true;
            UpdateTransportState();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async void OnButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        switch (args.Button)
        {
            case SystemMediaTransportControlsButton.Play:
                await _playback.PlayAsync();
                break;
            case SystemMediaTransportControlsButton.Pause:
                await _playback.PauseAsync();
                break;
            case SystemMediaTransportControlsButton.Next:
                await _playback.NextAsync();
                break;
            case SystemMediaTransportControlsButton.Previous:
                await _playback.PreviousAsync();
                break;
        }

        UpdateTransportState();
    }

    private void OnPlaybackChanged()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (!_initialized)
            {
                TryInitialize();
            }

            UpdateTransportState();
        });
    }

    private void UpdateTransportState()
    {
        if (_disposed || !_initialized || _controls is null)
        {
            return;
        }

        try
        {
            _controls.PlaybackStatus = _playback.Status switch
            {
                PlaybackStatus.Playing => MediaPlaybackStatus.Playing,
                PlaybackStatus.Paused => MediaPlaybackStatus.Paused,
                _ => MediaPlaybackStatus.Stopped
            };

            var item = _playback.CurrentItem;

            if (item is null)
            {
                _controls.DisplayUpdater.ClearAll();
                return;
            }

            _controls.DisplayUpdater.Type = MediaPlaybackType.Music;
            _controls.DisplayUpdater.MusicProperties.Title = item.DisplayName;
            _controls.DisplayUpdater.MusicProperties.AlbumArtist = Branding.AppName;
            _controls.DisplayUpdater.Update();
        }
        catch (COMException)
        {
            // SMTC can throw while the window is activating, minimizing, or during teardown.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _playback.Changed -= OnPlaybackChanged;

        if (_controls is not null)
        {
            _controls.ButtonPressed -= OnButtonPressed;
            _controls = null;
        }
    }
}
#endif
