# Smart-X Part 2 - Data Structures

This document explains each data structure used by the real-time command stream, why it was chosen, and its cost.

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