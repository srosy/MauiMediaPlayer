using System.Collections.Concurrent;

namespace MauiMediaPlayer.Services;

/// <summary>
/// Centralized non-modal status messaging. Replaces the old static alert bar
/// at the top of the shell. Toasts auto-dismiss after a per-severity duration
/// and stack newest-on-top. Components subscribe to <see cref="Changed"/> to
/// re-render. Each toast carries a monotonic Id so the host can key items and
/// run leave animations without flicker.
/// </summary>
public sealed class ToastService
{
    private static readonly TimeSpan DefaultLifetimeInfo = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan DefaultLifetimeSuccess = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan DefaultLifetimeError = TimeSpan.FromSeconds(6);
    private const int MaxConcurrentToasts = 5;

    private readonly object _gate = new();
    private readonly List<Toast> _toasts = [];
    private readonly ConcurrentDictionary<long, CancellationTokenSource> _dismissals = new();
    private long _nextId;

    public event Action? Changed;

    public IReadOnlyList<Toast> Toasts
    {
        get
        {
            lock (_gate)
            {
                return [.. _toasts];
            }
        }
    }

    public void Info(string message) => Push(message, ToastSeverity.Info, DefaultLifetimeInfo);

    public void Success(string message) => Push(message, ToastSeverity.Success, DefaultLifetimeSuccess);

    public void Error(string message) => Push(message, ToastSeverity.Error, DefaultLifetimeError);

    public void Push(string message, ToastSeverity severity, TimeSpan? lifetime = null)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var id = Interlocked.Increment(ref _nextId);
        var toast = new Toast(id, message, severity);

        lock (_gate)
        {
            _toasts.Insert(0, toast);

            // Cap to avoid runaway accumulation if a callsite spams. Drop oldest first;
            // the dismiss timer for those entries is still cancelled below.
            while (_toasts.Count > MaxConcurrentToasts)
            {
                var dropped = _toasts[^1];
                _toasts.RemoveAt(_toasts.Count - 1);
                CancelDismissal(dropped.Id);
            }
        }

        ScheduleDismissal(id, lifetime ?? DefaultLifetimeFor(severity));
        Changed?.Invoke();
    }

    public void Dismiss(long id)
    {
        var removed = false;

        lock (_gate)
        {
            for (var i = 0; i < _toasts.Count; i++)
            {
                if (_toasts[i].Id == id)
                {
                    _toasts.RemoveAt(i);
                    removed = true;
                    break;
                }
            }
        }

        CancelDismissal(id);

        if (removed)
        {
            Changed?.Invoke();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            foreach (var toast in _toasts)
            {
                CancelDismissal(toast.Id);
            }

            _toasts.Clear();
        }

        Changed?.Invoke();
    }

    private void ScheduleDismissal(long id, TimeSpan lifetime)
    {
        var cts = new CancellationTokenSource();
        _dismissals[id] = cts;
        _ = AutoDismissAsync(id, lifetime, cts.Token);
    }

    private async Task AutoDismissAsync(long id, TimeSpan lifetime, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(lifetime, cancellationToken);
            Dismiss(id);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void CancelDismissal(long id)
    {
        if (_dismissals.TryRemove(id, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    private static TimeSpan DefaultLifetimeFor(ToastSeverity severity) => severity switch
    {
        ToastSeverity.Success => DefaultLifetimeSuccess,
        ToastSeverity.Error => DefaultLifetimeError,
        _ => DefaultLifetimeInfo,
    };
}

public sealed record Toast(long Id, string Message, ToastSeverity Severity);

public enum ToastSeverity
{
    Info,
    Success,
    Error,
}
