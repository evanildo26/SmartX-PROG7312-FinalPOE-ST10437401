namespace SmartX.Api.Services.Topology;

/// <summary>
/// Disjoint-set (union–find) forest used by Kruskal's algorithm to answer
/// "would this link create a loop?" in near-constant time.
///
/// Two optimisations keep the trees almost flat:
///  • union by rank — the shorter tree is hung under the taller one;
///  • path compression — every node visited by Find is re-pointed straight at the root.
/// Together they make each operation O(α(n)), where α is the inverse Ackermann function (≤ 4 in practice).
/// </summary>
public sealed class DisjointSet<T> where T : notnull
{
    private readonly IEqualityComparer<T> _comparer;
    private readonly Dictionary<T, T> _parent;
    private readonly Dictionary<T, int> _rank;

    public DisjointSet(IEqualityComparer<T>? comparer = null)
    {
        _comparer = comparer ?? EqualityComparer<T>.Default;
        _parent = new Dictionary<T, T>(_comparer);
        _rank = new Dictionary<T, int>(_comparer);
    }

    /// <summary>Number of separate sets (for the mesh: separate connected islands).</summary>
    public int SetCount { get; private set; }

    public bool Contains(T item) => _parent.ContainsKey(item);

    public void MakeSet(T item)
    {
        if (_parent.ContainsKey(item)) return;
        _parent[item] = item;
        _rank[item] = 0;
        SetCount++;
    }

    public T Find(T item)
    {
        if (!_parent.ContainsKey(item)) throw new KeyNotFoundException($"'{item}' is not in the disjoint set.");

        var root = item;
        while (!_comparer.Equals(_parent[root], root)) root = _parent[root];

        // Path compression: point every node on the way directly at the root.
        while (!_comparer.Equals(_parent[item], root))
        {
            var next = _parent[item];
            _parent[item] = root;
            item = next;
        }
        return root;
    }

    /// <summary>Merges the two sets. Returns false if they were already the same set (the edge would form a cycle).</summary>
    public bool Union(T a, T b)
    {
        var rootA = Find(a);
        var rootB = Find(b);
        if (_comparer.Equals(rootA, rootB)) return false;

        int rankA = _rank[rootA], rankB = _rank[rootB];
        if (rankA < rankB) _parent[rootA] = rootB;
        else if (rankA > rankB) _parent[rootB] = rootA;
        else
        {
            _parent[rootB] = rootA;
            _rank[rootA] = rankA + 1;
        }
        SetCount--;
        return true;
    }

    public bool Connected(T a, T b) => _comparer.Equals(Find(a), Find(b));
}
