namespace SmartX.Api.Services.Topology;

/// <summary>One undirected, weighted edge. For the mesh: A and B are MAC addresses, Weight is the link's energy cost.</summary>
public readonly record struct WeightedEdge<TNode>(TNode A, TNode B, double Weight) where TNode : notnull;

/// <summary>
/// Undirected weighted graph stored as an adjacency list:
/// Dictionary&lt;vertex, Dictionary&lt;neighbour, weight&gt;&gt;.
///
/// Why an adjacency list and not a matrix: a wireless mesh is sparse (each ESP32 only hears the
/// few nodes inside its radio range), so the list uses O(V + E) memory instead of O(V²), and
/// "who are my neighbours?" — the question every traversal asks — costs O(degree), not O(V).
/// Looking up one specific link is still O(1) on average because the inner collection is a Dictionary.
/// </summary>
public sealed class WeightedGraph<TNode> where TNode : notnull
{
    private static readonly IReadOnlyDictionary<TNode, double> NoNeighbours = new Dictionary<TNode, double>();

    private readonly Dictionary<TNode, Dictionary<TNode, double>> _adjacency;

    public WeightedGraph(IEqualityComparer<TNode>? comparer = null)
    {
        Comparer = comparer ?? EqualityComparer<TNode>.Default;
        _adjacency = new Dictionary<TNode, Dictionary<TNode, double>>(Comparer);
    }

    public IEqualityComparer<TNode> Comparer { get; }
    public int VertexCount => _adjacency.Count;
    public int EdgeCount { get; private set; }
    public IEnumerable<TNode> Vertices => _adjacency.Keys;

    public bool AddVertex(TNode vertex)
    {
        if (_adjacency.ContainsKey(vertex)) return false;
        _adjacency[vertex] = new Dictionary<TNode, double>(Comparer);
        return true;
    }

    public bool ContainsVertex(TNode vertex) => _adjacency.ContainsKey(vertex);

    public bool RemoveVertex(TNode vertex)
    {
        if (!_adjacency.TryGetValue(vertex, out var neighbours)) return false;
        foreach (var neighbour in neighbours.Keys)
            _adjacency[neighbour].Remove(vertex);
        EdgeCount -= neighbours.Count;
        _adjacency.Remove(vertex);
        return true;
    }

    /// <summary>Adds the edge, or updates its weight if it already exists. Missing vertices are created.</summary>
    public void AddEdge(TNode a, TNode b, double weight)
    {
        if (Comparer.Equals(a, b)) throw new ArgumentException("A mesh link must join two different nodes.");
        if (double.IsNaN(weight) || weight < 0) throw new ArgumentOutOfRangeException(nameof(weight), "Weight must be a non-negative number.");
        AddVertex(a);
        AddVertex(b);
        if (!_adjacency[a].ContainsKey(b)) EdgeCount++;
        _adjacency[a][b] = weight;
        _adjacency[b][a] = weight;
    }

    public bool RemoveEdge(TNode a, TNode b)
    {
        if (!_adjacency.TryGetValue(a, out var fromA) || !fromA.Remove(b)) return false;
        _adjacency[b].Remove(a);
        EdgeCount--;
        return true;
    }

    public bool HasEdge(TNode a, TNode b) => _adjacency.TryGetValue(a, out var n) && n.ContainsKey(b);

    public bool TryGetWeight(TNode a, TNode b, out double weight)
    {
        weight = 0;
        return _adjacency.TryGetValue(a, out var n) && n.TryGetValue(b, out weight);
    }

    /// <summary>Neighbours of a vertex with the weight of the connecting edge. O(1) to obtain, O(degree) to walk.</summary>
    public IReadOnlyDictionary<TNode, double> Neighbors(TNode vertex) =>
        _adjacency.TryGetValue(vertex, out var n) ? n : NoNeighbours;

    public int Degree(TNode vertex) => _adjacency.TryGetValue(vertex, out var n) ? n.Count : 0;

    /// <summary>Every undirected edge exactly once. O(V + E).</summary>
    public IEnumerable<WeightedEdge<TNode>> Edges()
    {
        var finished = new HashSet<TNode>(Comparer);
        foreach (var (u, neighbours) in _adjacency)
        {
            foreach (var (v, weight) in neighbours)
                if (!finished.Contains(v)) yield return new WeightedEdge<TNode>(u, v, weight);
            finished.Add(u);
        }
    }

    public double TotalWeight() => Edges().Sum(e => e.Weight);

    /// <summary>
    /// Copy containing only the vertices (and edges) that pass the filters — used to build the
    /// "active" mesh: online nodes joined by links that have not failed.
    /// </summary>
    public WeightedGraph<TNode> Subgraph(Func<TNode, bool> keepVertex, Func<TNode, TNode, bool>? keepEdge = null)
    {
        var copy = new WeightedGraph<TNode>(Comparer);
        foreach (var v in _adjacency.Keys)
            if (keepVertex(v)) copy.AddVertex(v);
        foreach (var e in Edges())
            if (copy.ContainsVertex(e.A) && copy.ContainsVertex(e.B) && (keepEdge is null || keepEdge(e.A, e.B)))
                copy.AddEdge(e.A, e.B, e.Weight);
        return copy;
    }
}
