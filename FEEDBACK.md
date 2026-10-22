# Feedback & Update Ledger

This file lists every update made in response to lecturer feedback, plus the defects found and fixed through testing, as required for the final PoE ("a file listing the updates that you have made based on feedback from your lecturer").

Each row names the problem, the change, and the file where the change can be checked.

---

## 1. After Part 1 (mark: 90 %)

| # | Area | Feedback / problem | Change made | Files |
|---|---|---|---|---|
| 1.1 | Security | Uploaded file names were used as given. | `Path.GetFileName` strips directory parts, so `../../x` cannot escape the uploads folder. | `SmartX.Api/Program.cs` |
| 1.2 | Validation | A malformed MAC could be submitted. | Client-side MAC format validation with clear error messages. | `SmartX.Client/Pages/Ingestion.razor` |
| 1.3 | Robustness | The Ingestion page threw when the API was down. | API calls wrapped in error handling; the page shows a message instead of crashing. | `SmartX.Client/Pages/Ingestion.razor` |
| 1.4 | Configuration | API address was hard-coded in C#. | Moved to `wwwroot/appsettings.json` (`ApiBaseUrl`); no recompile needed to change it. | `SmartX.Client/Program.cs` |
| 1.5 | Integration | Part 1 sensors lived in a separate registry. | Sensors registered in Part 1 also join the Part 2 live registry, its queues and (now) the mesh graph. | `SmartX.Api/Program.cs` |
| 1.6 | Referencing | Reference list formatting was inconsistent. | All report references use one style (IEEE), with clean official URLs. | Reports |

## 2. After Part 2

### 2a. Lecturer feedback

| # | Area | Lecturer's comment | Change made | Files |
|---|---|---|---|---|
| 2.1 | _fill in_ | _copy the comment from your Part 2 marking rubric_ | _describe what you changed_ | _file(s)_ |

### 2b. Defects found while testing Part 2 and fixed

| # | Problem | Root cause | Fix | Files |
|---|---|---|---|---|
| 2.2 | Client did not compile (`CS1525`) on some machines. | Lambdas with nested quotes inside Razor attributes. | Replaced the inline lambdas with `TurnOn(mac)` / `TurnOff(mac)` helper methods. | `SmartX.Client/Pages/CommandStream.razor` |
| 2.3 | Dashboard showed "API offline" when launched with the `https` profile. | Client pointed at an HTTPS port the API was not listening on. | `ApiBaseUrl` uses `http://localhost:5261`, which both API profiles listen on. | `SmartX.Client/wwwroot/appsettings.json` |
| 2.4 | The PRIORITY packet scrolled out of the ingestion table during a burst. | The table only showed the newest packets, and 600 routine packets pushed it off. | A small `Queue` of recent priority packets is pinned to the top of the table. | `SmartX.Api/Services/CommandStreamEngine.cs` |
| 2.5 | No-op bulk commands created empty undo entries. | Bulk command did not check whether anything changed. | Rejected with a message; nothing is pushed onto the stack (unit-tested). | `CommandStreamEngine.cs`, `SmartX.Tests` |

## 3. Final PoE: engineering log (continuous improvement)

| # | Problem found | How it was found | Fix | Files |
|---|---|---|---|---|
| 3.1 | Three nodes received long-range "fallback" links (32–39 m, outside radio range), making the graph wrong. | Unit test `Seeded_mesh_is_fully_reachable…` expected 26 links but found 29. | The "link to the nearest node" fallback now runs only after every node has been placed, and only for nodes that still hear nobody. | `MeshTopologyEngine.cs` |
| 3.2 | Route flapping: the MST flipped between two near-equal links several times a minute because of ±1 dB radio noise, flooding the event log with "re-routed" messages. | Two-minute live soak test with the simulator. | Exponential moving average plus a 2 dB hysteresis band before a link's routing RSSI changes (the same idea real routing protocols use). A real interference burst (−14 dB) still re-routes within seconds. | `MeshTopologyEngine.ReportNeighbors`, test `Small_radio_noise_does_not_make_routes_flap` |
| 3.3 | Two SignalR connections would have been needed for two live feeds. | Design review. | One session-wide connection now carries both `snapshot` (Part 2) and `topology` (Part 3); every pillar shares the same link-state indicator. | `StreamStateService.cs`, `TopologyBroadcaster.cs` |
| 3.4 | Razor parsed `@step.Depth@(…)` inside an attribute as an e-mail address and failed to compile. | Build of the Topology page. | Tooltip text moved into a `StepTitle()` helper method. | `SmartX.Client/Pages/Topology.razor` |
| 3.5 | Building the 2 000-node benchmark mesh was O(n²) (one BFS per node to check connectivity). | Code review of the benchmark generator. | Islands are joined with a union–find `DisjointSet`, which takes near-linear time; the mesh is built with grid buckets. | `MeshTopologyEngine.BuildRandomMesh` |
| 3.6 | The Topology page was a static preview. | Final PoE requirement. | Fully active pillar: live mesh graph, BFS/DFS, Prim/Kruskal MST, route tracking by MAC, AVL history log, benchmark. | `SmartX.Client/Pages/Topology.razor`, `SmartX.Api/Services/Topology/*` |

## Test evidence

| Stage | Tests | Result |
|---|---|---|
| Part 2 submission | 24 | all passing |
| Final PoE | 46 (24 Part 2 + 22 topology) | all passing |
