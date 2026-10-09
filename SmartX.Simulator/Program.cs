using System.Net.Http.Json;
using SmartX.Shared;

// ============================================================================
//  Smart-X ESP32 Fleet Simulator
//  Behaves like the real microcontrollers: every device POSTs its reading to the
//  gateway over HTTP once per tick, and every actuator polls its command outbox.
//  Simple physics closes the loop: turning a pump ON makes soil moisture rise and
//  pump-house power climb, so operator commands have visible consequences.
//
//  Usage:  dotnet run --project SmartX.Simulator [apiBaseUrl] [--interval 1000] [--no-dropouts]
//  Env:    SMARTX_API, SMARTX_INTERVAL_MS
// ============================================================================

string apiBase = args.FirstOrDefault(a => a.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                 ?? Environment.GetEnvironmentVariable("SMARTX_API")
                 ?? "http://localhost:5261";
int intervalMs = ReadIntArg("--interval") ?? (int.TryParse(Environment.GetEnvironmentVariable("SMARTX_INTERVAL_MS"), out var iv) ? iv : 1000);
bool dropouts = !args.Contains("--no-dropouts");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

using var http = new HttpClient { BaseAddress = new Uri(apiBase), Timeout = TimeSpan.FromSeconds(5) };
Console.WriteLine($"Smart-X ESP32 fleet simulator → {apiBase} (tick {intervalMs} ms, dropouts {(dropouts ? "on" : "off")})");

// ---- Wait for the gateway ----------------------------------------------------------------
while (!cts.IsCancellationRequested)
{
    try
    {
        var health = await http.GetAsync("/api/health", cts.Token);
        if (health.IsSuccessStatusCode) break;
    }
    catch (Exception ex) when (ex is not OperationCanceledException) { }
    Console.WriteLine("  waiting for gateway API...");
    try { await Task.Delay(2000, cts.Token); } catch (OperationCanceledException) { return; }
}

var fleet = new Fleet(await LoadDevicesAsync());
Console.WriteLine($"  loaded {fleet.Devices.Count} devices ({fleet.Devices.Count(d => d.IsActuator)} actuators). Ctrl+C to stop.\n");

long tick = 0;
using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(Math.Max(100, intervalMs)));
try
{
    while (await timer.WaitForNextTickAsync(cts.Token))
    {
        tick++;

        // 1. Downlink: actuators collect queued commands from their outbox.
        foreach (var actuator in fleet.Devices.Where(d => d.IsActuator))
        {
            try
            {
                var commands = await http.GetFromJsonAsync<List<OutboxCommand>>(
                    $"/api/stream/outbox/{Uri.EscapeDataString(actuator.MacAddress)}", cts.Token);
                foreach (var cmd in commands ?? new())
                {
                    fleet.SetActuator(actuator.MacAddress, cmd.TargetState);
                    Console.WriteLine($"  ⬇ {actuator.DisplayName} ← {(cmd.TargetState ? "ON" : "OFF")}");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { }
        }

        // 2. Physics step + random node dropouts.
        fleet.Step(tick, dropouts);

        // 3. Uplink: every online device posts its reading concurrently, like independent ESP32s.
        var sends = fleet.Devices
            .Where(d => !fleet.IsSilent(d.MacAddress, tick))
            .Select(d => SendAsync(d.MacAddress, fleet.Read(d)))
            .ToList();
        var results = await Task.WhenAll(sends);

        int priority = results.Count(r => r?.Lane == QueueLane.Priority);
        var last = results.LastOrDefault(r => r is not null);
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] tick {tick,4} · sent {results.Count(r => r is not null),2}/{fleet.Devices.Count}" +
                          $" · priority {priority} · queues std={last?.StandardQueueDepth ?? 0} prio={last?.PriorityQueueDepth ?? 0}" +
                          (fleet.SilentCount(tick) > 0 ? $" · {fleet.SilentCount(tick)} node(s) silent" : ""));

        // 4. Pick up devices registered later (e.g. from the Ingestion page) every 30 ticks.
        if (tick % 30 == 0)
        {
            try { fleet.Merge(await LoadDevicesAsync()); } catch (Exception ex) when (ex is not OperationCanceledException) { }
        }
    }
}
catch (OperationCanceledException) { }
Console.WriteLine("Simulator stopped.");

async Task<IngestAck?> SendAsync(string mac, double value)
{
    try
    {
        var response = await http.PostAsJsonAsync("/api/stream/ingest", new StreamIngestRequest(mac, Math.Round(value, 2)), cts.Token);
        return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<IngestAck>(cts.Token) : null;
    }
    catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
}

async Task<List<LiveDevice>> LoadDevicesAsync() =>
    await http.GetFromJsonAsync<List<LiveDevice>>("/api/stream/devices", cts.Token) ?? new();

int? ReadIntArg(string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out var v) ? v : null;
}

/// <summary>Simulated physical state of the farm and grid.</summary>
sealed class Fleet
{
    private readonly Random _rnd = new();
    private readonly Dictionary<string, double> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _actuators = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _silentUntil = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _spikeThisTick = new(StringComparer.OrdinalIgnoreCase);
    private int _heatEventTicks;

    public List<LiveDevice> Devices { get; } = new();

    public Fleet(IEnumerable<LiveDevice> devices) => Merge(devices);

    public void Merge(IEnumerable<LiveDevice> devices)
    {
        foreach (var d in devices)
        {
            if (Devices.Any(x => x.MacAddress == d.MacAddress)) continue;
            if (d.Metric == "value") continue;     // manual Part 1 sensors report on demand, not continuously
            Devices.Add(d);
            if (d.IsActuator) _actuators[d.MacAddress] = d.ActuatorOn;
            else _values[d.MacAddress] = d.LastValue ?? Default(d.Metric);
        }
    }

    public void SetActuator(string mac, bool on) => _actuators[mac] = on;

    public bool IsSilent(string mac, long tick) => _silentUntil.TryGetValue(mac, out var until) && tick < until;
    public int SilentCount(long tick) => _silentUntil.Count(kv => tick < kv.Value);

    private bool AnyOn(string nameContains, string? zone) => Devices.Any(d =>
        d.IsActuator && _actuators.GetValueOrDefault(d.MacAddress) &&
        d.DisplayName.Contains(nameContains, StringComparison.OrdinalIgnoreCase) &&
        (zone is null || d.ServesZone == zone));

    private int CountOn(string nameContains) => Devices.Count(d =>
        d.IsActuator && _actuators.GetValueOrDefault(d.MacAddress) &&
        d.DisplayName.Contains(nameContains, StringComparison.OrdinalIgnoreCase));

    public void Step(long tick, bool dropouts)
    {
        _spikeThisTick.Clear();
        bool lights = AnyOn("Grow Light", null);
        bool misting = AnyOn("Misting", null);
        if (_heatEventTicks > 0) _heatEventTicks--;
        else if (_rnd.NextDouble() < 0.004) { _heatEventTicks = 90; Console.WriteLine("  ☀ heat event started in Zone 1"); }

        foreach (var d in Devices.Where(x => !x.IsActuator))
        {
            double v = _values[d.MacAddress];
            string zone = d.Location.Contains("Zone 2") ? "Zone 2" : "Zone 1";
            switch (d.Metric)
            {
                case "moisture":
                    bool pump = AnyOn("Pump", zone);
                    bool valve = AnyOn("Valve Line", zone);
                    v += pump ? 0.8 + (valve ? 0.4 : 0) : -(0.15 + _rnd.NextDouble() * 0.1);   // %/tick
                    v += Noise(0.15);
                    v = Math.Clamp(v, 0, 100);
                    break;
                case "temperature":
                    double target = 24 + (lights && zone == "Zone 1" ? 3 : 0) - (misting && zone == "Zone 1" ? 5 : 0)
                                    + (_heatEventTicks > 0 && zone == "Zone 1" ? 14 : 0);
                    v += (target - v) * 0.06 + Noise(0.12);
                    break;
                case "humidity":
                    v += ((misting ? 78 : 60) - v) * 0.05 + Noise(0.6);
                    break;
                case "power":
                    double baseline = d.DisplayName.Contains("Main")
                        ? 1500 + (lights ? 420 : 0) + CountOn("Pump") * 900
                        : d.DisplayName.Contains("Pump House")
                            ? 120 + CountOn("Pump") * 900
                            : lights ? 420 : 4;
                    v = baseline + Noise(baseline * 0.03);
                    // Occasional surge (motor inrush / fault) — a one-tick spike.
                    if (_rnd.NextDouble() < 0.012) _spikeThisTick[d.MacAddress] = v * (2.0 + _rnd.NextDouble());
                    break;
            }
            _values[d.MacAddress] = v;
        }

        // Random dropout: one node goes quiet for 12–25 ticks (brown-out / mesh link loss).
        if (dropouts && _rnd.NextDouble() < 0.008)
        {
            var candidates = Devices.Where(d => !IsSilent(d.MacAddress, tick)).ToList();
            if (candidates.Count > 0)
            {
                var victim = candidates[_rnd.Next(candidates.Count)];
                _silentUntil[victim.MacAddress] = tick + _rnd.Next(12, 26);
                Console.WriteLine($"  ✖ {victim.DisplayName} dropped off the mesh");
            }
        }
    }

    public double Read(LiveDevice d) =>
        d.IsActuator ? (_actuators.GetValueOrDefault(d.MacAddress) ? 1 : 0)
        : _spikeThisTick.TryGetValue(d.MacAddress, out var spike) ? spike
        : _values[d.MacAddress];

    private double Noise(double amplitude) => (_rnd.NextDouble() - 0.5) * 2 * amplitude;

    private static double Default(string metric) => metric switch
    {
        "moisture" => 50, "temperature" => 24, "humidity" => 60, "power" => 500, _ => 0
    };
}
