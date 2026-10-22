# Smart-X — Data Structures (Parts 2 and 3)

This document summarises each data structure, why it was chosen, and its cost. The README has the in-depth explanation and code for the Part 3 structures.

## Part 2 — Real-Time Command Stream

| Structure | Field | Operation | Cost | Why |
|---|---|---|---|---|
| `Queue<T>` | `_standardQueue` | Enqueue / Dequeue | O(1) | Routine telemetry is processed in arrival order (FIFO). |
| `PriorityQueue<T, (int, long)>` | `_priorityQueue` | Enqueue / Dequeue | O(log n) | Critical packets are served before any routine packet. The sequence number keeps FIFO order for equal priority. |
| `Stack<T>` | `_undoStack`, `_redoStack` | Push / Pop | O(1) | The most recent manual override is undone first (LIFO). A bulk command is one entry. |
| `Dictionary<string, LiveDevice>` | `_devices` | Lookup by MAC | O(1) average | Every incoming packet finds its device instantly. |
| `Dictionary<string, HashSet<string>>` | `_categoryIndex` | Lookup by category | O(1) + k | Filters by category without scanning all devices. |
| `SortedDictionary<DateTime, double>` | `_timelines[mac]` | Insert / remove oldest | O(log n) | Each sensor's history stays ordered by time for the chart. |
| `HashSet<AlertEntry>` | `_activeAlerts` | Add / Contains | O(1) average | One alert per device + code; repeats are rejected and counted. |
| `HashSet<string>` | `_disconnectedNodes` | Add / Remove | O(1) average | A silent node is reported once until it reconnects. |
| `Dictionary<string, Queue<OutboxCommand>>` | `_outbox` | Enqueue / drain | O(1) per command | Each device receives its commands in the order they were issued. |

## Predictive engine

| Structure | Purpose |
|---|---|
| `Dictionary<string, Dictionary<OperatorAction, int>>` | Counts how often each operator action follows each alert (trigger -> action). |
| `Dictionary<OperatorAction, Dictionary<OperatorAction, int>>` | Markov chain of which action usually follows another. |
| `Queue<RecentTrigger>` | Sliding three-minute window of recent alerts; old ones are dequeued from the front. |
| `HashSet<OperatorAction>` per trigger | Credits each action only once per alert occurrence. |
| `PriorityQueue<SuggestedAction, double>` | Max-heap (negated score) that returns the top six suggestions. |

## Thread safety

The generic collections are not thread-safe. `CommandStreamEngine` is used at the same time by HTTP requests, the background processor and the SignalR broadcaster, so every public method takes a single lock. Each locked section does O(1) or O(log n) work, so waiting is negligible.

## Part 3 — Network Topology & Mesh Routing

| Structure | Where | Operation | Cost | Why |
|---|---|---|---|---|
| `AvlTree<DateTime, double>` (hand-written, self-balancing BST) | `TelemetryHistoryIndex` — one tree per sensor | Insert / delete / lookup | O(log n) | Timestamps arrive in sorted order, which turns a plain BST into a linked list. AVL rotations keep the height ≤ 1.44·log₂ n. |
| | | `Range(from, to)` | O(log n + k) | "All readings between 14:00 and 16:00" visits only the matching nodes plus two root-to-leaf paths. |
| `WeightedGraph<TNode>` (adjacency list `Dictionary<TNode, Dictionary<TNode, double>>`) | `MeshTopologyEngine._physical` | Neighbours of a node / link lookup | O(degree) / O(1) | A wireless mesh is sparse; O(V + E) memory instead of O(V²). |
| `Queue<T>` | `GraphAlgorithms.BreadthFirst` | BFS | O(V + E) | Visits the mesh ring by ring, so the first visit is the minimum hop count. |
| `Stack<T>` | `GraphAlgorithms.DepthFirst` | DFS (iterative) | O(V + E) | Follows one branch to the end; iterative, so a long relay chain cannot overflow the call stack. |
| Recursive DFS + low-link `Dictionary`s | `GraphAlgorithms.ArticulationPoints` (Tarjan) | Single points of failure | O(V + E) | Finds the relays whose loss splits the mesh. |
| `MinHeap<TElement, TPriority>` (hand-written binary heap) | Prim's MST, Dijkstra | Push / pop | O(log n) | Both algorithms repeatedly need "the cheapest edge / node seen so far". |
| `DisjointSet<T>` (union–find with path compression and union by rank) | Kruskal's MST, benchmark mesh builder | Find / union | O(α(n)) ≈ O(1) | Answers "would this link create a loop?" for every sorted edge. |
| `HashSet<string>` | failed links, isolated nodes, MST membership | Add / Contains | O(1) | Simulated failures and the "is this link in the MST?" check while drawing. |
| `LinkedList<string>` | self-healing event log | AddFirst / RemoveLast | O(1) | Newest-first log with a fixed capacity. |

### MST algorithms compared

| | Prim's | Kruskal's |
|---|---|---|
| Strategy | Grow one tree outward from the gateway | Sort all links, accept the cheapest that joins two islands |
| Helper structure | `MinHeap` of frontier links | `DisjointSet` |
| Complexity | O(E log E) | O(E log E) (dominated by the sort) |
| Used for | Routing tree rooted at the gateway (parent of each node) | Cross-check: the total must equal Prim's |
