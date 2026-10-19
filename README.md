# Smart-X IoT Mesh Ecosystem — Part 2: Real-Time Processing & Device Management

Smart-X is a simulated IoT gateway for a fleet of ESP32 microcontrollers in a hydroponic farm and smart-grid installation. Part 1 delivered **Sensor Data Ingestion & Telemetry**. Part 2 turns the gateway into a **real-time processing engine**:

- Telemetry packets flow through a **FIFO queue**. Critical readings go through a **priority queue** instead.
- Operators issue **manual overrides** (pumps, valves, relays) with a **stack-based undo/redo**.
- Devices live in a **dictionary keyed by MAC address**. Each sensor's history sits in a **sorted dictionary timeline**.
- **Hash sets** keep alert and disconnect tracking free of duplicates.
- An **Automated Action Engine** learns from operator behaviour and trend data. It suggests the next action *before* the operator searches for it.

| Pillar | Status |
|---|---|
| 📡 Sensor Data Ingestion & Telemetry | ✅ Part 1 (now feeds the Part 2 stream) |
| ⏱ Real-Time Command Stream & History | ✅ **Part 2** |
| 🕸 Network Topology & Mesh Routing | 🔜 Final PoE (live inventory preview page in place) |

---

## Solution layout

| Project | Role |
|---|---|
| `SmartX.Api` | ASP.NET Core Minimal API on **.NET 10**. Runs the gateway engine, background processor, SignalR hub and REST endpoints. |
| `SmartX.Client` | **Blazor WebAssembly** dashboard. Receives live data over SignalR; sends commands over REST. |
| `SmartX.Shared` | Contracts shared by every project (`TelemetryPacket<T>`, `LiveDevice`, `StreamSnapshot`, …). |
| `SmartX.Simulator` | **ESP32 fleet emulator**. Each device POSTs readings over HTTP and polls its command outbox, exactly as hardware would. |
| `SmartX.Tests` | 24 xUnit tests proving each data structure behaves correctly. |

```
ESP32 fleet (Simulator) ──HTTP POST /api/stream/ingest──▶ ┌──────────────── SmartX.Api ────────────────┐
        ▲                                                │ classify O(1) ─▶ Queue<T>  (standard)       │
        └──────HTTP GET /api/stream/outbox/{mac}─────────│              └▶ PriorityQueue (critical)    │
                                                         │ StreamProcessorService: drain priority first │
                                                         │ Dictionary<MAC,LiveDevice> · SortedDictionary│
                                                         │ HashSet alerts/disconnects · Stack undo/redo │
                                                         │ ActionPatternEngine + TrendForecaster        │
Blazor dashboard ◀──SignalR "snapshot" every 1 s─────────│ SnapshotBroadcaster ─▶ /hubs/telemetry       │
                 ──REST commands / search / undo────────▶└─────────────────────────────────────────────┘
```

---

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (`dotnet --version` → `10.x`)
- Optional: Visual Studio 2026 / Rider / VS Code with C# Dev Kit

## Quick start (CLI)

```bash
# 1. Restore and build everything
dotnet restore SmartX.slnx
dotnet build SmartX.slnx

# 2. Trust the HTTPS development certificate (first time only)
dotnet dev-certs https --trust

# 3. Terminal 1 — gateway API (https://localhost:7290 and http://localhost:5261)
dotnet run --project SmartX.Api --launch-profile https

# 4. Terminal 2 — ESP32 fleet simulator (talks to http://localhost:5261 by default)
dotnet run --project SmartX.Simulator

# 5. Terminal 3 — Blazor dashboard (opens https://localhost:7192)
dotnet run --project SmartX.Client --launch-profile https
```

Open **https://localhost:7192** and choose **Real-Time Command Stream**.

> The client reads the API address from `SmartX.Client/wwwroot/appsettings.json` (`ApiBaseUrl`, default `http://localhost:5261`, which both the `http` and `https` API profiles listen on). If your API runs on a different port, change it there; no recompile is needed.

## Quick start (Visual Studio)

1. Open `SmartX.slnx`.
2. Choose the **"Smart-X full stack"** launch profile in the toolbar. It starts Api + Client + Simulator. If you don't see it, use **Configure Startup Projects → Multiple startup projects** and set those three to *Start*.
3. Press **F5**.

## Run the tests

```bash
dotnet test SmartX.Tests
```

The 24 tests cover queue bypass, FIFO order, LIFO undo, bulk undo, redo clearing, outbox order, case-insensitive MAC lookup, the category index, timeline ordering and capacity, duplicate alert and disconnect suppression, learning of operator patterns, the learned-patterns report, injected critical readings, toggle and empty-undo handling, and trend forecasting.

---

## How to test each Part 2 feature

All the API calls below also live in `SmartX.Api/SmartX.Api.http`, so you can run them with one click from Visual Studio, Rider or VS Code (REST Client).

### 1. Queue<T> and PriorityQueue: simulate a high-priority alert

**From the dashboard:** click **⚡ Burst test: 600 packets + 1 critical**. This puts 600 routine packets into the standard FIFO queue, *then* one critical packet.

- The **Standard queue** tile jumps to ~600 and drains over ~2–3 s. Processing is capped at 25 packets per 100 ms tick to model a constrained gateway (see `Gateway` in `appsettings.json`).
- In **Ingestion stream**, the critical packet shows lane **PRIORITY** with a queue wait of a few ms. The routine packets wait seconds.
- The **Priority lane** tile shows its average wait next to the standard lane's.

**From the API:**

```bash
# A normal reading → standard lane
curl -X POST http://localhost:5261/api/stream/ingest -H "Content-Type: application/json" \
     -d '{"macAddress":"24:6F:28:A1:00:02","value":41.5}'
# → {"lane":0 (Standard), "severity":0, ...}

# A critical moisture drop (≤ 15 %) → priority lane, bypasses the backlog
curl -X POST http://localhost:5261/api/stream/ingest -H "Content-Type: application/json" \
     -d '{"macAddress":"24:6F:28:A1:00:02","value":9}'
# → {"lane":1 (Priority), "severity":2 (Critical), ...}

# Backlog + critical in one go
curl -X POST http://localhost:5261/api/stream/simulate/burst -H "Content-Type: application/json" \
     -d '{"standardCount":600,"includeCritical":true,"criticalMac":"24:6F:28:A1:00:02"}'
```

You can also select any sensor and click **🚨 Inject critical reading for this device**.

### 2. Stack<T>: undo manual overrides

1. On a pump or valve card, click **Turn ON**. It goes onto the stack, and the simulator console prints `⬇ Pump A ← ON`.
2. Click **Shut down all valves**. That is **one** stack entry covering every valve.
3. Click **↶ Undo**. Every valve returns to its previous state in a single step. The history list shows the entry as *Undone*.
4. **↷ Redo** re-applies it. Issuing a new command clears the redo stack.

### 3. Dictionary<TKey,TValue>: O(1) device lookup

```bash
curl http://localhost:5261/api/stream/devices/24:6f:28:a1:00:02     # case-insensitive MAC key
```

Every incoming packet uses the same `TryGetValue(mac)` lookup. In the dashboard, use the search box, category and state filters. An exact MAC returns a direct dictionary hit. A category uses the `Dictionary<category, HashSet<mac>>` index rather than scanning every device.

### 4. SortedDictionary: timelines

Click any sensor card. The chart on the right is drawn from that device's `SortedDictionary<DateTime,double>`, which is always time-ordered. Switch between 2, 5 and 10 minutes.

```bash
curl "http://localhost:5261/api/stream/devices/24:6F:28:A1:00:02/timeline?minutes=5"
```

### 5. HashSet<T>: no duplicate alerts or disconnects

- Leave the simulator running. A low-moisture sensor sends a below-threshold reading every second, but **Active alerts** shows it once. The **Duplicates blocked** tile counts every repeat the set rejected.
- Stop the simulator (Ctrl+C). After 8 s every node lands in **Disconnected nodes** exactly once. The watchdog re-checks every second, and the "repeat disconnect alerts" counter climbs instead of new alerts appearing. Restart the simulator and the nodes leave the set as soon as they report again.

### 6. Predictive Action & Recommendation Engine

At start-up, the engine is seeded with about three weeks of historical operator behaviour (e.g. *"when Moisture Sensor B reports MOISTURE LOW, operators turned Pump A on 5 of 6 times"*). Click **Show learned patterns** to see the table.

1. Keep the simulator running. Moisture Sensor B starts near 38 % and dries out about 0.2 %/s.
2. **Before** it crosses the limit, a card appears: **"Pre-emptively turn ON Pump A"**. A linear-regression trend forecast predicts the breach, and the learned response is attached.
3. When it does cross 25 %, the card changes to **"Turn ON Pump A"**, with the confidence and the reason (*"In 5 of 7 past occurrences…"*).
4. Click **Run now**. The command goes to Pump A's outbox, the simulator turns the pump on, moisture rises, the alert clears and the card disappears.
5. Searching for a device (e.g. type `Pump A`) is recorded too. Searches made shortly after an alert are learned as responses to that alert, and "what operators do next" patterns come from a Markov chain.

```bash
curl http://localhost:5261/api/stream/suggestions
curl http://localhost:5261/api/stream/patterns
```

### 7. Navigation and state

Switch between **Control Centre**, **Ingestion**, **Command Stream** and **Topology**. The SignalR connection, search filters, selected device, timeline range and Ingestion form inputs are all kept: they live in session-scoped services, not in the pages. The top bar shows the link state (Live / Polling / Reconnecting / Offline). Critical alerts pop up as toasts on any page; clicking one opens that device. If SignalR is unavailable, the client falls back to REST polling and keeps retrying the hub.

---

## Data structures: where and why

| Requirement | Implementation | File |
|---|---|---|
| `Queue<T>`: FIFO telemetry | `Queue<TelemetryPacket<double>> _standardQueue` | `SmartX.Api/Services/CommandStreamEngine.cs` |
| Priority queue: critical bypass | `PriorityQueue<TelemetryPacket<double>, (int Rank, long Seq)>`. Rank 0 is a threshold breach, rank 1 a statistical spike; the sequence number keeps FIFO order within a rank. | same |
| `Stack<T>`: undo | `Stack<CommandRecord> _undoStack` + `_redoStack`. A bulk command is one record, so one undo reverts all of it. | same |
| `Dictionary<TKey,TValue>`: O(1) devices | `Dictionary<string, LiveDevice>` keyed by MAC (`OrdinalIgnoreCase`), plus a category index and per-device outbox queues | same |
| Sorted dictionary: timelines | `Dictionary<string, SortedDictionary<DateTime,double>>`, capped at 720 points/sensor (oldest key evicted) | same |
| `HashSet<T>`: unique alerts | `HashSet<AlertEntry>` with `AlertIdentityComparer` (MAC + code) and `HashSet<string> _disconnectedNodes` | same |
| Predictive engine | Trigger→action counts and a Markov chain (`Dictionary`s), a sliding window (`Queue`), per-occurrence credit (`HashSet`), and top-N ranking (`PriorityQueue` max-heap) | `ActionPatternEngine.cs`, `TrendForecaster.cs` |
| Generic packet (Part 1) | `TelemetryPacket<T>`, now the element type of both queues | `SmartX.Shared/TelemetryPacket.cs` |

**Thread safety:** the BCL generic collections are not thread-safe. The engine is used at once by HTTP requests, the processor loop and the broadcaster, so every public method takes one lock. Each critical section is O(1) or O(log n), which keeps contention negligible.

## API reference (Part 2)

| Method | Route | Purpose |
|---|---|---|
| POST | `/api/stream/ingest` | Device uplink → queue (returns lane + depths, 202) |
| GET | `/api/stream/outbox/{mac}` | Device downlink: pending commands (drained) |
| GET | `/api/stream/snapshot` | Full dashboard state |
| GET | `/api/stream/devices?q=&category=&state=&log=` | Search / filter |
| GET | `/api/stream/devices/{mac}` | O(1) device lookup |
| GET | `/api/stream/devices/{mac}/timeline?minutes=` | Sorted timeline |
| POST | `/api/stream/commands` | `{macAddress, action: ON/OFF/TOGGLE}` |
| POST | `/api/stream/commands/bulk` | `{action, nameContains, label}` |
| POST | `/api/stream/commands/undo` / `redo` | Stack pop |
| GET | `/api/stream/suggestions` · `/patterns` | Predictive engine |
| GET | `/api/stream/alerts` · `/alerts/history` · `/stats` | Monitoring |
| POST | `/api/stream/simulate/burst` · `/simulate/critical/{mac}` | Test helpers |
| WS | `/hubs/telemetry` | SignalR, `snapshot` every second |

Part 1 endpoints (`/api/sensors`, `/api/deployment/validate`, `/api/power/aggregate`, uploads) are unchanged. Sensors registered there now also join the live device registry, and their telemetry flows through the same queues.

## Seeded fleet

| MAC | Device | Category |
|---|---|---|
| 24:6F:28:A1:00:01–03 | Moisture Sensors A, B, C | Environmental |
| 24:6F:28:A1:00:04–05 | Greenhouse 1 & 2 Temperature | Environmental |
| 24:6F:28:A1:00:06 | Humidity Node 1 | Environmental |
| 24:6F:28:B2:00:10–12 | Smart meters (Main, Pump House), Grow-light plug | Power Consumption |
| 24:6F:28:C3:00:20–25 | Pumps A/B, Irrigation Valves 1/2, Misting Valve, Grow-light relay | Actuator |

`24:6F:28` is an Espressif (ESP32) vendor prefix.

## Changes since Part 1

- The upload endpoint strips directory parts from file names (`Path.GetFileName`) to block path traversal.
- The Ingestion page validates MAC format on the client, shows success and error messages, and survives an API outage without crashing.
- The client API address moved from hard-coded C# to `wwwroot/appsettings.json`.
- Removed the template `weather.json` sample data.

## Troubleshooting

| Symptom | Fix |
|---|---|
| Top bar says **API offline** | Make sure `SmartX.Api` is running and its console shows `Now listening on: http://localhost:5261`; otherwise point `ApiBaseUrl` at the port it prints. |
| All devices go grey after ~8 s | The simulator isn't running. Start `SmartX.Simulator`. |
| Certificate errors | `dotnet dev-certs https --trust`, then restart the browser. |
| Simulator says "waiting for gateway" | It defaults to `http://localhost:5261`. Pass another URL: `dotnet run --project SmartX.Simulator http://localhost:5000`. |

## Submission

| Item | Link |
|---|---|
| Repository | https://github.com/evanildo26/SmartX-PROG7312 |
| Video demo | https://youtu.be/vzVGulWa2Mg |
| Feedback ledger | [FEEDBACK.md](FEEDBACK.md) |
| Author | Evanildo Julio Chaves Jose (ST10437401) |
