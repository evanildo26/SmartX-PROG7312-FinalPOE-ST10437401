# Smart-X IoT Mesh Ecosystem — Part 1

Smart-X is a simulated IoT ingestion gateway for a distributed sensor network (hydroponic farms, smart grid nodes, utility trackers). This part implements the **Sensor Data Ingestion and Telemetry** pillar: a .NET 10 Web API backend and a Blazor WebAssembly client.

## Architecture

- **SmartX.Api** — ASP.NET Core Minimal API (.NET 10). In-memory registry of sensors, telemetry ingestion, file uploads, recursive deployment validation.
- **SmartX.Client** — Blazor WebAssembly (.NET 10). Dashboard UI communicating with the API over HTTP.
- **SmartX.Shared** — Class library with shared models (`TelemetryPacket<T>`, `PowerReading`, `DeploymentNode`, `SensorProfile`).

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Visual Studio 2025 (or `dotnet` CLI)

## Setup & Run

### Option A — Visual Studio 2025

1. Clone the repo and open `SmartX.sln`.
2. Right-click the **Solution** → **Configure Startup Projects** → **Multiple startup projects** → set both `SmartX.Api` and `SmartX.Client` to **Start**.
3. Press **F5**.
4. The Blazor client opens automatically at `https://localhost:7192` (check `SmartX.Client/Properties/launchSettings.json` if the port differs).

### Option B — CLI

```bash
# Terminal 1 — start the API
cd SmartX.Api
dotnet run

# Terminal 2 — start the client
cd SmartX.Client
dotnet run
```

Then open the URL printed in Terminal 2's output.

### First-time HTTPS setup

If you get a certificate error on first run:

```bash
dotnet dev-certs https --trust
```

## Configuration

The client's API base address is set in `SmartX.Client/Program.cs`. If your API launches on a different port than `7290`, update it there:

```csharp
BaseAddress = new Uri("https://localhost:7290")
```

## Features Implemented (Part 1)

| Feature | Location |
|---|---|
| Generic telemetry wrapper (`TelemetryPacket<T>`) | `SmartX.Shared/TelemetryPacket.cs` |
| Operator overloading on `PowerReading` (`+`, `-`, `>`, `<`, `==`) | `SmartX.Shared/PowerReading.cs` |
| Jagged array → `List<T>` telemetry batching | `SmartX.Shared/SensorProfile.cs` |
| Recursive deployment path validation | `SmartX.Shared/DeploymentNode.cs` |
| Sensor registration + telemetry ingestion API | `SmartX.Api/Program.cs` |
| Multipart file/log upload | `SmartX.Api/Program.cs` (`/api/sensors/{mac}/upload`) |
| Live "Sensor Health Pulse" dashboard engagement feature | `SmartX.Client/Pages/Ingestion.razor` |

## Testing the App

1. Go to **Sensor Ingestion & Telemetry**.
2. Register a sensor (e.g. MAC `AA:BB:CC:01`, Zone 1, Environmental).
3. Click **Simulate reading** a few times — watch the health pulse indicator shift from green to amber/red as readings deviate.
4. Try the deployment path validator with `Facility A/Zone 1/Sub-Zone B` (valid) vs. `Facility A/Zone 9` (invalid).
5. Upload a small file to a registered sensor to test the log attachment feature.

## Known Limitations

- Data is stored in-memory (`ConcurrentDictionary`) and resets when the API restarts — this is expected for Part 1; persistence is out of scope until a later part.
- No authentication layer yet.

## Project Status

Part 1 of 3 — Sensor Data Ingestion and Telemetry Gateway. Parts 2 (Real-Time Command Stream) and 3 (Network Topology & Mesh Routing) build on this foundation.