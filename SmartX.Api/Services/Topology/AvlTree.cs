namespace SmartX.Api.Services.Topology;

/// <summary>
/// Self-balancing binary search tree (AVL tree, Adelson-Velsky &amp; Landis).
///
/// Every node keeps its height; after an insert or delete, any node whose left and right
/// heights differ by more than 1 is fixed with one or two rotations. This guarantees the
/// height stays below 1.44·log₂(n + 2), so search, insert and delete are O(log n) even when
/// keys arrive in sorted order — which is exactly how timestamped telemetry arrives. A plain
/// BST would degrade into a linked list (O(n)) on that input.
///
/// Range(from, to) walks only the part of the tree that can contain matches and returns the
/// k matching entries already in order in O(log n + k) time.
/// </summary>
public sealed class AvlTree<TKey, TValue> where TKey : IComparable<TKey>
{
    private sealed class Node
    {
        public Node(TKey key, TValue value)
        {
            Key = key;
            Value = value;
        }

        public TKey Key;
        public TValue Value;
        public Node? Left;
        public Node? Right;
        public int Height = 1;
    }

    private Node? _root;

    public int Count { get; private set; }
    public int Height => HeightOf(_root);
    public bool IsEmpty => _root is null;

    // =====================================================================================
    //  Insert / update
    // =====================================================================================

    /// <summary>Adds the key, or replaces the value if the key already exists (same timestamp = same reading).</summary>
    public void Insert(TKey key, TValue value) => _root = Insert(_root, key, value);

    private Node Insert(Node? node, TKey key, TValue value)
    {
        if (node is null)
        {
            Count++;
            return new Node(key, value);
        }

        int cmp = key.CompareTo(node.Key);
        if (cmp < 0) node.Left = Insert(node.Left, key, value);
        else if (cmp > 0) node.Right = Insert(node.Right, key, value);
        else
        {
            node.Value = value;
            return node;
        }
        return Rebalance(node);
    }

    // =====================================================================================
    //  Delete
    // =====================================================================================

    public bool Remove(TKey key)
    {
        int before = Count;
        _root = Remove(_root, key);
        return Count < before;
    }

    private Node? Remove(Node? node, TKey key)
    {
        if (node is null) return null;

        int cmp = key.CompareTo(node.Key);
        if (cmp < 0) node.Left = Remove(node.Left, key);
        else if (cmp > 0) node.Right = Remove(node.Right, key);
        else
        {
            if (node.Left is null || node.Right is null)
            {
                Count--;
                return node.Left ?? node.Right;
            }
            // Two children: copy the in-order successor here, then delete the successor.
            var successor = node.Right;
            while (successor.Left is not null) successor = successor.Left;
            node.Key = successor.Key;
            node.Value = successor.Value;
            node.Right = Remove(node.Right, successor.Key);
        }
        return Rebalance(node);
    }

    /// <summary>Removes the smallest key (the oldest reading). Used to cap memory per sensor.</summary>
    public bool RemoveMin()
    {
        if (_root is null) return false;
        var node = _root;
        while (node.Left is not null) node = node.Left;
        return Remove(node.Key);
    }

    // =====================================================================================
    //  Lookups
    // =====================================================================================

    public bool TryGetValue(TKey key, out TValue value)
    {
        var node = _root;
        while (node is not null)
        {
            int cmp = key.CompareTo(node.Key);
            if (cmp == 0)
            {
                value = node.Value;
                return true;
            }
            node = cmp < 0 ? node.Left : node.Right;
        }
        value = default!;
        return false;
    }

    public bool TryGetMin(out TKey key, out TValue value)
    {
        var node = _root;
        while (node?.Left is not null) node = node.Left;
        return Output(node, out key, out value);
    }

    public bool TryGetMax(out TKey key, out TValue value)
    {
        var node = _root;
        while (node?.Right is not null) node = node.Right;
        return Output(node, out key, out value);
    }

    private static bool Output(Node? node, out TKey key, out TValue value)
    {
        if (node is null)
        {
            key = default!;
            value = default!;
            return false;
        }
        key = node.Key;
        value = node.Value;
        return true;
    }

    /// <summary>
    /// All entries with from &lt;= key &lt;= to, in ascending order. Iterative in-order walk with pruning:
    /// a node smaller than <paramref name="from"/> means its whole left subtree is too small, so it is skipped;
    /// the walk stops at the first key larger than <paramref name="to"/>.
    /// <paramref name="nodesVisited"/> reports how many tree nodes were touched (≈ log n + k).
    /// </summary>
    public List<KeyValuePair<TKey, TValue>> Range(TKey from, TKey to, out int nodesVisited)
    {
        var result = new List<KeyValuePair<TKey, TValue>>();
        nodesVisited = 0;
        if (from.CompareTo(to) > 0) return result;

        var stack = new Stack<Node>();
        var node = _root;
        while (stack.Count > 0 || node is not null)
        {
            if (node is not null)
            {
                nodesVisited++;
                if (node.Key.CompareTo(from) < 0)
                {
                    node = node.Right;          // this node and everything on its left are before the range
                    continue;
                }
                stack.Push(node);
                node = node.Left;
            }
            else
            {
                node = stack.Pop();
                if (node.Key.CompareTo(to) > 0) break;   // in-order: everything after this is later still
                result.Add(new KeyValuePair<TKey, TValue>(node.Key, node.Value));
                node = node.Right;
            }
        }
        return result;
    }

    /// <summary>All entries in ascending key order (in-order traversal).</summary>
    public IEnumerable<KeyValuePair<TKey, TValue>> InOrder()
    {
        var stack = new Stack<Node>();
        var node = _root;
        while (stack.Count > 0 || node is not null)
        {
            while (node is not null)
            {
                stack.Push(node);
                node = node.Left;
            }
            node = stack.Pop();
            yield return new KeyValuePair<TKey, TValue>(node.Key, node.Value);
            node = node.Right;
        }
    }

    /// <summary>
    /// Checks the three AVL invariants on every node: BST ordering, correct stored heights,
    /// and a balance factor of -1, 0 or +1. Used by the unit tests.
    /// </summary>
    public bool IsValidAvl() => Validate(_root, default!, false, default!, false) >= 0;

    private static int Validate(Node? node, TKey min, bool hasMin, TKey max, bool hasMax)
    {
        if (node is null) return 0;
        if (hasMin && node.Key.CompareTo(min) <= 0) return -1;
        if (hasMax && node.Key.CompareTo(max) >= 0) return -1;
        int left = Validate(node.Left, min, hasMin, node.Key, true);
        int right = Validate(node.Right, node.Key, true, max, hasMax);
        if (left < 0 || right < 0 || Math.Abs(left - right) > 1) return -1;
        int height = 1 + Math.Max(left, right);
        return height == node.Height ? height : -1;
    }

    // =====================================================================================
    //  Balancing
    // =====================================================================================

    private static int HeightOf(Node? node) => node?.Height ?? 0;
    private static int BalanceOf(Node node) => HeightOf(node.Left) - HeightOf(node.Right);
    private static void UpdateHeight(Node node) => node.Height = 1 + Math.Max(HeightOf(node.Left), HeightOf(node.Right));

    private static Node Rebalance(Node node)
    {
        UpdateHeight(node);
        int balance = BalanceOf(node);

        if (balance > 1)                                   // left-heavy
        {
            if (BalanceOf(node.Left!) < 0)                 // left-right case
                node.Left = RotateLeft(node.Left!);
            return RotateRight(node);                      // left-left case
        }
        if (balance < -1)                                  // right-heavy
        {
            if (BalanceOf(node.Right!) > 0)                // right-left case
                node.Right = RotateRight(node.Right!);
            return RotateLeft(node);                       // right-right case (sorted timestamps land here)
        }
        return node;
    }

    private static Node RotateRight(Node y)
    {
        var x = y.Left!;
        y.Left = x.Right;
        x.Right = y;
        UpdateHeight(y);
        UpdateHeight(x);
        return x;
    }

    private static Node RotateLeft(Node x)
    {
        var y = x.Right!;
        x.Right = y.Left;
        y.Left = x;
        UpdateHeight(x);
        UpdateHeight(y);
        return y;
    }
}
