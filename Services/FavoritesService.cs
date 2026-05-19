using System.Text.Json;

namespace MauiMediaPlayer.Services;

public sealed class FavoritesService
{
    private const string FavoritesFileName = "favorites.json";
    private readonly HashSet<string> _paths = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _persistGate = new(1, 1);
    private bool _loaded;

    public event Action? Changed;

    public IReadOnlyCollection<string> Paths
    {
        get
        {
            EnsureLoaded();
            return _paths.ToList();
        }
    }

    public int Count
    {
        get
        {
            EnsureLoaded();
            return _paths.Count;
        }
    }

    public bool IsFavorite(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        EnsureLoaded();
        return _paths.Contains(NormalizePath(filePath));
    }

    public bool Toggle(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        EnsureLoaded();
        var normalized = NormalizePath(filePath);
        var added = _paths.Add(normalized);

        if (!added)
        {
            _paths.Remove(normalized);
        }

        _ = PersistAsync();
        Changed?.Invoke();
        return added;
    }

    public int CountInPlaylist(IEnumerable<string> playlistPaths)
    {
        EnsureLoaded();

        return playlistPaths.Count(path => !string.IsNullOrWhiteSpace(path) && _paths.Contains(NormalizePath(path)));
    }

    private void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        var path = GetFavoritesFilePath();

        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(path);
            var paths = JsonSerializer.Deserialize<List<string>>(json) ?? [];

            foreach (var entry in paths)
            {
                if (!string.IsNullOrWhiteSpace(entry))
                {
                    _paths.Add(NormalizePath(entry));
                }
            }
        }
        catch (Exception)
        {
            _paths.Clear();
        }
    }

    private async Task PersistAsync()
    {
        await _persistGate.WaitAsync();

        try
        {
            var directory = FileSystem.AppDataDirectory;
            Directory.CreateDirectory(directory);
            var json = JsonSerializer.Serialize(_paths.OrderBy(static path => path, StringComparer.OrdinalIgnoreCase));
            await File.WriteAllTextAsync(Path.Combine(directory, FavoritesFileName), json);
        }
        finally
        {
            _persistGate.Release();
        }
    }

    private static string GetFavoritesFilePath() =>
        Path.Combine(FileSystem.AppDataDirectory, FavoritesFileName);

    private static string NormalizePath(string filePath) =>
        Path.GetFullPath(filePath);
}
