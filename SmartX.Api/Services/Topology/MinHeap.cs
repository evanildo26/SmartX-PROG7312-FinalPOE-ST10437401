namespace SmartX.Api.Services.Topology;

/// <summary>
/// Binary min-heap stored in a List (the "array-backed complete binary tree"):
/// the parent of index i is (i - 1) / 2 and its children are 2i + 1 and 2i + 2.
///
/// Push and Pop are O(log n) because an item only ever moves along one root-to-leaf path;
/// Peek is O(1). Prim's algorithm and Dijkstra's algorithm both repeatedly need "the cheapest
/// thing seen so far", which is exactly what a min-heap answers fastest.
/// Duplicate elements are allowed: the algorithms use lazy deletion (stale entries are skipped when popped).
/// </summary>
public sealed class MinHeap<TElement, TPriority> where TPriority : IComparable<TPriority>
{
    private readonly List<(TElement Element, TPriority Priority)> _items = new();

    public int Count => _items.Count;
    public bool IsEmpty => _items.Count == 0;

    public void Push(TElement element, TPriority priority)
    {
        _items.Add((element, priority));
        SiftUp(_items.Count - 1);
    }

    public (TElement Element, TPriority Priority) Peek() =>
        _items.Count > 0 ? _items[0] : throw new InvalidOperationException("The heap is empty.");

    public TElement Pop() =>
        TryPop(out var element, out _) ? element : throw new InvalidOperationException("The heap is empty.");

    public bool TryPop(out TElement element, out TPriority priority)
    {
        if (_items.Count == 0)
        {
            element = default!;
            priority = default!;
            return false;
        }

        (element, priority) = _items[0];
        var last = _items[^1];
        _items.RemoveAt(_items.Count - 1);
        if (_items.Count > 0)
        {
            _items[0] = last;   // move the last leaf to the root, then let it sink to its place
            SiftDown(0);
        }
        return true;
    }

    /// <summary>True if every parent is &lt;= its children. Used by the unit tests.</summary>
    public bool IsValidHeap()
    {
        for (int i = 1; i < _items.Count; i++)
            if (_items[i].Priority.CompareTo(_items[(i - 1) / 2].Priority) < 0) return false;
        return true;
    }

    private void SiftUp(int index)
    {
        while (index > 0)
        {
            int parent = (index - 1) / 2;
            if (_items[index].Priority.CompareTo(_items[parent].Priority) >= 0) break;
            (_items[index], _items[parent]) = (_items[parent], _items[index]);
            index = parent;
        }
    }

    private void SiftDown(int index)
    {
        int count = _items.Count;
        while (true)
        {
            int left = 2 * index + 1, right = left + 1, smallest = index;
            if (left < count && _items[left].Priority.CompareTo(_items[smallest].Priority) < 0) smallest = left;
            if (right < count && _items[right].Priority.CompareTo(_items[smallest].Priority) < 0) smallest = right;
            if (smallest == index) return;
            (_items[index], _items[smallest]) = (_items[smallest], _items[index]);
            index = smallest;
        }
    }
}
