namespace MauiMediaPlayer.Services;

public sealed class DebugLogService
{
    private const int MaxEntries = 1000;
    private readonly Lock _lock = new();
    private readonly Queue<string> _entries = [];

    public event Action? Changed;

    public IReadOnlyList<string> Entries
    {
        get
        {
            lock (_lock)
            {
                return _entries.ToArray();
            }
        }
    }

    public string Text
    {
        get
        {
            lock (_lock)
            {
                return string.Join(Environment.NewLine, _entries);
            }
        }
    }

    public void Add(string message)
    {
        Info(message);
    }

    public void Trace(string message)
    {
        Add("TRACE", message);
    }

    public void Debug(string message)
    {
        Add("DEBUG", message);
    }

    public void Info(string message)
    {
        Add("INFO", message);
    }

    public void Error(string message)
    {
        Add("ERROR", message);
    }

    private void Add(string level, string message)
    {
        lock (_lock)
        {
            _entries.Enqueue($"[{DateTimeOffset.Now:HH:mm:ss}] {level}: {message}");

            while (_entries.Count > MaxEntries)
            {
                _entries.Dequeue();
            }
        }

        Changed?.Invoke();
    }

    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
        }

        Changed?.Invoke();
    }
}
