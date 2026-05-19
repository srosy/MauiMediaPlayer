using System.Text;

namespace MauiMediaPlayer.Services;

public sealed class DebugLogService
{
    private const int MaxEntries = 1000;
    private const long MaxSessionLogBytes = 2L * 1024 * 1024;
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

    public string BuildDiagnosticsReport(string header)
    {
        lock (_lock)
        {
            var builder = new StringBuilder(header.Length + (_entries.Count * 96));
            builder.AppendLine(header);
            builder.AppendLine();
            builder.AppendLine("--- Debug log (most recent) ---");
            foreach (var entry in _entries)
            {
                builder.AppendLine(entry);
            }

            return builder.ToString();
        }
    }

    private void Add(string level, string message)
    {
        var line = $"[{DateTimeOffset.Now:HH:mm:ss}] {level}: {message}";

        lock (_lock)
        {
            _entries.Enqueue(line);

            while (_entries.Count > MaxEntries)
            {
                _entries.Dequeue();
            }
        }

        AppendToSessionLog(line);
        Changed?.Invoke();
    }

    private void AppendToSessionLog(string line)
    {
        try
        {
            var path = GetSessionLogPath();
            var directory = Path.GetDirectoryName(path);

            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (File.Exists(path) && new FileInfo(path).Length > MaxSessionLogBytes)
            {
                return;
            }

            File.AppendAllText(path, line + Environment.NewLine);
        }
        catch
        {
        }
    }

    private static string GetSessionLogPath()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Mosaic",
            "logs");

        return Path.Combine(root, $"session-{DateTime.Now:yyyy-MM-dd}.txt");
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
