using System.Diagnostics;
using SmartX.Shared;

namespace SmartX.Api.Services.Topology;

/// <summary>
/// Final PoE — the Network Topology &amp; Mesh Routing engine.
///
/// Holds the physical mesh as a <see cref="WeightedGraph{TNode}"/> (vertices = ESP32 MAC addresses,
/// edges = wireless links weighted by energy per delivered packet) and, on every request, derives the
/// "active" mesh: nodes that are online and not isolated, joined by links that have not failed.
/// On that active graph it runs:
///   • BFS — which devices can reach the gateway (or a chosen subnet), and in how many hops;
///   • DFS — the same reachability question answered branch by branch;
///   • Tarjan's articulation points — relays whose loss would cut part of the mesh off;
///   • Prim's and Kruskal's MST — the cheapest-energy backbone; telemetry is routed along it;
///   • Dijkstra — the cheapest path for one device, and a backup path that avoids its first relay.
/// Device status comes live from the Part 2 <see cref="CommandStreamEngine"/>, so a node that stops
/// sending heartbeats disappears from the active graph and the routes heal around it automatically.
/// It also owns the AVL-indexed telemetry history (<see cref="TelemetryHistoryIndex"/>).
/// Every public member takes one lock; each call does at most O(E log E) work on a small graph.
/// </summary>
public sealed class MeshTopologyEngine
{
    public const string GatewayMac = "24:6F:28:00:00:00";
    public const string GatewayName = "Smart-X Gateway";
    private const int EventCapacity = 14;

    private readonly object _gate = new();
    private readonly CommandStreamEngine _stream;
    private readonly Func<DateTime> _clock;
    private readonly TelemetryHistoryIndex _history;

    private readonly Dictionary<string, MeshNodeInfo> _nodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly WeightedGraph<string> _physical = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LinkInfo> _links = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _failedLinks = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _isolatedNodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _events = new();   // newest first

    private Dictionary<string, string> _lastParents = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _lastReachable = new(StringComparer.OrdinalIgnoreCase);
    private bool _analysedOnce;
    private DateTime _lastHistoryImportUtc = DateTime.MinValue;

    private sealed class MeshNodeInfo
    {
        public string Mac = string.Empty;
        public string Name = string.Empty;
        public string Category = string.Empty;
        public string Zone = string.Empty;
        public double X;
        public double Y;
        public bool IsGateway;
        public bool IsOnline;
    }

    /// <summary>Hysteresis band (dB): the link's effective RSSI only moves when the smoothed value drifts this far.</summary>
    public const double RssiHysteresisDb = 2.0;

    private sealed class LinkInfo
    {
        public string A = string.Empty;
        public string B = string.Empty;
        public double Distance;
        /// <summary>Exponential moving average of the reported RSSI.</summary>
        public double SmoothedRssi;
        /// <summary>The RSSI the routing actually uses (changes only outside the hysteresis band).</summary>
        public int Rssi;
        /// <summary>True while interference has pushed the cost well above the link's normal cost.</summary>
        public bool Degraded;
        public double Cost => MeshLayout.LinkCost(Distance, Rssi);
        public double NormalCost => MeshLayout.LinkCost(Distance, MeshLayout.EstimateRssi(Distance));

        public void ResetSignal()
        {
            Rssi = MeshLayout.EstimateRssi(Distance);
            SmoothedRssi = Rssi;
            Degraded = false;
        }
    }

    /// <summary>Everything derived from the active graph in one pass (used by the snapshot and the tests).</summary>
    private sealed record Analysis(
        WeightedGraph<string> Active, TraversalOutput<string> Bfs, List<WeightedEdge<string>> Prim,
        List<WeightedEdge<string>> Kruskal, HashSet<string> MstKeys, Dictionary<string, string> RouteParent,
        Dictionary<string, double> RouteCost, HashSet<string> ArticulationPoints,
        Dictionary<string, int> CutOffIfLost, int ComponentCount);

    public MeshTopologyEngine(CommandStreamEngine stream, Func<DateTime>? clock = null, bool seedHistory = true,
                              int historyCapacityPerSensor = TelemetryHistoryIndex.DefaultCapacityPerSensor)
    {
        _stream = stream;
        _clock = clock ?? (() => DateTime.UtcNow);
        _history = new TelemetryHistoryIndex(historyCapacityPerSensor);

        lock (_gate)
        {
            var (gx, gy) = MeshLayout.KnownPositions[GatewayMac];
            AddNodeLocked(new MeshNodeInfo
            {
                Mac = GatewayMac, Name = GatewayName, Category = "Gateway", Zone = "Plant Room",
                X = gx, Y = gy, IsGateway = true, IsOnline = true
            });
            SyncDevicesLocked(_stream.AllDevices());

            if (seedHistory)
            {
                // A week of 30-second readings per device (~20 000 per sensor, ~300 000 in total), ending where
                // the Part 2 live timeline begins; the live readings are then imported on every Sync().
                _history.SeedSynthetic(_stream.AllDevices(), _clock().AddMinutes(-11), TimeSpan.FromDays(7), TimeSpan.FromSeconds(30));
            }
        }
        Sync();
    }

    public TelemetryHistoryIndex History => _history;

    // =====================================================================================
    //  Live synchronisation with the Part 2 device registry
    // =====================================================================================

    /// <summary>
    /// Called once per second by <see cref="TopologyBroadcaster"/>: picks up new devices and online/offline
    /// changes from the command stream, copies new readings into the AVL history, and logs self-healing events.
    /// </summary>
    public void Sync()
    {
        var devices = _stream.AllDevices();
        var now = _clock();
        var window = _lastHistoryImportUtc == DateTime.MinValue
            ? TimeSpan.FromMinutes(15)
            : (now - _lastHistoryImportUtc) + TimeSpan.FromSeconds(5);
        window = window > TimeSpan.FromMinutes(15) ? TimeSpan.FromMinutes(15) : window;

        // Read the timelines before taking our own lock, so the two engine locks are never nested.
        var fresh = new List<(string Mac, List<TimelinePoint> Points)>();
        foreach (var device in devices)
            if (_stream.GetTimeline(device.MacAddress, window) is { Count: > 0 } points)
                fresh.Add((device.MacAddress, points));

        lock (_gate)
        {
            SyncDevicesLocked(devices);
            foreach (var (mac, points) in fresh)
                foreach (var p in points)
                    _history.Add(mac, p.TimestampUtc, p.Value);
            _lastHistoryImportUtc = now;
            DetectChangesLocked(AnalyseLocked());
        }
    }

    private void SyncDevicesLocked(IEnumerable<LiveDevice> devices)
    {
        var added = new List<MeshNodeInfo>();
        foreach (var device in devices)
        {
            if (!_nodes.TryGetValue(device.MacAddress, out var node))
            {
                string zone = MeshLayout.ZoneOf(device.Location);
                var (x, y) = MeshLayout.PositionFor(device.MacAddress, zone);
                node = new MeshNodeInfo { Mac = device.MacAddress, Zone = zone, X = x, Y = y };
                AddNodeLocked(node);
                added.Add(node);
                if (_analysedOnce) LogLocked($"➕ {device.DisplayName} joined the mesh in {zone}");
            }
            node.Name = device.DisplayName;
            node.Category = device.Category;
            node.IsOnline = device.IsOnline;
        }

        // Only after every new node is placed: a node that still hears nobody gets one long-range
        // link to its nearest neighbour, so it is never left completely outside the graph.
        foreach (var node in added.Where(n => _physical.Degree(n.Mac) == 0 && _nodes.Count > 1))
        {
            var nearest = _nodes.Values.Where(o => o.Mac != node.Mac)
                                       .MinBy(o => MeshLayout.Distance((node.X, node.Y), (o.X, o.Y)))!;
            AddLinkLocked(node.Mac, nearest.Mac, MeshLayout.Distance((node.X, node.Y), (nearest.X, nearest.Y)));
        }
    }

    /// <summary>Adds a vertex and an edge to every node already placed within radio range.</summary>
    private void AddNodeLocked(MeshNodeInfo node)
    {
        var existing = _nodes.Values.ToList();
        _nodes[node.Mac] = node;
        _physical.AddVertex(node.Mac);

        foreach (var other in existing)
        {
            double d = MeshLayout.Distance((node.X, node.Y), (other.X, other.Y));
            if (d <= MeshLayout.RadioRangeMeters) AddLinkLocked(node.Mac, other.Mac, d);
        }
    }

    private void AddLinkLocked(string a, string b, double distance)
    {
        var link = new LinkInfo { A = a, B = b, Distance = distance };
        link.ResetSignal();
        _links[LinkKey(a, b)] = link;
        _physical.AddEdge(a, b, link.Cost);
    }

    public static string LinkKey(string a, string b)
    {
        a = CommandStreamEngine.NormalizeMac(a);
        b = CommandStreamEngine.NormalizeMac(b);
        return string.CompareOrdinal(a, b) <= 0 ? $"{a}|{b}" : $"{b}|{a}";
    }

    // =====================================================================================
    //  Analysis of the active mesh
    // =====================================================================================

    private bool IsUpLocked(string mac) =>
        _nodes.TryGetValue(mac, out var n) && (n.IsGateway || (n.IsOnline && !_isolatedNodes.Contains(mac)));

    private WeightedGraph<string> ActiveGraphLocked() =>
        _physical.Subgraph(IsUpLocked, (a, b) => !_failedLinks.Contains(LinkKey(a, b)));

    private Analysis AnalyseLocked()
    {
        var active = ActiveGraphLocked();
        var bfs = GraphAlgorithms.BreadthFirst(active, GatewayMac);
        var prim = GraphAlgorithms.Prim(active, GatewayMac);
        var kruskal = GraphAlgorithms.Kruskal(active);
        var mstKeys = prim.Select(e => LinkKey(e.A, e.B)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Telemetry is routed along the MST: root the tree at the gateway and record each node's parent.
        var mstGraph = GraphAlgorithms.FromEdges(prim, active.Comparer);
        mstGraph.AddVertex(GatewayMac);
        var rooted = GraphAlgorithms.BreadthFirst(mstGraph, GatewayMac);
        var routeCost = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { [GatewayMac] = 0 };
        foreach (var mac in rooted.Order.Skip(1))
        {
            var parent = rooted.Parent[mac];
            mstGraph.TryGetWeight(mac, parent, out var w);
            routeCost[mac] = Math.Round(routeCost[parent] + w, 2);
        }

        // Single points of failure, and how many reachable nodes each one would strand.
        var articulation = GraphAlgorithms.ArticulationPoints(active);
        var reachable = bfs.Order.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var cutOff = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var ap in articulation)
        {
            if (ap.Equals(GatewayMac, StringComparison.OrdinalIgnoreCase) || !reachable.Contains(ap)) continue;
            var without = GraphAlgorithms.BreadthFirst(active, GatewayMac, v => !v.Equals(ap, StringComparison.OrdinalIgnoreCase));
            cutOff[ap] = reachable.Count - 1 - without.Order.Count;
        }

        int components = GraphAlgorithms.ConnectedComponents(active).Count;
        return new Analysis(active, bfs, prim, kruskal, mstKeys, rooted.Parent, routeCost, articulation, cutOff, components);
    }

    /// <summary>Compares the new routing tree with the last one and writes self-healing events.</summary>
    private void DetectChangesLocked(Analysis analysis)
    {
        var reachable = analysis.Bfs.Order.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (_analysedOnce)
        {
            foreach (var mac in _lastReachable)
            {
                if (reachable.Contains(mac) || !_nodes.TryGetValue(mac, out var node)) continue;
                LogLocked(IsUpLocked(mac)
                    ? $"✖ {node.Name} lost its route to the gateway (no active link path)"
                    : $"✖ {node.Name} went down — removed from the active mesh");
            }
            foreach (var mac in reachable)
            {
                if (mac.Equals(GatewayMac, StringComparison.OrdinalIgnoreCase)) continue;
                var name = _nodes[mac].Name;
                if (!_lastReachable.Contains(mac))
                {
                    LogLocked($"✔ {name} rejoined the mesh ({analysis.Bfs.Depth[mac]} hop{(analysis.Bfs.Depth[mac] == 1 ? "" : "s")} from the gateway)");
                }
                else if (analysis.RouteParent.TryGetValue(mac, out var parent) &&
                         _lastParents.TryGetValue(mac, out var oldParent) &&
                         !parent.Equals(oldParent, StringComparison.OrdinalIgnoreCase))
                {
                    LogLocked($"↻ {name} re-routed: now hops via {NameOf(parent)} instead of {NameOf(oldParent)}");
                }
            }
        }
        _lastReachable = reachable;
        _lastParents = new Dictionary<string, string>(analysis.RouteParent, StringComparer.OrdinalIgnoreCase);
        _analysedOnce = true;
    }

    private string NameOf(string mac) => _nodes.TryGetValue(mac, out var n) ? n.Name : mac;

    private void LogLocked(string message)
    {
        _events.AddFirst($"{_clock().ToLocalTime():HH:mm:ss}  {message}");
        while (_events.Count > EventCapacity) _events.RemoveLast();
    }

    // =====================================================================================
    //  Snapshot
    // =====================================================================================

    public TopologySnapshot GetSnapshot()
    {
        lock (_gate)
        {
            var a = AnalyseLocked();
            var reachable = a.Bfs.Order.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var childCount = a.RouteParent.Values.GroupBy(p => p, StringComparer.OrdinalIgnoreCase)
                                                 .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

            var nodes = _nodes.Values
                .OrderBy(n => n.IsGateway ? 0 : 1).ThenBy(n => n.Zone).ThenBy(n => n.Name)
                .Select(n =>
                {
                    var state = n.IsGateway ? MeshNodeState.Online
                              : !n.IsOnline ? MeshNodeState.Offline
                              : _isolatedNodes.Contains(n.Mac) ? MeshNodeState.Isolated
                              : reachable.Contains(n.Mac) ? MeshNodeState.Online
                              : MeshNodeState.Unreachable;
                    var role = n.IsGateway ? MeshRole.Gateway
                             : childCount.ContainsKey(n.Mac) ? MeshRole.Router
                             : MeshRole.Leaf;
                    return new MeshNodeDto(
                        n.Mac, n.Name, n.Category, n.Zone, role, state, n.X, n.Y,
                        a.Bfs.Depth.TryGetValue(n.Mac, out var hops) ? hops : null,
                        a.RouteParent.TryGetValue(n.Mac, out var parent) ? parent : null,
                        a.RouteCost.TryGetValue(n.Mac, out var cost) ? cost : null,
                        _physical.Degree(n.Mac),
                        a.ArticulationPoints.Contains(n.Mac) && reachable.Contains(n.Mac),
                        a.CutOffIfLost.GetValueOrDefault(n.Mac));
                })
                .ToList();

            var links = _links.Values
                .Select(l =>
                {
                    string key = LinkKey(l.A, l.B);
                    bool active = a.Active.HasEdge(l.A, l.B);
                    return new MeshLinkDto(l.A, l.B, l.Distance, l.Rssi, l.Cost, active, _failedLinks.Contains(key), a.MstKeys.Contains(key));
                })
                .OrderBy(l => l.Cost)
                .ToList();

            double activeCost = Math.Round(a.Active.TotalWeight(), 2);
            double primCost = Math.Round(a.Prim.Sum(e => e.Weight), 2);
            double kruskalCost = Math.Round(a.Kruskal.Sum(e => e.Weight), 2);
            var down = nodes.Where(n => n.State is MeshNodeState.Offline or MeshNodeState.Isolated).Select(n => n.MacAddress).ToList();
            var unreachable = nodes.Where(n => n.State == MeshNodeState.Unreachable).Select(n => n.MacAddress).ToList();

            var summary = new TopologySummary(
                NodeCount: nodes.Count,
                OnlineCount: nodes.Count(n => n.State != MeshNodeState.Offline && n.State != MeshNodeState.Isolated),
                ReachableCount: reachable.Count,
                LinkCount: links.Count,
                ActiveLinkCount: a.Active.EdgeCount,
                ComponentCount: a.ComponentCount,
                MaxHops: a.Bfs.Depth.Count == 0 ? 0 : a.Bfs.Depth.Values.Max(),
                UnreachableMacs: unreachable,
                DownMacs: down,
                ArticulationPoints: nodes.Where(n => n.IsArticulationPoint).Select(n => n.MacAddress).ToList(),
                TotalActiveLinkCost: activeCost,
                MstCost: primCost,
                MstEdgeCount: a.Prim.Count,
                SavingsPercent: activeCost <= 0 ? 0 : Math.Round(100 * (1 - primCost / activeCost), 1),
                PrimCost: primCost,
                KruskalCost: kruskalCost,
                PrimKruskalAgree: Math.Abs(primCost - kruskalCost) < 0.001,
                HistoryPointCount: _history.TotalPoints,
                HistorySensorCount: _history.SensorCount);

            return new TopologySnapshot(GatewayMac, MeshLayout.MapWidth, MeshLayout.MapHeight, MeshLayout.Zones.ToList(),
                nodes, links, summary, _events.ToList(), _clock());
        }
    }

    // =====================================================================================
    //  Route tracking (by MAC address)
    // =====================================================================================

    /// <summary>
    /// How telemetry from <paramref name="fromMac"/> travels to <paramref name="toMac"/> (the gateway by default):
    /// the MST route actually used for routing, the cheapest path Dijkstra can find, and a backup path that avoids
    /// the first relay — i.e. where the packet hops if that relay goes down.
    /// </summary>
    public RouteResult Route(string fromMac, string? toMac = null)
    {
        lock (_gate)
        {
            fromMac = CommandStreamEngine.NormalizeMac(fromMac);
            toMac = CommandStreamEngine.NormalizeMac(string.IsNullOrWhiteSpace(toMac) ? GatewayMac : toMac);

            if (!_nodes.ContainsKey(fromMac)) return NotFound(fromMac, toMac, $"No mesh node has MAC {fromMac}.");
            if (!_nodes.ContainsKey(toMac)) return NotFound(fromMac, toMac, $"No mesh node has MAC {toMac}.");
            if (!IsUpLocked(fromMac)) return NotFound(fromMac, toMac, $"{NameOf(fromMac)} is down, so it has no route right now.");
            if (!IsUpLocked(toMac)) return NotFound(fromMac, toMac, $"{NameOf(toMac)} is down.");

            var a = AnalyseLocked();
            var shortest = GraphAlgorithms.Dijkstra(a.Active, fromMac);
            var path = GraphAlgorithms.PathTo(fromMac, toMac, shortest.Previous, a.Active.Comparer);
            if (path is null)
                return NotFound(fromMac, toMac, $"{NameOf(fromMac)} cannot reach {NameOf(toMac)}: every path is broken.", shortest.Settled);

            // Route along the MST (the power-efficient backbone the mesh actually uses).
            var mst = GraphAlgorithms.FromEdges(a.Prim, a.Active.Comparer);
            var mstSearch = GraphAlgorithms.BreadthFirst(mst, fromMac);
            var mstPath = GraphAlgorithms.PathTo(fromMac, toMac, mstSearch.Parent, mst.Comparer);
            double? mstCost = mstPath is null ? null : Math.Round(PathCost(a.Active, mstPath), 2);

            // Backup: best path that avoids the first relay (only meaningful when the route has a relay).
            List<string>? backup = null;
            double? backupCost = null;
            if (path.Count > 2)
            {
                string firstRelay = path[1];
                var avoid = GraphAlgorithms.Dijkstra(a.Active, fromMac, v => !v.Equals(firstRelay, StringComparison.OrdinalIgnoreCase));
                backup = GraphAlgorithms.PathTo(fromMac, toMac, avoid.Previous, a.Active.Comparer);
                if (backup is not null) backupCost = Math.Round(avoid.Distance[toMac], 2);
            }

            var primary = mstPath ?? path;
            var hops = new List<RouteHop>();
            for (int i = 0; i + 1 < primary.Count; i++)
            {
                var link = _links[LinkKey(primary[i], primary[i + 1])];
                hops.Add(new RouteHop(primary[i], NameOf(primary[i]), primary[i + 1], NameOf(primary[i + 1]), link.Cost, link.Rssi));
            }

            double shortestCost = Math.Round(shortest.Distance[toMac], 2);
            string message = mstPath is not null && SamePath(mstPath, path)
                ? $"{NameOf(fromMac)} reaches {NameOf(toMac)} in {primary.Count - 1} hop(s); the MST route is also the cheapest path."
                : $"{NameOf(fromMac)} reaches {NameOf(toMac)} in {primary.Count - 1} hop(s) along the MST backbone.";

            return new RouteResult(fromMac, toMac, true, message,
                MstPath: primary, MstPathNames: primary.Select(NameOf).ToList(), Hops: hops, MstCost: mstCost ?? shortestCost,
                ShortestPath: path, ShortestPathNames: path.Select(NameOf).ToList(), ShortestCost: shortestCost,
                BackupPath: backup, BackupPathNames: backup?.Select(NameOf).ToList(), BackupCost: backupCost,
                NodesSettled: shortest.Settled);
        }
    }

    private static bool SamePath(List<string> x, List<string> y) =>
        x.Count == y.Count && x.Zip(y).All(p => p.First.Equals(p.Second, StringComparison.OrdinalIgnoreCase));

    private static double PathCost(WeightedGraph<string> graph, List<string> path)
    {
        double total = 0;
        for (int i = 0; i + 1 < path.Count; i++)
            if (graph.TryGetWeight(path[i], path[i + 1], out var w)) total += w;
        return total;
    }

    private RouteResult NotFound(string from, string to, string message, int settled = 0) =>
        new(from, to, false, message, new(), new(), new(), 0, new(), new(), 0, null, null, null, settled);

    // =====================================================================================
    //  BFS / DFS — which devices are connected to a gateway or subnet, and which are down
    // =====================================================================================

    /// <summary>
    /// Traverses the active mesh from <paramref name="startMac"/> (the gateway by default). If
    /// <paramref name="zone"/> is given, the traversal is limited to that subnet (zone) and starts from
    /// the zone node closest to the gateway. Devices that are not reached are reported with a reason and,
    /// where possible, the neighbour their packets should hop to.
    /// </summary>
    public TraversalResult Traverse(string algorithm, string? startMac = null, string? zone = null)
    {
        lock (_gate)
        {
            bool depthFirst = algorithm.Equals("dfs", StringComparison.OrdinalIgnoreCase);
            var a = AnalyseLocked();
            bool limitToZone = !string.IsNullOrWhiteSpace(zone);

            string start = CommandStreamEngine.NormalizeMac(string.IsNullOrWhiteSpace(startMac) ? GatewayMac : startMac);
            if (limitToZone)
            {
                bool startInZone = _nodes.TryGetValue(start, out var s) && s.Zone.Equals(zone, StringComparison.OrdinalIgnoreCase) && IsUpLocked(start);
                if (!startInZone)
                {
                    // Subnet entry point: the zone's node with the fewest hops from the gateway.
                    var entry = _nodes.Values
                        .Where(n => n.Zone.Equals(zone, StringComparison.OrdinalIgnoreCase) && a.Bfs.Depth.ContainsKey(n.Mac))
                        .OrderBy(n => a.Bfs.Depth[n.Mac]).ThenBy(n => n.Name).FirstOrDefault();
                    if (entry is not null) start = entry.Mac;
                }
            }

            Func<string, bool>? filter = limitToZone
                ? (string mac) => _nodes.TryGetValue(mac, out var n) && n.Zone.Equals(zone, StringComparison.OrdinalIgnoreCase)
                : null;

            var output = a.Active.ContainsVertex(start)
                ? (depthFirst ? GraphAlgorithms.DepthFirst(a.Active, start, filter) : GraphAlgorithms.BreadthFirst(a.Active, start, filter))
                : new TraversalOutput<string>(new(), new(), new());

            var steps = output.Order.Select((mac, i) => new TraversalStep(i + 1, mac, NameOf(mac), output.Depth[mac],
                output.Parent.TryGetValue(mac, out var p) ? p : null, false)).ToList();

            // Everything in scope that the traversal did not reach is "down" from this start's point of view.
            var visited = output.Order.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var reachableFromStart = a.Active.ContainsVertex(start)
                ? GraphAlgorithms.BreadthFirst(a.Active, start).Order.ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var down = new List<DownDeviceAdvice>();
            foreach (var n in _nodes.Values.Where(n => !n.IsGateway && !visited.Contains(n.Mac)).OrderBy(n => n.Name))
            {
                if (limitToZone && !n.Zone.Equals(zone, StringComparison.OrdinalIgnoreCase)) continue;

                string reason = !n.IsOnline ? "Offline — no heartbeat from the device"
                              : _isolatedNodes.Contains(n.Mac) ? "Isolated by an operator"
                              : limitToZone && reachableFromStart.Contains(n.Mac) ? "Online, but only reachable through another zone"
                              : "Online, but cut off: its relay is down or its links failed";

                // Suggested hop: the cheapest physical neighbour that the start node can still reach.
                var hop = _physical.Neighbors(n.Mac)
                    .Where(kv => reachableFromStart.Contains(kv.Key))
                    .OrderBy(kv => kv.Value)
                    .Select(kv => kv.Key)
                    .FirstOrDefault();
                down.Add(new DownDeviceAdvice(n.Mac, n.Name, reason, hop, hop is null ? null : NameOf(hop)));
            }

            string structure = depthFirst
                ? "Stack<T> (LIFO) — follows one branch to its end, then backtracks"
                : "Queue<T> (FIFO) — visits the mesh ring by ring, so depth = minimum hop count";
            return new TraversalResult(depthFirst ? "DFS" : "BFS", start, NameOf(start), steps, steps.Count, down, structure);
        }
    }

    // =====================================================================================
    //  Minimum spanning tree
    // =====================================================================================

    public MstResult MinimumSpanningTree(string algorithm)
    {
        lock (_gate)
        {
            bool kruskal = algorithm.Equals("kruskal", StringComparison.OrdinalIgnoreCase);
            var active = ActiveGraphLocked();

            var watch = Stopwatch.StartNew();
            var edges = kruskal ? GraphAlgorithms.Kruskal(active) : GraphAlgorithms.Prim(active, GatewayMac);
            watch.Stop();

            var dtos = edges.Select(e =>
            {
                var link = _links[LinkKey(e.A, e.B)];
                return new MeshLinkDto(e.A, e.B, link.Distance, link.Rssi, e.Weight, true, false, true);
            }).ToList();

            double total = Math.Round(edges.Sum(e => e.Weight), 2);
            double all = Math.Round(active.TotalWeight(), 2);
            int components = GraphAlgorithms.ConnectedComponents(active).Count;
            string structure = kruskal
                ? "All links sorted by cost + DisjointSet<T> (union–find) to reject loops"
                : "MinHeap<T> (binary heap) of frontier links, grown outward from the gateway";

            return new MstResult(kruskal ? "Kruskal" : "Prim", dtos, total, dtos.Count, active.VertexCount, components,
                all, all <= 0 ? 0 : Math.Round(100 * (1 - total / all), 1), structure, Math.Round(watch.Elapsed.TotalMicroseconds, 1));
        }
    }

    // =====================================================================================
    //  Operator actions (simulate failures to watch the mesh heal)
    // =====================================================================================

    public TopologyActionResult ToggleLink(string a, string b)
    {
        lock (_gate)
        {
            string key = LinkKey(a, b);
            if (!_links.TryGetValue(key, out var link))
                return new TopologyActionResult(false, "Those two nodes are not within radio range of each other.");

            string label = $"{NameOf(link.A)} ↔ {NameOf(link.B)}";
            if (_failedLinks.Remove(key))
            {
                LogLocked($"🔗 Link restored: {label}");
                DetectChangesLocked(AnalyseLocked());
                return new TopologyActionResult(true, $"Link restored: {label}");
            }
            _failedLinks.Add(key);
            LogLocked($"⚡ Link failed (operator test): {label}");
            DetectChangesLocked(AnalyseLocked());
            return new TopologyActionResult(true, $"Link failed: {label}. Routes re-calculated.");
        }
    }

    public TopologyActionResult ToggleNode(string mac)
    {
        lock (_gate)
        {
            mac = CommandStreamEngine.NormalizeMac(mac);
            if (mac == GatewayMac) return new TopologyActionResult(false, "The gateway is the root of the mesh and cannot be isolated.");
            if (!_nodes.TryGetValue(mac, out var node)) return new TopologyActionResult(false, $"No mesh node has MAC {mac}.");

            if (_isolatedNodes.Remove(mac))
            {
                LogLocked($"🔌 {node.Name} restored by operator");
                DetectChangesLocked(AnalyseLocked());
                return new TopologyActionResult(true, $"{node.Name} restored.");
            }
            _isolatedNodes.Add(mac);
            LogLocked($"🔌 {node.Name} isolated by operator (simulated power loss)");
            DetectChangesLocked(AnalyseLocked());
            return new TopologyActionResult(true, $"{node.Name} isolated. Watch its neighbours re-route.");
        }
    }

    public TopologyActionResult ResetFailures()
    {
        lock (_gate)
        {
            int count = _failedLinks.Count + _isolatedNodes.Count;
            _failedLinks.Clear();
            _isolatedNodes.Clear();
            foreach (var link in _links.Values) link.ResetSignal();
            RefreshWeightsLocked();
            LogLocked($"↺ Mesh reset: {count} simulated failure(s) cleared");
            DetectChangesLocked(AnalyseLocked());
            return new TopologyActionResult(true, $"Cleared {count} simulated failure(s).");
        }
    }

    // =====================================================================================
    //  Device uplink: neighbour tables with signal strength (ESP-NOW style)
    // =====================================================================================

    /// <summary>
    /// A device reports the RSSI it hears from each neighbour. Two guards stop "route flapping"
    /// (the MST flipping back and forth between two near-equal links because of radio noise):
    ///  1. an exponential moving average (α = 0.3) smooths each report;
    ///  2. hysteresis — the RSSI used for routing only changes when the smoothed value has moved
    ///     at least <see cref="RssiHysteresisDb"/> dB away from it.
    /// A real interference burst (−14 dB) passes both guards within a few reports. Returns how many links were updated.
    /// </summary>
    public int ReportNeighbors(NeighborReport report)
    {
        lock (_gate)
        {
            int updated = 0;
            foreach (var n in report.Neighbors)
            {
                if (!_links.TryGetValue(LinkKey(report.MacAddress, n.NeighborMac), out var link)) continue;
                if (n.Rssi is > -20 or < -120) continue;           // physically impossible reading — ignore

                link.SmoothedRssi = 0.7 * link.SmoothedRssi + 0.3 * n.Rssi;
                updated++;
                if (Math.Abs(link.SmoothedRssi - link.Rssi) < RssiHysteresisDb) continue;   // inside the band: keep routing stable

                link.Rssi = (int)Math.Round(link.SmoothedRssi);
                _physical.AddEdge(link.A, link.B, link.Cost);

                if (!link.Degraded && link.Cost > link.NormalCost * 1.5)
                {
                    link.Degraded = true;
                    LogLocked($"📶 Weak signal on {NameOf(link.A)} ↔ {NameOf(link.B)} ({link.Rssi} dBm) — link cost {link.NormalCost:0.##} → {link.Cost:0.##}");
                }
                else if (link.Degraded && link.Cost <= link.NormalCost * 1.2)
                {
                    link.Degraded = false;
                    LogLocked($"📶 Signal recovered on {NameOf(link.A)} ↔ {NameOf(link.B)} ({link.Rssi} dBm)");
                }
            }
            return updated;
        }
    }

    private void RefreshWeightsLocked()
    {
        foreach (var link in _links.Values) _physical.AddEdge(link.A, link.B, link.Cost);
    }

    // =====================================================================================
    //  Historical telemetry (AVL tree range queries)
    // =====================================================================================

    public HistoryQueryResult? QueryHistory(string mac, DateTime fromUtc, DateTime toUtc, int maxPoints = 600)
    {
        var device = _stream.GetDevice(mac);
        if (device is null) return null;
        lock (_gate) return _history.Query(device, fromUtc, toUtc, maxPoints);
    }

    public List<HistoryIndexInfo> DescribeHistory()
    {
        var devices = _stream.AllDevices();
        lock (_gate) return devices.Select(_history.Describe).Where(i => i.Size > 0).ToList();
    }

    // =====================================================================================
    //  Scale benchmark — how the structures behave far beyond the 15-node demo farm
    // =====================================================================================

    /// <summary>
    /// (1) Times an AVL range query against a linear scan of an unordered list holding the same readings.
    /// (2) Builds a random geometric mesh of <paramref name="meshNodes"/> nodes and times BFS, Prim, Kruskal and Dijkstra.
    /// </summary>
    public ScaleBenchmarkResult RunScaleBenchmark(int meshNodes = 2000, int seed = 7312)
    {
        meshNodes = Math.Clamp(meshNodes, 50, 20000);

        // ---- 1. History: AVL range query vs. linear scan ------------------------------------------
        AvlTree<DateTime, double>? tree = null;
        lock (_gate)
        {
            foreach (var mac in _history.Macs.ToList())
                if (_history.TryGetTree(mac, out var t) && (tree is null || t.Count > tree.Count))
                    tree = t;
        }

        int historyPoints = 0, height = 0, matches = 0, visited = 0;
        double avlMicros = 0, scanMicros = 0;
        long comparisons = 0;
        if (tree is not null)
        {
            List<KeyValuePair<DateTime, double>> unordered;
            DateTime rangeFrom, rangeTo;
            lock (_gate)
            {
                historyPoints = tree.Count;
                height = tree.Height;
                unordered = tree.InOrder().ToList();
                tree.TryGetMax(out var newest, out _);
                rangeTo = newest.AddHours(-24);
                rangeFrom = rangeTo.AddHours(-2);                       // "readings between 14:00 and 16:00 yesterday"
            }
            var rnd = new Random(seed);
            for (int i = unordered.Count - 1; i > 0; i--)     // shuffle: a massive, unorganised list
            {
                int j = rnd.Next(i + 1);
                (unordered[i], unordered[j]) = (unordered[j], unordered[i]);
            }

            const int runs = 50;
            var watch = Stopwatch.StartNew();
            for (int r = 0; r < runs; r++)
                lock (_gate) matches = tree.Range(rangeFrom, rangeTo, out visited).Count;
            watch.Stop();
            avlMicros = watch.Elapsed.TotalMicroseconds / runs;

            watch.Restart();
            int scanMatches = 0;
            for (int r = 0; r < runs; r++)
            {
                scanMatches = 0;
                foreach (var kv in unordered)
                    if (kv.Key >= rangeFrom && kv.Key <= rangeTo) scanMatches++;
            }
            watch.Stop();
            scanMicros = watch.Elapsed.TotalMicroseconds / runs;
            comparisons = unordered.Count;
        }

        // ---- 2. Graph algorithms on a large synthetic mesh -----------------------------------------
        var mesh = BuildRandomMesh(meshNodes, seed);
        var sw = Stopwatch.StartNew();
        GraphAlgorithms.BreadthFirst(mesh, 0);
        double bfsMs = sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        var prim = GraphAlgorithms.Prim(mesh, 0);
        double primMs = sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        var kruskal = GraphAlgorithms.Kruskal(mesh);
        double kruskalMs = sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        GraphAlgorithms.Dijkstra(mesh, 0);
        double dijkstraMs = sw.Elapsed.TotalMilliseconds;

        bool agree = Math.Abs(prim.Sum(e => e.Weight) - kruskal.Sum(e => e.Weight)) < 0.001;
        return new ScaleBenchmarkResult(historyPoints, height, matches, Math.Round(avlMicros, 1), Math.Round(scanMicros, 1),
            visited, comparisons, mesh.VertexCount, mesh.EdgeCount, Math.Round(bfsMs, 2), Math.Round(primMs, 2),
            Math.Round(kruskalMs, 2), Math.Round(dijkstraMs, 2), agree);
    }

    /// <summary>
    /// Random geometric graph: nodes scattered over a square field, linked when within radio range,
    /// weighted with the same energy model as the farm. A chain links consecutive nodes so the mesh is connected.
    /// </summary>
    public static WeightedGraph<int> BuildRandomMesh(int nodeCount, int seed)
    {
        var rnd = new Random(seed);
        double side = Math.Sqrt(nodeCount) * 18;             // keeps about 6 neighbours per node
        var points = new (double X, double Y)[nodeCount];
        for (int i = 0; i < nodeCount; i++) points[i] = (rnd.NextDouble() * side, rnd.NextDouble() * side);

        // Grid buckets so building the mesh is O(n) instead of O(n²).
        var cells = new Dictionary<(int, int), List<int>>();
        double cell = MeshLayout.RadioRangeMeters;
        for (int i = 0; i < nodeCount; i++)
        {
            var key = ((int)(points[i].X / cell), (int)(points[i].Y / cell));
            if (!cells.TryGetValue(key, out var bucket)) cells[key] = bucket = new List<int>();
            bucket.Add(i);
        }

        var graph = new WeightedGraph<int>();
        for (int i = 0; i < nodeCount; i++)
        {
            graph.AddVertex(i);
            int cx = (int)(points[i].X / cell), cy = (int)(points[i].Y / cell);
            for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            {
                if (!cells.TryGetValue((cx + dx, cy + dy), out var bucket)) continue;
                foreach (int j in bucket)
                {
                    if (j <= i) continue;
                    double d = MeshLayout.Distance(points[i], points[j]);
                    if (d <= MeshLayout.RadioRangeMeters && d > 0)
                        graph.AddEdge(i, j, MeshLayout.LinkCost(d, MeshLayout.EstimateRssi(d)));
                }
            }
        }
        // Join any separate islands so the benchmark mesh is one connected network (union–find, near O(n)).
        var islands = new DisjointSet<int>();
        for (int i = 0; i < nodeCount; i++) islands.MakeSet(i);
        foreach (var e in graph.Edges()) islands.Union(e.A, e.B);
        for (int i = 1; i < nodeCount; i++)
        {
            if (!islands.Union(i - 1, i)) continue;
            double d = Math.Max(MeshLayout.Distance(points[i - 1], points[i]), 1);
            graph.AddEdge(i - 1, i, MeshLayout.LinkCost(d, MeshLayout.EstimateRssi(d)));
        }
        return graph;
    }
}
