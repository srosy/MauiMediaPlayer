namespace MauiMediaPlayer.Services;

/// <summary>
/// Bridges native MediaElement pan gestures (MainPage) to MediaViewport's JS pane-swap session.
/// Native SwapChainPanel sits above BlazorWebView, so HTML pointerdown never fires on video panes.
/// </summary>
public sealed class NativePaneSwapDragBridgeService
{
    private Func<int, double, double, Task>? _startAsync;
    private Func<int, double, double, Task>? _updateAsync;
    private Func<int, double, double, bool, Task>? _completeAsync;
    private Func<bool>? _canDrag;

    public void Register(
        Func<bool> canDrag,
        Func<int, double, double, Task> startAsync,
        Func<int, double, double, Task> updateAsync,
        Func<int, double, double, bool, Task> completeAsync)
    {
        _canDrag = canDrag;
        _startAsync = startAsync;
        _updateAsync = updateAsync;
        _completeAsync = completeAsync;
    }

    public void Unregister()
    {
        _canDrag = null;
        _startAsync = null;
        _updateAsync = null;
        _completeAsync = null;
    }

    public bool CanDrag => _canDrag?.Invoke() == true;

    public Task TryStartNativePanAsync(int slotIndex, double clientX, double clientY) =>
        _startAsync?.Invoke(slotIndex, clientX, clientY) ?? Task.CompletedTask;

    public Task UpdateNativePanAsync(int slotIndex, double clientX, double clientY) =>
        _updateAsync?.Invoke(slotIndex, clientX, clientY) ?? Task.CompletedTask;

    public Task CompleteNativePanAsync(int slotIndex, double clientX, double clientY, bool canceled) =>
        _completeAsync?.Invoke(slotIndex, clientX, clientY, canceled) ?? Task.CompletedTask;

    /// <summary>Fired when a native-initiated drag crosses the move threshold so MainPage can cancel pending tap-to-play/pause.</summary>
    public event Action? NativeDragArmed;

    public void NotifyNativeDragArmed() => NativeDragArmed?.Invoke();
}
