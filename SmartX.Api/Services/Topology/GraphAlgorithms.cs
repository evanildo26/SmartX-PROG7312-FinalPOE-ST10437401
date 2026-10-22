namespace SmartX.Api.Services.Topology;

/// <summary>Visit order plus the spanning tree a traversal discovered (depth and parent of each vertex).</summary>
public sealed record TraversalOutput<T>(List<T> Order, Dictionary<T, int> Depth, Dictionary<T, T> Parent) where T : notnull;

/// <summary>Single-source shortest paths: distance to every reached vertex and the previous hop on the best path.</summary>
public sealed record ShortestPaths<T>(T Source, Dictionary<T, double> Distance, Dictionary<T, T> Previous, int Settled) where T : notnull;

/// <summary>
/// Graph algorithms for the mesh. All are generic over the vertex type, so the same code runs on
/// MAC-address strings in the app and on integers in the scale benchmark.
/// </summary>
public static class GraphAlgorithms
{
    // =====================================================================================
    //  Breadth-first search — Queue<T>
    // =====================================================================================

    /// <summary>
    /// Visits vertices in rings of increasing hop count from <paramref name="start"/>.
    /// The first time BFS reaches a vertex it has found the path with the fewest hops, which is
    /// why hop counts on the topology map come from BFS. O(V + E).
    /// Neighbours are explored cheapest-link first so the order is deterministic and meaningful.
    /// </summary>
    public static TraversalOutput<T> BreadthFirst<T>(WeightedGraph<T> graph, T start, Func<T, bool>? canVisit = null) where T : notnull
    {
        var order = new List<T>();
        var depth = new Dictionary<T, int>(graph.Comparer);
        var parent = new Dictionary<T, T>(graph.Comparer);
        if (!graph.ContainsVertex(start)) return new TraversalOutput<T>(order, depth, parent);

        var visited = new HashSet<T>(graph.Comparer) { start };
        var queue = new Queue<T>();
        queue.Enqueue(start);
        depth[start] = 0;

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            order.Add(current);
            foreach (var (next, _) in graph.Neighbors(current).OrderBy(kv => kv.Value))
            {
                if (visited.Contains(next) || (canVisit is not null && !canVisit(next))) continue;
                visited.Add(next);
                depth[next] = depth[current] + 1;
                parent[next] = current;
                queue.Enqueue(next);
            }
        }
        return new TraversalOutput<T>(order, depth, parent);
    }

    // =====================================================================================
    //  Depth-first search — Stack<T> (iterative, so a long chain of relays cannot overflow the call stack)
    // =====================================================================================

    /// <summary>
    /// Follows one branch of the mesh as deep as it goes before backtracking. O(V + E).
    /// Neighbours are pushed most-expensive first so the cheapest link is popped (explored) first.
    /// </summary>
    public static TraversalOutput<T> DepthFirst<T>(WeightedGraph<T> graph, T start, Func<T, bool>? canVisit = null) where T : notnull
    {
        var order = new List<T>();
        var depth = new Dictionary<T, int>(graph.Comparer);
        var parent = new Dictionary<T, T>(graph.Comparer);
        if (!graph.ContainsVertex(start)) return new TraversalOutput<T>(order, depth, parent);

        var visited = new HashSet<T>(graph.Comparer);
        var stack = new Stack<(T Vertex, T From, int Depth)>();
        stack.Push((start, start, 0));

        while (stack.Count > 0)
        {
            var (current, from, level) = stack.Pop();
            if (!visited.Add(current)) continue;           // already reached by another branch

            order.Add(current);
            depth[current] = level;
            if (!graph.Comparer.Equals(current, from)) parent[current] = from;

            foreach (var (next, _) in graph.Neighbors(current).OrderByDescending(kv => kv.Value))
                if (!visited.Contains(next) && (canVisit is null || canVisit(next)))
                    stack.Push((next, current, level + 1));
        }
        return new TraversalOutput<T>(order, depth, parent);
    }

    /// <summary>Splits the graph into its connected islands (each found with one BFS). O(V + E).</summary>
    public static List<List<T>> ConnectedComponents<T>(WeightedGraph<T> graph) where T : notnull
    {
        var seen = new HashSet<T>(graph.Comparer);
        var components = new List<List<T>>();
        foreach (var vertex in graph.Vertices)
        {
            if (seen.Contains(vertex)) continue;
            var island = BreadthFirst(graph, vertex).Order;
            seen.UnionWith(island);
            components.Add(island);
        }
        return components;
    }

    // =====================================================================================
    //  Articulation points (Tarjan's low-link DFS)
    // =====================================================================================

    /// <summary>
    /// Vertices whose failure would split the mesh — single points of failure. A vertex u is one
    /// if some DFS child v cannot climb back above u through a back-edge (low[v] ≥ disc[u]),
    /// or if u is a DFS root with two or more children. O(V + E).
    /// </summary>
    public static HashSet<T> ArticulationPoints<T>(WeightedGraph<T> graph) where T : notnull
    {
        var discovered = new Dictionary<T, int>(graph.Comparer);
        var low = new Dictionary<T, int>(graph.Comparer);
        var result = new HashSet<T>(graph.Comparer);
        int time = 0;

        foreach (var root in graph.Vertices.ToList())
            if (!discovered.ContainsKey(root))
                Visit(root, root, isRoot: true);
        return result;

        void Visit(T u, T parent, bool isRoot)
        {
            discovered[u] = time;
            low[u] = time;
            time++;
            int children = 0;

            foreach (var v in graph.Neighbors(u).Keys)
            {
                if (!discovered.ContainsKey(v))
                {
                    children++;
                    Visit(v, u, isRoot: false);
                    low[u] = Math.Min(low[u], low[v]);
                    if (!isRoot && low[v] >= discovered[u]) result.Add(u);
                }
                else if (!graph.Comparer.Equals(v, parent))
                {
                    low[u] = Math.Min(low[u], discovered[v]);   // back-edge to an ancestor
                }
            }
            if (isRoot && children > 1) result.Add(u);
        }
    }

    // =====================================================================================
    //  Minimum spanning tree — Prim's algorithm (binary min-heap)
    // =====================================================================================

    /// <summary>
    /// Grows the tree outward from <paramref name="start"/> (the gateway), always adding the cheapest
    /// link that reaches a new node. Uses a binary <see cref="MinHeap{TElement,TPriority}"/> with lazy
    /// deletion: O(E log E). If the mesh is split, it restarts from each unreached vertex, producing a
    /// minimum spanning forest. Edges are returned in the order they were accepted.
    /// </summary>
    public static List<WeightedEdge<T>> Prim<T>(WeightedGraph<T> graph, T start) where T : notnull
    {
        var inTree = new HashSet<T>(graph.Comparer);
        var tree = new List<WeightedEdge<T>>();
        var heap = new MinHeap<WeightedEdge<T>, double>();

        var roots = new List<T>();
        if (graph.ContainsVertex(start)) roots.Add(start);
        roots.AddRange(graph.Vertices);

        foreach (var root in roots)
        {
            if (!inTree.Add(root)) continue;
            foreach (var (next, weight) in graph.Neighbors(root))
                heap.Push(new WeightedEdge<T>(root, next, weight), weight);

            while (heap.TryPop(out var edge, out _))
            {
                if (inTree.Contains(edge.B)) continue;      // stale entry: B was reached more cheaply already
                inTree.Add(edge.B);
                tree.Add(edge);
                foreach (var (next, weight) in graph.Neighbors(edge.B))
                    if (!inTree.Contains(next))
                        heap.Push(new WeightedEdge<T>(edge.B, next, weight), weight);
            }
        }
        return tree;
    }

    // =====================================================================================
    //  Minimum spanning tree — Kruskal's algorithm (sorted edges + union–find)
    // =====================================================================================

    /// <summary>
    /// Sorts every link by cost and accepts each one that joins two different islands, which the
    /// <see cref="DisjointSet{T}"/> answers in near O(1). O(E log E), dominated by the sort.
    /// </summary>
    public static List<WeightedEdge<T>> Kruskal<T>(WeightedGraph<T> graph) where T : notnull
    {
        var edges = graph.Edges().ToList();
        edges.Sort((x, y) => x.Weight.CompareTo(y.Weight));

        var sets = new DisjointSet<T>(graph.Comparer);
        foreach (var vertex in graph.Vertices) sets.MakeSet(vertex);

        var tree = new List<WeightedEdge<T>>();
        foreach (var edge in edges)
        {
            if (sets.Union(edge.A, edge.B)) tree.Add(edge);
            if (tree.Count == graph.VertexCount - 1) break;   // a spanning tree has exactly V - 1 edges
        }
        return tree;
    }

    // =====================================================================================
    //  Shortest paths — Dijkstra's algorithm (binary min-heap)
    // =====================================================================================

    /// <summary>
    /// Cheapest-energy paths from <paramref name="source"/>. O((V + E) log V).
    /// <paramref name="canVisit"/> lets callers forbid vertices, e.g. "route around this relay".
    /// </summary>
    public static ShortestPaths<T> Dijkstra<T>(WeightedGraph<T> graph, T source, Func<T, bool>? canVisit = null) where T : notnull
    {
        var distance = new Dictionary<T, double>(graph.Comparer);
        var previous = new Dictionary<T, T>(graph.Comparer);
        var settled = new HashSet<T>(graph.Comparer);
        if (!graph.ContainsVertex(source)) return new ShortestPaths<T>(source, distance, previous, 0);

        var heap = new MinHeap<T, double>();
        distance[source] = 0;
        heap.Push(source, 0);

        while (heap.TryPop(out var current, out var cost))
        {
            if (!settled.Add(current)) continue;            // stale heap entry
            foreach (var (next, weight) in graph.Neighbors(current))
            {
                if (settled.Contains(next) || (canVisit is not null && !canVisit(next))) continue;
                double candidate = cost + weight;
                if (!distance.TryGetValue(next, out var known) || candidate < known)
                {
                    distance[next] = candidate;
                    previous[next] = current;
                    heap.Push(next, candidate);
                }
            }
        }
        return new ShortestPaths<T>(source, distance, previous, settled.Count);
    }

    /// <summary>Rebuilds the path source → target from a predecessor map. Null if the target was not reached.</summary>
    public static List<T>? PathTo<T>(T source, T target, IReadOnlyDictionary<T, T> previous, IEqualityComparer<T> comparer) where T : notnull
    {
        var path = new List<T> { target };
        var current = target;
        int guard = previous.Count + 1;
        while (!comparer.Equals(current, source))
        {
            if (!previous.TryGetValue(current, out var before) || guard-- <= 0) return null;
            path.Add(before);
            current = before;
        }
        path.Reverse();
        return path;
    }

    /// <summary>Builds a graph containing only the given edges (e.g. the MST) so it can be traversed.</summary>
    public static WeightedGraph<T> FromEdges<T>(IEnumerable<WeightedEdge<T>> edges, IEqualityComparer<T> comparer) where T : notnull
    {
        var graph = new WeightedGraph<T>(comparer);
        foreach (var e in edges) graph.AddEdge(e.A, e.B, e.Weight);
        return graph;
    }
}
