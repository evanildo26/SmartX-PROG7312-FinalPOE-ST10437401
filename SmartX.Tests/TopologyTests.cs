using SmartX.Api.Services;
using SmartX.Api.Services.Topology;
using SmartX.Shared;

namespace SmartX.Tests;

/// <summary>
/// Final PoE — proves the tree, heap, graph and MST structures behind the Network Topology pillar.
/// The first group tests each data structure on its own; the second runs the topology engine on the
/// real seeded farm (16 nodes, 26 radio links) with a fixed clock so every result is deterministic.
/// </summary>
public class TopologyTests
{
    private static readonly DateTime Now = new(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);
    private static readonly string Gw = MeshTopologyEngine.GatewayMac;

    // =====================================================================================
    //  AVL tree (historical telemetry)
    // =====================================================================================

    [Fact]
    public void Avl_tree_stays_balanced_when_timestamps_arrive_in_order()
    {
        var tree = new AvlTree<DateTime, double>();
        for (int i = 0; i < 20_000; i++) tree.Insert(Now.AddSeconds(i), i);

        Assert.Equal(20_000, tree.Count);
        Assert.True(tree.IsValidAvl());
        // A plain BST would be 20 000 levels deep here; AVL guarantees height < 1.44·log2(n + 2).
        Assert.True(tree.Height <= (int)(1.44 * Math.Log2(tree.Count + 2)), $"height {tree.Height} is too tall");
    }

    [Fact]
    public void Avl_range_query_returns_exactly_the_readings_in_the_window_in_time_order()
    {
        var tree = new AvlTree<DateTime, double>();
        var day = Now.Date;
        var minutes = Enumerable.Range(0, 24 * 60).OrderBy(i => (i * 7919) % 1440).ToList();   // shuffled arrival
        foreach (int m in minutes) tree.Insert(day.AddMinutes(m), m);

        var result = tree.Range(day.AddHours(14), day.AddHours(16), out int visited);

        Assert.Equal(121, result.Count);                                   // 14:00 … 16:00 inclusive, one per minute
        Assert.Equal(day.AddHours(14), result[0].Key);
        Assert.Equal(day.AddHours(16), result[^1].Key);
        Assert.True(result.Zip(result.Skip(1)).All(p => p.First.Key < p.Second.Key));
        Assert.True(visited < tree.Count / 4, $"visited {visited} of {tree.Count} nodes — the tree was not pruned");
    }

    [Fact]
    public void Avl_insert_with_an_existing_timestamp_updates_instead_of_duplicating()
    {
        var tree = new AvlTree<DateTime, double>();
        tree.Insert(Now, 41.5);
        tree.Insert(Now, 42.0);

        Assert.Equal(1, tree.Count);
        Assert.True(tree.TryGetValue(Now, out var value));
        Assert.Equal(42.0, value);
    }

    [Fact]
    public void Avl_delete_and_remove_min_keep_the_tree_valid_and_sorted()
    {
        var tree = new AvlTree<int, int>();
        var rnd = new Random(42);
        var keys = Enumerable.Range(0, 2000).OrderBy(_ => rnd.Next()).ToList();
        foreach (int k in keys) tree.Insert(k, k);
        foreach (int k in keys.Take(700)) Assert.True(tree.Remove(k));
        Assert.True(tree.RemoveMin());

        Assert.Equal(1299, tree.Count);
        Assert.True(tree.IsValidAvl());
        var inOrder = tree.InOrder().Select(kv => kv.Key).ToList();
        Assert.Equal(inOrder.OrderBy(k => k), inOrder);
        Assert.False(tree.Remove(keys[0]));                                // already gone
    }

    // =====================================================================================
    //  Heap and union–find
    // =====================================================================================

    [Fact]
    public void Min_heap_always_pops_the_smallest_priority()
    {
        var heap = new MinHeap<string, double>();
        var rnd = new Random(7);
        for (int i = 0; i < 500; i++) heap.Push($"item{i}", Math.Round(rnd.NextDouble() * 100, 3));
        Assert.True(heap.IsValidHeap());

        var popped = new List<double>();
        while (heap.TryPop(out _, out var priority)) popped.Add(priority);

        Assert.Equal(500, popped.Count);
        Assert.Equal(popped.OrderBy(p => p), popped);
    }

    [Fact]
    public void Disjoint_set_tracks_islands_and_rejects_loops()
    {
        var sets = new DisjointSet<string>();
        foreach (var n in new[] { "A", "B", "C", "D" }) sets.MakeSet(n);

        Assert.True(sets.Union("A", "B"));
        Assert.True(sets.Union("C", "D"));
        Assert.Equal(2, sets.SetCount);
        Assert.True(sets.Union("B", "C"));
        Assert.False(sets.Union("A", "D"));                                // would close a cycle
        Assert.Equal(1, sets.SetCount);
        Assert.True(sets.Connected("A", "D"));
    }

    // =====================================================================================
    //  Graph algorithms on small hand-made graphs
    // =====================================================================================

    /// <summary>GW–A–B–C chain plus a cheap-but-long detour GW–D–E–C.</summary>
    private static WeightedGraph<string> Diamond()
    {
        var g = new WeightedGraph<string>();
        g.AddEdge("GW", "A", 5);
        g.AddEdge("A", "B", 5);
        g.AddEdge("B", "C", 5);
        g.AddEdge("GW", "D", 1);
        g.AddEdge("D", "E", 1);
        g.AddEdge("E", "F", 1);
        g.AddEdge("F", "C", 1);
        return g;
    }

    [Fact]
    public void Bfs_finds_minimum_hop_counts()
    {
        var bfs = GraphAlgorithms.BreadthFirst(Diamond(), "GW");

        Assert.Equal(7, bfs.Order.Count);
        Assert.Equal("GW", bfs.Order[0]);
        Assert.Equal(3, bfs.Depth["C"]);                                   // GW → A → B → C (3 hops) beats 4 hops via D
        Assert.Equal(1, bfs.Depth["D"]);
    }

    [Fact]
    public void Dfs_follows_one_branch_to_the_end_before_backtracking()
    {
        var dfs = GraphAlgorithms.DepthFirst(Diamond(), "GW");

        Assert.Equal(7, dfs.Order.Distinct().Count());
        // Cheapest link first: GW → D → E → F → C, then C → B → A before the walk ever returns to GW.
        Assert.Equal(new[] { "GW", "D", "E", "F", "C", "B", "A" }, dfs.Order);
        Assert.Equal(6, dfs.Depth["A"]);
    }

    [Fact]
    public void Dijkstra_finds_the_cheapest_route_not_the_fewest_hops()
    {
        var g = Diamond();
        var paths = GraphAlgorithms.Dijkstra(g, "C");
        var route = GraphAlgorithms.PathTo("C", "GW", paths.Previous, g.Comparer);

        Assert.Equal(new[] { "C", "F", "E", "D", "GW" }, route);           // 4 hops, cost 4 — cheaper than 3 hops costing 15
        Assert.Equal(4, paths.Distance["GW"]);
    }

    [Fact]
    public void Articulation_points_are_the_relays_whose_loss_splits_the_mesh()
    {
        var g = new WeightedGraph<string>();
        g.AddEdge("GW", "A", 1);
        g.AddEdge("A", "B", 1);
        g.AddEdge("B", "GW", 1);      // triangle: no single point of failure inside it
        g.AddEdge("B", "R", 1);       // R hangs off B …
        g.AddEdge("R", "S", 1);       // … and S hangs off R

        var points = GraphAlgorithms.ArticulationPoints(g);

        Assert.True(points.SetEquals(new[] { "B", "R" }), string.Join(", ", points));
    }

    [Fact]
    public void Prim_and_Kruskal_build_the_same_minimum_spanning_tree_cost_on_a_large_mesh()
    {
        var mesh = MeshTopologyEngine.BuildRandomMesh(800, seed: 11);
        var prim = GraphAlgorithms.Prim(mesh, 0);
        var kruskal = GraphAlgorithms.Kruskal(mesh);

        Assert.Equal(mesh.VertexCount - 1, prim.Count);                    // a spanning tree has V − 1 edges
        Assert.Equal(mesh.VertexCount - 1, kruskal.Count);
        Assert.Equal(kruskal.Sum(e => e.Weight), prim.Sum(e => e.Weight), 6);
        Assert.True(prim.Sum(e => e.Weight) < mesh.TotalWeight());

        // Prim's edges must contain no cycle: every edge has to join two different islands.
        var sets = new DisjointSet<int>();
        foreach (var v in mesh.Vertices) sets.MakeSet(v);
        Assert.All(prim, e => Assert.True(sets.Union(e.A, e.B)));
    }

    // =====================================================================================
    //  Topology engine on the seeded farm
    // =====================================================================================

    private static (CommandStreamEngine Stream, MeshTopologyEngine Topology) NewFarm(bool seedHistory = false)
    {
        var stream = new CommandStreamEngine(new ActionPatternEngine(), () => Now);
        FleetSeeder.Seed(stream, Now);
        return (stream, new MeshTopologyEngine(stream, () => Now, seedHistory));
    }

    [Fact]
    public void Seeded_mesh_is_fully_reachable_and_prim_matches_kruskal()
    {
        var (_, topology) = NewFarm();
        var snap = topology.GetSnapshot();

        Assert.Equal(16, snap.Summary.NodeCount);                           // gateway + 15 ESP32 devices
        Assert.Equal(16, snap.Summary.ReachableCount);
        Assert.Equal(26, snap.Summary.LinkCount);
        Assert.Equal(15, snap.Summary.MstEdgeCount);
        Assert.True(snap.Summary.PrimKruskalAgree);
        Assert.True(snap.Summary.SavingsPercent > 50, "the MST should need far less energy than using every link");
        Assert.Equal(3, snap.Summary.MaxHops);
    }

    [Fact]
    public void Single_points_of_failure_are_detected_on_the_farm()
    {
        var (_, topology) = NewFarm();
        var snap = topology.GetSnapshot();
        var names = snap.Nodes.Where(n => n.IsArticulationPoint).Select(n => n.DisplayName).ToHashSet();

        Assert.True(names.SetEquals(new[]
        {
            "Smart Meter — Main Board", "Smart Meter — Pump House", "Humidity Node 1",
            "Irrigation Valve Line 2", "Greenhouse 2 Temp"
        }), string.Join(", ", names));
        Assert.Equal(3, snap.Nodes.Single(n => n.MacAddress == FleetSeeder.MeterMain).NodesCutOffIfLost);
    }

    [Fact]
    public void Route_tracking_by_mac_shows_mst_route_cheapest_path_and_backup_hop()
    {
        var (_, topology) = NewFarm();
        var route = topology.Route("24:6f:28:a1:00:02");                    // Moisture Sensor B, lower-case on purpose

        Assert.True(route.Found);
        Assert.Equal(FleetSeeder.MoistureB, route.MstPath[0]);
        Assert.Equal(Gw, route.MstPath[^1]);
        Assert.Equal(new[] { FleetSeeder.MoistureB, FleetSeeder.RelayGrowLights, FleetSeeder.PlugGrowLights, Gw }, route.ShortestPath);
        Assert.True(route.ShortestCost <= route.MstCost);                   // Dijkstra is never beaten on a single route

        Assert.NotNull(route.BackupPath);                                   // if the grow-light relay fails …
        Assert.DoesNotContain(FleetSeeder.RelayGrowLights, route.BackupPath!);
        Assert.Equal(Gw, route.BackupPath![^1]);                            // … the sensor still reaches the gateway
    }

    [Fact]
    public void Mesh_heals_around_a_failed_relay()
    {
        var (_, topology) = NewFarm();
        Assert.Contains(FleetSeeder.ValveLine1, topology.Route(FleetSeeder.MoistureA).ShortestPath);

        topology.ToggleNode(FleetSeeder.ValveLine1);                        // simulated power loss
        var healed = topology.Route(FleetSeeder.MoistureA);

        Assert.True(healed.Found);
        Assert.DoesNotContain(FleetSeeder.ValveLine1, healed.ShortestPath);
        Assert.DoesNotContain(FleetSeeder.ValveLine1, healed.MstPath);
        Assert.Equal(15, topology.GetSnapshot().Summary.ReachableCount);    // everyone else still reaches the gateway
        Assert.Contains(topology.GetSnapshot().RecentEvents, e => e.Contains("isolated"));
    }

    [Fact]
    public void Failed_link_reroutes_zone_2_through_the_main_meter()
    {
        var (_, topology) = NewFarm();
        topology.ToggleLink(Gw, FleetSeeder.ValveLine2);

        var route = topology.Route(FleetSeeder.MoistureC);

        Assert.True(route.Found);
        Assert.Equal(new[] { FleetSeeder.MoistureC, FleetSeeder.TempGh2, FleetSeeder.ValveLine2, FleetSeeder.MeterMain, Gw }, route.MstPath);
        Assert.Contains(topology.GetSnapshot().Links, l => l.IsFailed);
    }

    [Fact]
    public void Bfs_reports_devices_cut_off_when_their_only_relay_is_down()
    {
        var (_, topology) = NewFarm();
        topology.ToggleNode(FleetSeeder.MeterMain);

        var bfs = topology.Traverse("bfs");

        Assert.Equal(12, bfs.VisitedCount);                                 // 16 − main meter − pump house (3 nodes)
        var downMacs = bfs.Down.Select(d => d.MacAddress).ToList();
        Assert.Contains(FleetSeeder.MeterMain, downMacs);
        Assert.Contains(FleetSeeder.PumpA, downMacs);
        Assert.Contains(FleetSeeder.PumpB, downMacs);
        Assert.Contains(FleetSeeder.MeterPumpHouse, downMacs);
        Assert.StartsWith("Isolated", bfs.Down.Single(d => d.MacAddress == FleetSeeder.MeterMain).Reason);
    }

    [Fact]
    public void Subnet_traversal_stays_inside_the_zone()
    {
        var (_, topology) = NewFarm();
        var zone2 = topology.Traverse("dfs", zone: "Zone 2");

        Assert.Equal(FleetSeeder.ValveLine2, zone2.StartMac);               // the Zone 2 node closest to the gateway
        Assert.Equal(3, zone2.VisitedCount);                                // Valve Line 2, Greenhouse 2 Temp, Moisture C
        Assert.Empty(zone2.Down);
    }

    [Fact]
    public void Weak_signal_reports_raise_link_cost_and_the_mst_routes_around_it()
    {
        var (_, topology) = NewFarm();
        bool InMst() => topology.GetSnapshot().Links.Single(l =>
            MeshTopologyEngine.LinkKey(l.A, l.B) == MeshTopologyEngine.LinkKey(FleetSeeder.MeterPumpHouse, FleetSeeder.PumpA)).InMst;
        Assert.True(InMst());

        for (int i = 0; i < 10; i++)                                         // ten noisy −90 dBm reports from Pump A
            topology.ReportNeighbors(new NeighborReport(FleetSeeder.PumpA, new() { new NeighborRssi(FleetSeeder.MeterPumpHouse, -90) }));

        Assert.False(InMst());
        Assert.True(topology.GetSnapshot().Summary.PrimKruskalAgree);
    }

    [Fact]
    public void Small_radio_noise_does_not_make_routes_flap()
    {
        var (_, topology) = NewFarm();
        var before = topology.GetSnapshot();
        var rnd = new Random(3);

        // 50 rounds of ±1 dB noise on every link, reported by both ends — typical of a healthy mesh.
        for (int round = 0; round < 50; round++)
            foreach (var link in before.Links)
            {
                int noise() => rnd.Next(-1, 2);
                topology.ReportNeighbors(new NeighborReport(link.A, new() { new NeighborRssi(link.B, link.Rssi + noise()) }));
                topology.ReportNeighbors(new NeighborReport(link.B, new() { new NeighborRssi(link.A, link.Rssi + noise()) }));
            }

        var after = topology.GetSnapshot();
        Assert.Equal(before.Summary.MstCost, after.Summary.MstCost);
        var mstBefore = before.Links.Where(l => l.InMst).Select(l => MeshTopologyEngine.LinkKey(l.A, l.B)).OrderBy(k => k);
        var mstAfter = after.Links.Where(l => l.InMst).Select(l => MeshTopologyEngine.LinkKey(l.A, l.B)).OrderBy(k => k);
        Assert.True(mstBefore.SequenceEqual(mstAfter), "the MST changed although only noise was reported");
        Assert.DoesNotContain(after.RecentEvents, e => e.Contains("re-routed"));
    }

    [Fact]
    public void History_range_query_reads_from_the_avl_index()
    {
        var (_, topology) = NewFarm(seedHistory: true);
        var from = Now.AddDays(-1);                                         // 12:00 UTC = 14:00 SAST yesterday
        var to = from.AddHours(2);

        var result = topology.QueryHistory(FleetSeeder.MoistureA, from, to, maxPoints: 1000)!;

        Assert.Equal(241, result.Count);                                    // one reading every 30 s, both ends inclusive
        Assert.All(result.Points, p => Assert.InRange(p.TimestampUtc, from, to));
        Assert.True(result.TreeSize > 20_000);                              // a week of history
        Assert.True(result.NodesVisited < result.TreeSize / 20);
        Assert.InRange(result.Min!.Value, 0, 100);
    }

    [Fact]
    public void Device_registered_later_joins_the_mesh_with_a_radio_link()
    {
        var (stream, topology) = NewFarm();
        stream.RegisterDevice(new LiveDevice
        {
            MacAddress = "24:6F:28:D4:00:40", DisplayName = "Custom Environmental", Category = DeviceCategories.Environmental,
            Location = "Facility A/Zone 2/Sub-Zone D", Metric = "value"
        });
        stream.SeedHistory("24:6F:28:D4:00:40", new[] { (Now, 50.0) });     // first packet → online

        topology.Sync();
        var node = topology.GetSnapshot().Nodes.Single(n => n.MacAddress == "24:6F:28:D4:00:40");

        Assert.Equal("Zone 2", node.Zone);
        Assert.True(node.Degree >= 1);
        Assert.NotNull(node.HopCount);
    }
}
