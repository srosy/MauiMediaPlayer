using MauiMediaPlayer.Models;

namespace MauiMediaPlayer.Services;

public sealed class PlaylistService
{
    private readonly Random _random = new();
    private readonly List<MediaItem> _items = [];
    private readonly Stack<int> _shuffleHistory = [];
    private readonly Queue<int> _shuffleQueue = [];
    private int _currentIndex = -1;

    public event Action? Changed;

    public IReadOnlyList<MediaItem> Items => _items;

    public MediaItem? CurrentItem => _currentIndex >= 0 && _currentIndex < _items.Count ? _items[_currentIndex] : null;

    public int CurrentIndex => _currentIndex;

    public bool HasItems => _items.Count > 0;

    public string CounterText => CurrentItem is null ? $"0 / {_items.Count}" : $"{_currentIndex + 1} / {_items.Count}";

    public IReadOnlyList<MediaItem> GetWindowItems(int count)
    {
        if (_items.Count == 0 || _currentIndex < 0)
        {
            return [];
        }

        var safeCount = Math.Clamp(count, 1, Math.Min(3, _items.Count));
        var items = new List<MediaItem>(safeCount);

        for (var offset = 0; offset < safeCount; offset++)
        {
            items.Add(_items[(_currentIndex + offset) % _items.Count]);
        }

        return items;
    }

    public void AddItems(IEnumerable<MediaItem> items)
    {
        var existingPaths = _items.Select(static item => item.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var newItems = items.Where(item => existingPaths.Add(item.FilePath)).ToList();

        if (newItems.Count == 0)
        {
            return;
        }

        _items.AddRange(newItems);

        if (_currentIndex == -1)
        {
            _currentIndex = 0;
        }

        ResetShuffleQueue();
        NotifyChanged();
    }

    public void Clear()
    {
        _items.Clear();
        _currentIndex = -1;
        _shuffleHistory.Clear();
        _shuffleQueue.Clear();
        NotifyChanged();
    }

    public void Remove(MediaItem item)
    {
        var index = _items.FindIndex(candidate => candidate.Id == item.Id);

        if (index == -1)
        {
            return;
        }

        _items.RemoveAt(index);

        if (_items.Count == 0)
        {
            _currentIndex = -1;
        }
        else if (_currentIndex >= _items.Count)
        {
            _currentIndex = _items.Count - 1;
        }
        else if (index < _currentIndex)
        {
            _currentIndex--;
        }

        ResetShuffleQueue();
        NotifyChanged();
    }

    public bool SetCurrent(MediaItem item)
    {
        var index = _items.FindIndex(candidate => candidate.Id == item.Id);

        if (index == -1)
        {
            return false;
        }

        return SetCurrentIndex(index);
    }

    public bool SetCurrentIndex(int index)
    {
        if (index < 0 || index >= _items.Count)
        {
            return false;
        }

        if (_currentIndex >= 0 && _currentIndex != index)
        {
            _shuffleHistory.Push(_currentIndex);
        }

        _currentIndex = index;
        NotifyChanged();
        return true;
    }

    public bool MoveNext(bool shuffle, LoopMode loopMode)
    {
        if (_items.Count == 0)
        {
            _currentIndex = -1;
            NotifyChanged();
            return false;
        }

        if (loopMode == LoopMode.One)
        {
            NotifyChanged();
            return true;
        }

        if (shuffle)
        {
            return MoveToNextShuffleItem(loopMode);
        }

        if (ShuffleIndexFilter is not null)
        {
            return MoveToNextSequentialFiltered(loopMode);
        }

        if (_currentIndex < _items.Count - 1)
        {
            _currentIndex++;
            NotifyChanged();
            return true;
        }

        if (loopMode == LoopMode.All)
        {
            _currentIndex = 0;
            NotifyChanged();
            return true;
        }

        return false;
    }

    public bool MovePrevious(bool shuffle)
    {
        if (_items.Count == 0)
        {
            return false;
        }

        if (shuffle && _shuffleHistory.TryPop(out var previousIndex) && previousIndex >= 0 && previousIndex < _items.Count)
        {
            _currentIndex = previousIndex;
            NotifyChanged();
            return true;
        }

        if (ShuffleIndexFilter is not null && !shuffle)
        {
            return MoveToPreviousSequentialFiltered();
        }

        if (_currentIndex > 0)
        {
            _currentIndex--;
            NotifyChanged();
            return true;
        }

        return false;
    }

    public bool MoveRandom()
    {
        if (_items.Count == 0)
        {
            return false;
        }

        if (_currentIndex >= 0)
        {
            _shuffleHistory.Push(_currentIndex);
        }

        _currentIndex = _items.Count == 1 ? 0 : GetRandomIndexExcept(_currentIndex);
        NotifyChanged();
        return true;
    }

    public void SortByName()
    {
        Sort(static item => item.DisplayName);
    }

    public void SortByPath()
    {
        Sort(static item => item.FilePath);
    }

    public void SortByDateModified()
    {
        var currentItem = CurrentItem;
        _items.Sort(static (left, right) => right.LastModified.CompareTo(left.LastModified));
        RestoreCurrentItem(currentItem);
    }

    public void ShuffleItems()
    {
        var currentItem = CurrentItem;

        for (var index = _items.Count - 1; index > 0; index--)
        {
            var randomIndex = _random.Next(index + 1);
            (_items[index], _items[randomIndex]) = (_items[randomIndex], _items[index]);
        }

        RestoreCurrentItem(currentItem);
    }

    public void ResetShufflePlayback()
    {
        _shuffleHistory.Clear();
        ResetShuffleQueue();
        NotifyChanged();
    }

    public void RebuildShuffleQueue()
    {
        ResetShuffleQueue();
        NotifyChanged();
    }

    public Func<int, bool>? ShuffleIndexFilter { get; set; }

    public Func<int, int>? ShuffleIndexWeight { get; set; }

    public bool MoveRandomAmong(IReadOnlyList<int> eligibleIndices)
    {
        if (eligibleIndices.Count == 0)
        {
            return false;
        }

        if (_currentIndex >= 0)
        {
            _shuffleHistory.Push(_currentIndex);
        }

        _currentIndex = eligibleIndices.Count == 1
            ? eligibleIndices[0]
            : eligibleIndices[Random.Shared.Next(eligibleIndices.Count)];

        NotifyChanged();
        return true;
    }

    private void Sort(Func<MediaItem, string> keySelector)
    {
        var currentItem = CurrentItem;
        _items.Sort((left, right) => string.Compare(keySelector(left), keySelector(right), StringComparison.OrdinalIgnoreCase));
        RestoreCurrentItem(currentItem);
    }

    private void RestoreCurrentItem(MediaItem? currentItem)
    {
        _currentIndex = currentItem is null ? (_items.Count == 0 ? -1 : 0) : _items.FindIndex(item => item.Id == currentItem.Id);

        if (_currentIndex == -1 && _items.Count > 0)
        {
            _currentIndex = 0;
        }

        ResetShuffleQueue();
        NotifyChanged();
    }

    private bool MoveToNextShuffleItem(LoopMode loopMode)
    {
        if (_shuffleQueue.Count == 0)
        {
            if (loopMode != LoopMode.All)
            {
                return false;
            }

            ResetShuffleQueue();
        }

        if (_shuffleQueue.Count == 0)
        {
            var fallback = FindFilteredIndex(preferNotCurrent: true);
            if (fallback < 0)
            {
                return false;
            }

            if (_currentIndex >= 0)
            {
                _shuffleHistory.Push(_currentIndex);
            }

            _currentIndex = fallback;
            NotifyChanged();
            return true;
        }

        if (_currentIndex >= 0)
        {
            _shuffleHistory.Push(_currentIndex);
        }

        _currentIndex = _shuffleQueue.Dequeue();
        NotifyChanged();
        return true;
    }

    private void ResetShuffleQueue()
    {
        _shuffleQueue.Clear();

        if (_items.Count == 0)
        {
            return;
        }

        var entries = new List<int>();

        for (var index = 0; index < _items.Count; index++)
        {
            if (index == _currentIndex)
            {
                continue;
            }

            if (ShuffleIndexFilter is not null && !ShuffleIndexFilter(index))
            {
                continue;
            }

            var weight = Math.Max(1, ShuffleIndexWeight?.Invoke(index) ?? 1);

            for (var copy = 0; copy < weight; copy++)
            {
                entries.Add(index);
            }
        }

        for (var i = entries.Count - 1; i > 0; i--)
        {
            var j = _random.Next(i + 1);
            (entries[i], entries[j]) = (entries[j], entries[i]);
        }

        foreach (var index in entries)
        {
            _shuffleQueue.Enqueue(index);
        }
    }

    private bool MoveToNextSequentialFiltered(LoopMode loopMode)
    {
        var count = _items.Count;
        var start = _currentIndex < 0 ? -1 : _currentIndex;

        for (var step = 1; step <= count; step++)
        {
            var candidate = start + step;

            if (candidate >= count)
            {
                if (loopMode != LoopMode.All)
                {
                    return false;
                }

                candidate %= count;
            }

            if (ShuffleIndexFilter!(candidate))
            {
                if (_currentIndex >= 0)
                {
                    _shuffleHistory.Push(_currentIndex);
                }

                _currentIndex = candidate;
                NotifyChanged();
                return true;
            }
        }

        return false;
    }

    private bool MoveToPreviousSequentialFiltered()
    {
        var count = _items.Count;
        var start = _currentIndex < 0 ? count : _currentIndex;

        for (var step = 1; step <= count; step++)
        {
            var candidate = start - step;

            if (candidate < 0)
            {
                return false;
            }

            if (ShuffleIndexFilter!(candidate))
            {
                if (_currentIndex >= 0)
                {
                    _shuffleHistory.Push(_currentIndex);
                }

                _currentIndex = candidate;
                NotifyChanged();
                return true;
            }
        }

        return false;
    }

    private int FindFilteredIndex(bool preferNotCurrent)
    {
        for (var index = 0; index < _items.Count; index++)
        {
            if (ShuffleIndexFilter is not null && !ShuffleIndexFilter(index))
            {
                continue;
            }

            if (preferNotCurrent && index == _currentIndex)
            {
                continue;
            }

            return index;
        }

        if (!preferNotCurrent)
        {
            return -1;
        }

        for (var index = 0; index < _items.Count; index++)
        {
            if (ShuffleIndexFilter is not null && !ShuffleIndexFilter(index))
            {
                continue;
            }

            return index;
        }

        return -1;
    }

    private int GetRandomIndexExcept(int exceptIndex)
    {
        if (exceptIndex < 0 || exceptIndex >= _items.Count)
        {
            return _random.Next(_items.Count);
        }

        var index = _random.Next(_items.Count - 1);

        if (index >= exceptIndex)
        {
            index++;
        }

        return index;
    }

    private void NotifyChanged()
    {
        Changed?.Invoke();
    }
}
