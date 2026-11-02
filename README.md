# Smart-X IoT Mesh Ecosystem — Final PoE (Parts 1, 2 & 3)

Smart-X is a gateway suite for a fleet of ESP32 microcontrollers in a hydroponic farm and smart-grid installation. The devices are simulated, but every one talks to the API over HTTP exactly as real hardware would. The suite has three pillars, all active:

| Pillar | Part | What it does |
|---|---|---|
| 📡 **Sensor Data Ingestion & Telemetry** | Part 1 | Register devices, push telemetry with a generic `TelemetryPacket<T>`, upload configuration logs, validate deployment paths recursively. |
| ⏱ **Real-Time Command Stream & History** | Part 2 | FIFO and priority queues, manual overrides with stack-based undo, O(1) device dictionary, sorted timelines, duplicate-free alert sets, and a predictive action engine. |
| 🕸 **Network Topology & Mesh Routing** | **Final PoE** | The live mesh as a weighted graph. It provides BFS/DFS reachability, Prim's and Kruskal's minimum spanning tree for power-efficient routing, route tracking by MAC address, self-healing when nodes fail, and an AVL tree for range queries over a week of history. |

**Author:** Evanildo Julio Chaves Jose · ST10437401 · PROG7312 · [GitHub repository](https://github.com/evanildo26/SmartX-PROG7312-FinalPOE_ST10437401)

---

## Contents

1. [Solution layout and architecture](#1-solution-layout-and-architecture)
2. [Restore, build, run](#2-restore-build-run)
3. [Using the application suite](#3-using-the-application-suite)
4. [Final PoE data structures in depth](#4-final-poe-data-structures-in-depth)
5. [How performance scales (measured)](#5-how-performance-scales-measured)
6. [API reference](#6-api-reference)
7. [Seeded fleet and mesh layout](#7-seeded-fleet-and-mesh-layout)
8. [Feedback, tests and troubleshooting](#8-feedback-tests-and-troubleshooting)

---

## 1. Solution layout and architecture

| Project | Role |
|---|---|
| `SmartX.Api` | ASP.NET Core Minimal API on **.NET 10**. Hosts the Part 2 `CommandStreamEngine` and the Part 3 `MeshTopologyEngine`, background services, the SignalR hub and all REST endpoints. |
| `SmartX.Client` | **Blazor WebAssembly** dashboard. Live data arrives over one SignalR connection; commands go over REST. |
| `SmartX.Shared` | Contracts shared by every project (`TelemetryPacket<T>`, `LiveDevice`, `StreamSnapshot`, `TopologySnapshot`, …). |
| `SmartX.Simulator` | **ESP32 fleet emulator**. Each device POSTs readings, polls its command outbox, and reports its neighbours' signal strength (RSSI). |
| `SmartX.Tests` | **46 xUnit tests**: 24 for Part 2 and 22 for the trees, heap, graphs, MST and self-healing. |

```
                                   ┌─────────────────────────── SmartX.Api (.NET 10) ───────────────────────────┐
ESP32 fleet (Simulator)            │  CommandStreamEngine (Part 2)          MeshTopologyEngine (Final PoE)       │
  POST /api/stream/ingest ────────▶│  Queue<T> + PriorityQueue ──▶ Dictionary<MAC,LiveDevice> ──status──▶ WeightedGraph<MAC>
  GET  /api/stream/outbox/{mac} ◀──│  Stack<T> undo · HashSet alerts         │   BFS (Queue) · DFS (Stack)        │
  POST /api/topology/neighbors ───▶│  SortedDictionary timelines ──readings──┼─▶ AvlTree<DateTime,double> history │
                                   │  ActionPatternEngine                    │   Prim (MinHeap) · Kruskal (DisjointSet)
                                   │                                         │   Dijkstra · Tarjan articulation   │
Blazor dashboard ◀── SignalR /hubs/telemetry: "snapshot" + "topology" every 1 s ───────────────────────────────┤
                 ──── REST: commands, search, route tracking, traversals, history queries ───────────────────▶└
```

Part 3 is built **on top of** Part 2, not beside it. `TopologyBroadcaster` runs once per second:
- It asks the command stream which devices are online, so a node that stops sending heartbeats disappears from the graph and the routes heal around it.
- It copies new readings into the AVL history.
- It pushes a `topology` snapshot over the same SignalR connection that carries the Part 2 `snapshot`.

---

## 2. Restore, build, run

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). `dotnet --version` should print `10.x`.
- Optional: Visual Studio 2026 / Rider / VS Code with C# Dev Kit. No Docker or database is needed: all state is in memory and seeded at start-up.

### Command line

```bash
# 1. Restore NuGet packages and compile all five projects
dotnet restore SmartX.slnx
dotnet build SmartX.slnx

# 2. Run the 46 unit tests (expect: total 46, failed 0)
dotnet test SmartX.Tests

# 3. Trust the HTTPS development certificate (first time only)
dotnet dev-certs https --trust

# 4. Terminal 1 — gateway API (http://localhost:5261 and https://localhost:7290)
dotnet run --project SmartX.Api --launch-profile https

# 5. Terminal 2 — ESP32 fleet simulator (readings, command polling, neighbour RSSI reports)
dotnet run --project SmartX.Simulator

# 6. Terminal 3 — Blazor dashboard (https://localhost:7192)
dotnet run --project SmartX.Client --launch-profile https
```

Open **https://localhost:7192**. The API console prints `Now listening on: http://localhost:5261`. On first start it seeds the 15-device fleet, about three weeks of operator behaviour, and **one week of 30-second history per device**: about 300 000 readings in 15 AVL trees, in well under a second.

> The client reads the API address from `SmartX.Client/wwwroot/appsettings.json` (`ApiBaseUrl`, default `http://localhost:5261`). Change it there if your API uses another port; no recompile is needed.

### Visual Studio

1. Open `SmartX.slnx`.
2. Choose the **"Smart-X full stack"** launch profile, which starts Api + Client + Simulator. If it is missing, use **Configure Startup Projects → Multiple startup projects** and set those three to *Start*.
3. Press **F5**. Run the tests from **Test → Run All Tests**.

---

## 3. Using the application suite

The **Control Centre** shows all three pillars with live numbers. The sidebar works from any page and keeps state: the SignalR connection, filters, the selected device, the tracked route, the chosen MST algorithm and the history range all live in session-scoped services, not in the pages. The top bar shows the link state (Live / Polling / Reconnecting / Offline). Critical alerts appear as toasts on every page. A badge on **Network Topology** counts nodes that are down or cut off.

### 3.1 Pillar 1 — Sensor Ingestion & Telemetry (Part 1)

Register a sensor with a MAC, location and category; push readings; upload a log file; validate a deployment path such as `Facility A/Zone 1/Sub-Zone B` (recursive tree validation). A sensor registered here also joins the Part 2 live registry and the Part 3 mesh graph. It is placed inside its zone and linked to every node in radio range.

### 3.2 Pillar 2 — Real-Time Command Stream (Part 2)

| Feature | How to test it |
|---|---|
| `Queue<T>` + priority queue | Click **⚡ Burst test**. 600 routine packets queue up, then one critical packet. The red **PRIORITY** row waits a few ms; routine packets wait seconds. |
| `Stack<T>` undo | Turn both irrigation valves ON → **Shut down all valves** → **↶ Undo** once. Both come back ON (one stack entry). |
| `Dictionary` O(1) lookup | `curl http://localhost:5261/api/stream/devices/24:6f:28:a1:00:02` (case-insensitive key). |
| `SortedDictionary` timeline | Click a sensor; the chart is drawn from its time-ordered timeline (2/5/10 min). |
| `HashSet` de-duplication | Stop the simulator. After 8 s each node appears in **Disconnected nodes** once; the "duplicates blocked" counter climbs instead. |
| Predictive engine | Wait for **Turn ON Pump A** under *Suggested Actions*, read the reason, click **Run now**. Moisture B starts rising. |

The full Part 2 walkthrough, with curl commands for every feature, is in `SmartX.Api/SmartX.Api.http` and [docs/DATA-STRUCTURES.md](docs/DATA-STRUCTURES.md).

### 3.3 Pillar 3 — Network Topology & Mesh Routing (Final PoE)

Open **Network Topology & Mesh Routing** in the sidebar.

| What you see / do | Data structure / algorithm |
|---|---|
| **KPI strip.** Nodes reachable from the gateway, deepest hop count, single points of failure, MST energy compared with using every link, the Prim = Kruskal check, and readings indexed. | All of the below |
| **Live mesh map.** Each ESP32 sits at its surveyed position on the farm map (metres). Lines are wireless links. The number inside a node is its hop count from the gateway. A dashed amber ring marks a single point of failure. | `WeightedGraph<string>` |
| **View: All links.** Links coloured by signal quality (green strong → red weak). | Edge weights from RSSI |
| **View: MST backbone.** The minimum spanning tree that telemetry is routed along. Switch between **Prim's** and **Kruskal's**; the totals always match. | `MinHeap`, `DisjointSet` |
| **Track a node's route.** Type a MAC (e.g. `24:6F:28:A1:00:02`) or a name, or click a node. The route is drawn hop by hop with RSSI and cost. It is compared with the cheapest path (Dijkstra) and a **backup hop** that avoids the first relay. | MST path, Dijkstra |
| **Connected devices (BFS / DFS).** Run either traversal from the gateway, from the selected node, or inside one **subnet** (zone). The visit order is animated. The panel lists every device that is **down or cut off**, with the reason and the neighbour to hop to. | `Queue<T>`, `Stack<T>` |
| **Simulate node failure / click a link.** Isolate a relay or break a link. BFS, the MST, routes and hop counts are recalculated at once, and the **Self-healing events** log shows who re-routed through whom. **Reset simulated failures** restores everything. | Graph re-analysis |
| **Resilience panel.** Articulation points ranked by how many nodes each would cut off. | Tarjan's DFS |
| **Historical telemetry log.** Pick a sensor and a range (1 h, 6 h, 24 h, 7 days, **Yesterday 14:00–16:00**, or any from/to). It shows min/max/average, a chart and a time-ordered log, plus proof of the tree's efficiency: *"20 301 readings · height 15 · visited 255 nodes vs 20 301 for a linear scan · 9 µs"*. | `AvlTree<DateTime,double>` |
| **Performance at scale.** Runs the same code on a 2 000-node mesh and the largest history tree. | Benchmark |

**Suggested 3-minute demo:**
1. Track **Moisture Sensor B**. Its MST route and its cheapest path can differ, and the backup hop is shown.
2. Click **Simulate failure** on **Smart Meter — Main Board**. The three pump-house nodes turn amber ("cut off"). Run **BFS**: 12 connected, 4 down, each with a reason.
3. Reset. Click the **Gateway ↔ Valve 2** link to fail it. Zone 2 re-routes through the main meter (watch the events).
4. In the history log choose **Yesterday 14:00–16:00** for Moisture Sensor A.
5. Click **Run benchmark**.

The simulator also causes real events. A random node drops out now and then, and a radio-interference burst (−14 dB for about 25 s) occasionally hits one link. The link's cost rises, the MST routes around it, and it switches back when the signal recovers.

---

## 4. Final PoE data structures in depth

All Part 3 structures are **hand-written and generic**, in `SmartX.Api/Services/Topology/`:

| File | Purpose |
|---|---|
| `AvlTree.cs` | Self-balancing binary search tree (history) |
| `WeightedGraph.cs` | Adjacency-list graph (the mesh) |
| `MinHeap.cs` | Binary heap (Prim, Dijkstra) |
| `DisjointSet.cs` | Union–find (Kruskal) |
| `GraphAlgorithms.cs` | BFS, DFS, components, articulation points, Prim, Kruskal, Dijkstra |
| `MeshTopologyEngine.cs` | Ties the structures to the live fleet |
| `TelemetryHistoryIndex.cs` | One AVL tree per sensor |

The algorithms are generic over the vertex type, so the same code runs on MAC-address strings in the app and on integers in the 50 000-node benchmark.

### 4.1 Trees — AVL tree for historical telemetry (`AvlTree<TKey,TValue>`)

**Role.** The brief asks to *"pull all moisture readings between 14:00 and 16:00 for Node A without iterating through a massive, unorganised list."* Each sensor's history is an AVL tree keyed by timestamp (`TelemetryHistoryIndex`). It holds a week of 30-second readings (about 20 000 per sensor), plus every live reading copied in from Part 2 each second, capped at 60 000 per sensor (`RemoveMin` evicts the oldest).

**Why a self-balancing tree.** Telemetry arrives **in time order**. In a plain BST every new key is larger than the last, so each insert goes to the right and the tree degenerates into a linked list of height *n*; every query becomes O(n). An AVL tree stores a height in each node and repairs any node whose subtrees differ by more than one level with a rotation. The height is guaranteed to stay below 1.44·log₂(n+2): **15** for 20 000 readings and **20** for a million. A `List<T>` would need a full scan per query, and a `SortedDictionary` (a red-black tree) has no range query in its public API.

```csharp
// AvlTree.cs — insert recurses down, then rebalances on the way back up (O(log n))
private Node Insert(Node? node, TKey key, TValue value)
{
    if (node is null) { Count++; return new Node(key, value); }
    int cmp = key.CompareTo(node.Key);
    if (cmp < 0) node.Left = Insert(node.Left, key, value);
    else if (cmp > 0) node.Right = Insert(node.Right, key, value);
    else { node.Value = value; return node; }          // same timestamp = same reading: update
    return Rebalance(node);
}

private static Node Rebalance(Node node)
{
    UpdateHeight(node);
    int balance = BalanceOf(node);                     // height(left) − height(right)
    if (balance > 1)  { if (BalanceOf(node.Left!) < 0)  node.Left  = RotateLeft(node.Left!);  return RotateRight(node); }
    if (balance < -1) { if (BalanceOf(node.Right!) > 0) node.Right = RotateRight(node.Right!); return RotateLeft(node); }
    return node;                                       // sorted timestamps trigger the right-right case → one left rotation
}
```

**The range query** walks the tree in order but **prunes**. When a node is earlier than `from`, its entire left subtree is earlier too, so the walk jumps straight to the right child. It stops at the first key after `to`. Cost: O(log n + k). It touches about k matching nodes plus two root-to-leaf paths, whatever the size of the tree.

```csharp
// AvlTree.Range — iterative in-order walk with pruning
while (stack.Count > 0 || node is not null)
{
    if (node is not null)
    {
        nodesVisited++;
        if (node.Key.CompareTo(from) < 0) { node = node.Right; continue; }  // skip everything too early
        stack.Push(node); node = node.Left;
    }
    else
    {
        node = stack.Pop();
        if (node.Key.CompareTo(to) > 0) break;                             // everything after is too late
        result.Add(new(node.Key, node.Value));
        node = node.Right;
    }
}
```

Unit tests check every AVL invariant on every node (`IsValidAvl`: BST order, stored heights and balance factor of −1/0/+1) after 20 000 sorted inserts and after 700 random deletes. They also check that a 14:00–16:00 query returns exactly the 121 minute-readings in order while visiting less than a quarter of the tree.

### 4.2 Graphs — the mesh as a weighted graph (`WeightedGraph<TNode>`)

**Role.** Vertices are ESP32 MAC addresses (plus the gateway `24:6F:28:00:00:00`). Edges are the wireless links between nodes within 26 m of each other: 26 links among 16 nodes on the seeded farm.

**Why an adjacency list.** A mesh is sparse: each node hears only a few neighbours. `Dictionary<TNode, Dictionary<TNode,double>>` uses O(V + E) memory instead of an O(V²) matrix. "Who are my neighbours?", the question every traversal asks, costs O(degree), and a single link lookup is still O(1).

**Edge weight = energy per delivered packet** (`MeshLayout.LinkCost`):
- RSSI comes from the log-distance path-loss model: −40 dBm − 27·log₁₀(d).
- The packet reception ratio falls from 100 % at −70 dBm to 10 % near −92 dBm.
- Transmit energy grows with the square of the distance, and lost packets are resent.

So `cost = (d/10)² ÷ PRR`. A long or noisy link is expensive, and that is what makes the MST a *power-efficient* backbone.

```csharp
public static int EstimateRssi(double d) => (int)Math.Round(-40 - 10 * PathLossExponent * Math.Log10(Math.Max(d, 1)));
public static double PacketReceptionRatio(int rssi) => Math.Clamp((rssi + 95) / 25.0, 0.1, 1.0);
public static double LinkCost(double d, int rssi) => Math.Round(Math.Max(0.5, Math.Pow(d / 10.0, 2)) / PacketReceptionRatio(rssi), 2);
```

The engine keeps the **physical** graph and, on every analysis, derives the **active** graph with `Subgraph(...)`: online, non-isolated nodes joined by links that have not failed. Every algorithm below runs on the active graph, which is why the mesh "heals" automatically.

**Live signal and route flapping.** Every 5 s each simulated ESP32 reports the RSSI it hears from each neighbour (`POST /api/topology/neighbors`). Raw radio noise would make the MST flip between near-equal links several times a minute. Each link's RSSI is therefore smoothed (exponential moving average) and only changes for routing when it moves **≥ 2 dB** (hysteresis). A real −14 dB interference burst still re-routes within seconds. The test `Small_radio_noise_does_not_make_routes_flap` proves it.

### 4.3 Graph traversal — BFS (`Queue<T>`) and DFS (`Stack<T>`)

**BFS** answers *"which devices are connected to this gateway or subnet, and how many hops away?"* It visits the mesh ring by ring, so the first time it reaches a node is along the fewest hops. The hop numbers on the map come from BFS. Anything registered but not reached is reported as **down** with a reason, plus the cheapest still-reachable neighbour to hop to:
- *offline* (no heartbeat);
- *isolated* (by an operator);
- *cut off* (its relay is down or its links failed).

```csharp
// GraphAlgorithms.BreadthFirst — O(V + E)
var queue = new Queue<T>(); queue.Enqueue(start); depth[start] = 0;
while (queue.Count > 0)
{
    var current = queue.Dequeue();
    order.Add(current);
    foreach (var (next, _) in graph.Neighbors(current).OrderBy(kv => kv.Value))   // strongest link first
    {
        if (visited.Contains(next) || (canVisit is not null && !canVisit(next))) continue;   // canVisit = subnet filter
        visited.Add(next); depth[next] = depth[current] + 1; parent[next] = current; queue.Enqueue(next);
    }
}
```

**DFS** uses an explicit `Stack<T>` rather than recursion, so a long chain of relays can never overflow the call stack. The page animates both traversals so the FIFO vs LIFO visit order is visible. A **subnet** traversal (`?zone=Zone 2`) starts from the zone node closest to the gateway and stays inside the zone.

**Articulation points** (Tarjan's low-link DFS, O(V + E)) are the relays whose failure would split the mesh. The farm has five of them; for example, if *Smart Meter — Main Board* fails, the three pump-house nodes lose the gateway.

### 4.4 Minimum spanning tree — Prim's and Kruskal's

**Role.** The brief: *"demonstrate the most power-efficient routing path for telemetry data to hop through the mesh nodes to reach the central server."* The MST is the cheapest set of links that still connects every node. On the farm it uses **15 of 26 links** and about **59 % less energy** than keeping every link active. The MST is rooted at the gateway, and each node's parent in that tree is its next hop. That is the route shown when you track a MAC, and the **Router / Leaf** role of each node comes from it.

**Prim's algorithm** grows the tree outward from the gateway, always adding the cheapest link that reaches a new node. A hand-written **binary min-heap** returns that link in O(log E). Stale entries are skipped when popped (lazy deletion). Total O(E log E).

```csharp
// GraphAlgorithms.Prim
inTree.Add(root);
foreach (var (next, w) in graph.Neighbors(root)) heap.Push(new WeightedEdge<T>(root, next, w), w);
while (heap.TryPop(out var edge, out _))
{
    if (inTree.Contains(edge.B)) continue;          // already reached more cheaply
    inTree.Add(edge.B); tree.Add(edge);
    foreach (var (next, w) in graph.Neighbors(edge.B))
        if (!inTree.Contains(next)) heap.Push(new WeightedEdge<T>(edge.B, next, w), w);
}
```

**Kruskal's algorithm** sorts every link by cost and accepts each one that joins two different islands. A **disjoint-set (union–find)** with path compression and union by rank answers "would this create a loop?" in near-constant time. Total O(E log E), dominated by the sort.

```csharp
// GraphAlgorithms.Kruskal
edges.Sort((x, y) => x.Weight.CompareTo(y.Weight));
foreach (var edge in edges)
{
    if (sets.Union(edge.A, edge.B)) tree.Add(edge);   // false = both ends already connected → would form a cycle
    if (tree.Count == graph.VertexCount - 1) break;  // a spanning tree has exactly V − 1 edges
}
```

Both run on every snapshot, and the dashboard shows **Prim = Kruskal ✓**. Tests check on an 800-node mesh that both produce V − 1 edges, equal totals and no cycle. If failures split the mesh, both return a minimum spanning **forest** and the page says how many islands there are.

**MST route vs cheapest path.** The MST minimises the energy of the *whole network*; Dijkstra minimises the cost of *one* route. They usually agree but not always. For Moisture Sensor B the MST route goes through the misting valve, while the single cheapest path goes through the grow-light relay. The route panel shows both, plus the **backup path** that avoids the first relay: "where to hop if it goes down".

### 4.5 Heaps — `MinHeap<TElement,TPriority>`

The heap is an array-backed complete binary tree. The parent of index *i* is `(i − 1) / 2`, and its children are `2i + 1` and `2i + 2`. `Push` sifts up and `Pop` moves the last leaf to the root and sifts down, so each is O(log n) because an item moves along only one root-to-leaf path. Prim's MST and Dijkstra both rely on it. A test pushes 500 random priorities, checks the heap property (`IsValidHeap`), and checks that items pop in ascending order.

---

## 5. How performance scales (measured)

Measured with .NET 10 (Debug build, so a Release build is faster still) on a 2-core Linux container, averaging repeated runs. You can reproduce the 2 000-node row live with **Run benchmark** on the Topology page or `GET /api/topology/benchmark?nodes=2000`.

**AVL range query (a 2-hour window ≈ 241 readings) compared with scanning an unordered list:**

| Readings (n) | AVL height | 1.44·log₂ n | Insert all n | Nodes visited | AVL query | Linear scan | Speed-up |
|---|---|---|---|---|---|---|---|
| 1 000 | 10 | 14.4 | 1 ms | 251 | 11 µs | 25 µs | 2× |
| 10 000 | 14 | 19.1 | 4 ms | 252 | ~11–25 µs | 224 µs | ≈ 9–20× |
| 100 000 | 17 | 23.9 | 51 ms | 259 | 11 µs | 2 320 µs | ≈ 200× |
| 1 000 000 | 20 | 28.7 | 643 ms | 260 | 9 µs | 20 438 µs | ≈ 2 270× |

From 1 000 to a million readings, the tree height grows by only 10 levels and the query keeps touching about 255 nodes, so its time stays flat at about 10 µs. The linear scan grows in direct proportion to *n*. This is O(log n + k) against O(n).

**Graph algorithms on random geometric meshes (about 3 links per node, the same energy model):**

| Nodes (V) | Links (E) | BFS | DFS | Prim | Kruskal | Dijkstra | Prim = Kruskal |
|---|---|---|---|---|---|---|---|
| 500 | 1 592 | 0.6 ms | 2.6 ms | 0.9 ms | 1.2 ms | 3.0 ms | ✓ |
| 2 000 | 6 458 | 3.3 ms | 3.4 ms | 4.3 ms | 6.3 ms | 3.8 ms | ✓ |
| 10 000 | 32 356 | 13.9 ms | 14.8 ms | 25.5 ms | 22.8 ms | 22.0 ms | ✓ |
| 50 000 | 164 309 | 62 ms | 111 ms | 180 ms | 117 ms | 94 ms | ✓ |

BFS and DFS grow linearly (O(V + E)). Prim, Kruskal and Dijkstra grow slightly faster than linearly (the log factor of the heap or the sort). A 50 000-node mesh, far beyond a farm, is fully re-optimised in under a fifth of a second. The 16-node farm is re-analysed every second in microseconds.

---

## 6. API reference

### Final PoE — `/api/topology`

| Method | Route | Purpose |
|---|---|---|
| GET | `/api/topology` | Whole mesh: nodes (position, state, hops, MST parent, role, articulation flag), links (distance, RSSI, cost, failed, in MST), summary, events |
| GET | `/api/topology/route/{mac}?to=` | Route tracking by MAC: MST route, cheapest path (Dijkstra), backup path |
| GET | `/api/topology/bfs?start=&zone=` | BFS: connected devices, depth/parent, down devices with reason and suggested hop |
| GET | `/api/topology/dfs?start=&zone=` | Same with DFS |
| GET | `/api/topology/mst?algorithm=prim\|kruskal` | MST edges in acceptance order, total, savings, elapsed µs |
| POST | `/api/topology/nodes/{mac}/toggle` | Simulate a node failure / restore it |
| POST | `/api/topology/links/toggle` | `{ "a": mac, "b": mac }`: fail / restore a link |
| POST | `/api/topology/reset` | Clear all simulated failures |
| POST | `/api/topology/neighbors` | Device uplink: `{ macAddress, neighbors: [{ neighborMac, rssi }] }` |
| GET | `/api/topology/history` | Size, height and time span of each sensor's AVL tree |
| GET | `/api/topology/history/{mac}?from=&to=` / `?fromMs=&toMs=` / `?minutes=` | AVL range query (+ min/max/avg, nodes visited) |
| GET | `/api/topology/benchmark?nodes=2000` | AVL vs linear scan; BFS/Prim/Kruskal/Dijkstra on a synthetic mesh |
| WS | `/hubs/telemetry` | SignalR: `snapshot` (Part 2) and `topology` (Part 3) every second |

Example — Moisture Sensor A between 14:00 and 16:00 SAST:

```bash
curl "http://localhost:5261/api/topology/history/24:6F:28:A1:00:01?from=2026-10-21T14:00:00%2B02:00&to=2026-10-21T16:00:00%2B02:00"
```

### Part 2 — `/api/stream`

| Method | Route | Purpose |
|---|---|---|
| POST | `/api/stream/ingest` | Device uplink → queue (returns lane + depths, 202) |
| GET | `/api/stream/outbox/{mac}` | Device downlink: pending commands |
| GET | `/api/stream/snapshot` · `/stats` · `/alerts` · `/alerts/history` | Dashboard state and monitoring |
| GET | `/api/stream/devices?q=&category=&state=` · `/devices/{mac}` · `/devices/{mac}/timeline?minutes=` | Search, O(1) lookup, sorted timeline |
| POST | `/api/stream/commands` · `/commands/bulk` · `/commands/undo` · `/commands/redo` | Overrides with stack undo/redo |
| GET | `/api/stream/suggestions` · `/patterns` | Predictive engine |
| POST | `/api/stream/simulate/burst` · `/simulate/critical/{mac}` | Test helpers |

### Part 1

`/api/sensors` (register, list, get), `/api/sensors/{mac}/telemetry`, `/api/sensors/{mac}/upload`, `/api/deployment/validate`, `/api/power/aggregate`.

Every request above is ready to run in **`SmartX.Api/SmartX.Api.http`**.

---

## 7. Seeded fleet and mesh layout

| MAC | Device | Category | Zone | Position (m) |
|---|---|---|---|---|
| 24:6F:28:00:00:00 | **Smart-X Gateway** (root) | Gateway | Plant Room | (60, 45) |
| 24:6F:28:A1:00:01–03 | Moisture Sensors A, B, C | Environmental | Zone 1, Zone 1, Zone 2 | (12,20) (15,70) (104,12) |
| 24:6F:28:A1:00:04–05 | Greenhouse 1 & 2 Temp | Environmental | Zone 1, Zone 2 | (28,45) (92,22) |
| 24:6F:28:A1:00:06 | Humidity Node 1 | Environmental | Zone 1 | (22,30) |
| 24:6F:28:B2:00:10–12 | Smart Meters (Main, Pump House), Grow-light plug | Power Consumption | Plant Room, Pump House, Zone 1 | (66,53) (86,60) (43,57) |
| 24:6F:28:C3:00:20–25 | Pumps A/B, Irrigation Valves 1/2, Grow-light relay, Misting valve | Actuator | Pump House, Zone 1/2 | see `MeshLayout.cs` |

`24:6F:28` is an Espressif (ESP32) vendor prefix. Radio range is 26 m, which gives 26 links, a maximum depth of 3 hops, and 5 single points of failure.

---

## 8. Feedback, tests and troubleshooting

- **Feedback ledger:** [FEEDBACK.md](FEEDBACK.md) lists every change made after Part 1 (90 %) and Part 2 (100 %, "Excellent submission") feedback, and every defect found and fixed during the final PoE.
- **Tests:** `dotnet test SmartX.Tests` → 46 passing.
  - Part 2: queues, stack, dictionaries, sets and the predictive engine.
  - Final PoE:
    - AVL balance, range, update and delete;
    - heap order and union–find;
    - BFS hop counts and DFS order;
    - Dijkstra cost vs hops, articulation points;
    - Prim = Kruskal on 800 nodes;
    - on the seeded farm: reachability, single points of failure, route tracking by MAC, self-healing around a failed relay, a failed link, cut-off detection, subnet traversal, weak-signal re-routing, no route flapping under noise, history range queries, and late-registered devices joining the mesh.

| Symptom | Fix |
|---|---|
| Top bar says **API offline** | Start `SmartX.Api` and check it prints `Now listening on: http://localhost:5261`; otherwise point `ApiBaseUrl` at its port. |
| Every node turns grey after about 8 s | The simulator isn't running. Start `SmartX.Simulator`. |
| Topology map is empty | The API is older than this version, or still starting (history seeding takes under a second). Refresh. |
| Certificate errors | `dotnet dev-certs https --trust`, then restart the browser. |
| Simulator says "waiting for gateway" | Pass the URL: `dotnet run --project SmartX.Simulator http://localhost:5261`. |

## Submission

| Item | Link |
|---|---|
| Repository | https://github.com/evanildo26/SmartX-PROG7312-FinalPOE_ST10437401 |
| Video demo | [add YouTube link] |
| Report (PDF) | [docs/PROG7312_PoE_Report_ST10437401.pdf](docs/PROG7312_PoE_Report_ST10437401.pdf) |
| Feedback ledger | [FEEDBACK.md](FEEDBACK.md) |
| Author | Evanildo Julio Chaves Jose (ST10437401) |
