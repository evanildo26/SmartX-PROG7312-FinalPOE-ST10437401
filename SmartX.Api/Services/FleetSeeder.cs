using SmartX.Shared;

namespace SmartX.Api.Services;

/// <summary>
/// Seeds the gateway with a realistic hydroponic farm + smart-grid fleet, ten minutes of
/// historical telemetry per sensor, and a few weeks of historical operator behaviour so the
/// predictive engine has something to learn from on first launch.
/// MAC prefix 24:6F:28 is an Espressif (ESP32) OUI.
/// </summary>
public static class FleetSeeder
{
    public const string MoistureA = "24:6F:28:A1:00:01";
    public const string MoistureB = "24:6F:28:A1:00:02";
    public const string MoistureC = "24:6F:28:A1:00:03";
    public const string TempGh1 = "24:6F:28:A1:00:04";
    public const string TempGh2 = "24:6F:28:A1:00:05";
    public const string Humidity1 = "24:6F:28:A1:00:06";
    public const string MeterMain = "24:6F:28:B2:00:10";
    public const string MeterPumpHouse = "24:6F:28:B2:00:11";
    public const string PlugGrowLights = "24:6F:28:B2:00:12";
    public const string PumpA = "24:6F:28:C3:00:20";
    public const string PumpB = "24:6F:28:C3:00:21";
    public const string ValveLine1 = "24:6F:28:C3:00:22";
    public const string ValveLine2 = "24:6F:28:C3:00:23";
    public const string RelayGrowLights = "24:6F:28:C3:00:24";
    public const string MistingValve = "24:6F:28:C3:00:25";

    public static IReadOnlyList<LiveDevice> Fleet() => new List<LiveDevice>
    {
        Sensor(MoistureA, "Moisture Sensor A", "Facility A/Zone 1/Sub-Zone B", "moisture", "%", new(15, 25, 85, 95)),
        Sensor(MoistureB, "Moisture Sensor B", "Facility A/Zone 1/Sub-Zone C", "moisture", "%", new(15, 25, 85, 95)),
        Sensor(MoistureC, "Moisture Sensor C", "Facility A/Zone 2/Sub-Zone D", "moisture", "%", new(15, 25, 85, 95)),
        Sensor(TempGh1, "Greenhouse 1 Temp", "Facility A/Zone 1", "temperature", "°C", new(5, 12, 32, 40)),
        Sensor(TempGh2, "Greenhouse 2 Temp", "Facility A/Zone 2", "temperature", "°C", new(5, 12, 32, 40)),
        Sensor(Humidity1, "Humidity Node 1", "Facility A/Zone 1", "humidity", "%", new(20, 35, 85, 95)),

        Meter(MeterMain, "Smart Meter — Main Board", "Facility A/Plant Room", new(null, null, 3200, 4000)),
        Meter(MeterPumpHouse, "Smart Meter — Pump House", "Facility A/Pump House", new(null, null, 2200, 2800)),
        Meter(PlugGrowLights, "Smart Plug — Grow Lights", "Facility A/Zone 1", new(null, null, 900, 1200)),

        Actuator(PumpA, "Pump A", "Facility A/Pump House", "Zone 1"),
        Actuator(PumpB, "Pump B", "Facility A/Pump House", "Zone 2"),
        Actuator(ValveLine1, "Irrigation Valve Line 1", "Facility A/Zone 1", "Zone 1"),
        Actuator(ValveLine2, "Irrigation Valve Line 2", "Facility A/Zone 2", "Zone 2"),
        Actuator(MistingValve, "Misting Valve", "Facility A/Zone 1", "Zone 1"),
        Actuator(RelayGrowLights, "Grow Light Relay", "Facility A/Zone 1", "Zone 1", on: true),
    };

    /// <summary>Starting point for each sensor's synthetic history (value, noise, drift per minute).</summary>
    private static readonly Dictionary<string, (double Start, double Noise, double DriftPerMin)> Profiles = new()
    {
        [MoistureA] = (58, 0.6, -0.9),
        [MoistureB] = (38, 0.5, -1.1),
        [MoistureC] = (47, 0.6, -0.8),
        [TempGh1] = (25.5, 0.3, 0.05),
        [TempGh2] = (24.0, 0.3, 0.02),
        [Humidity1] = (62, 1.0, 0.0),
        [MeterMain] = (1850, 60, 0),
        [MeterPumpHouse] = (640, 25, 0),
        [PlugGrowLights] = (410, 8, 0),
    };

    public static void Seed(CommandStreamEngine engine, DateTime? nowUtc = null, int seed = 7312)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        var rnd = new Random(seed);

        foreach (var device in Fleet())
            engine.RegisterDevice(device);

        // ---- 10 minutes of history at 5-second resolution (120 points per sensor) --------
        foreach (var (mac, p) in Profiles)
        {
            var points = new List<(DateTime, double)>();
            for (int i = 120; i >= 1; i--)
            {
                var at = now.AddSeconds(-5 * i);
                double minutesAgo = 5.0 * i / 60;
                double value = p.Start - p.DriftPerMin * minutesAgo + (rnd.NextDouble() - 0.5) * 2 * p.Noise;
                points.Add((at, Math.Round(value, 2)));
            }
            engine.SeedHistory(mac, points);
        }
        foreach (var actuator in Fleet().Where(d => d.IsActuator))
            engine.SeedHistory(actuator.MacAddress, new[] { (now.AddSeconds(-2), actuator.ActuatorOn ? 1.0 : 0.0) });

        SeedOperatorHistory(engine.Patterns, now);
    }

    /// <summary>
    /// Replays past operator sessions into the pattern engine. Each tuple is:
    /// trigger, how many times it happened, and the responses operators gave (with how often).
    /// </summary>
    public static void SeedOperatorHistory(ActionPatternEngine patterns, DateTime now)
    {
        var history = new (string Code, string Mac, int Occurrences, (OperatorAction Action, int Times)[] Responses)[]
        {
            ("MOISTURE_LOW", MoistureB, 6, new[] { (OperatorAction.Search(PumpA), 4), (OperatorAction.Command(PumpA, true), 5) }),
            ("MOISTURE_LOW", MoistureA, 5, new[] { (OperatorAction.Command(PumpA, true), 4), (OperatorAction.Command(ValveLine1, true), 3) }),
            ("MOISTURE_LOW", MoistureC, 5, new[] { (OperatorAction.Command(PumpB, true), 4), (OperatorAction.Command(ValveLine2, true), 3) }),
            ("MOISTURE_HIGH", MoistureA, 4, new[] { (OperatorAction.Command(PumpA, false), 4) }),
            ("MOISTURE_HIGH", MoistureB, 3, new[] { (OperatorAction.Command(PumpA, false), 3) }),
            ("MOISTURE_HIGH", MoistureC, 4, new[] { (OperatorAction.Command(PumpB, false), 3) }),
            ("TEMPERATURE_HIGH", TempGh1, 4, new[] { (OperatorAction.Command(MistingValve, true), 3), (OperatorAction.Command(RelayGrowLights, false), 3) }),
            ("POWER_HIGH", MeterPumpHouse, 3, new[] { (OperatorAction.Command(PumpB, false), 3) }),
            ("POWER_HIGH", MeterMain, 3, new[] { (OperatorAction.Command(RelayGrowLights, false), 2) }),
            ("ANOMALY_SPIKE", MeterMain, 3, new[] { (OperatorAction.Search(MeterPumpHouse), 2) }),
        };

        var at = now.AddDays(-21);
        foreach (var (code, mac, occurrences, responses) in history)
        {
            string key = ActionPatternEngine.TriggerKey(code, mac);
            for (int i = 0; i < occurrences; i++)
            {
                at = at.AddHours(7);
                patterns.ObserveTrigger(key, at);
                var t = at;
                foreach (var (action, times) in responses)
                {
                    if (i >= times) continue;          // this response happened on `times` of the occurrences
                    t = t.AddSeconds(40);
                    patterns.ObserveAction(action, t);
                }
            }
        }

        // Search → command habit (Markov chain): Moisture B search → Pump A search.
        for (int i = 0; i < 4; i++)
        {
            at = at.AddHours(5);
            patterns.ObserveAction(OperatorAction.Search(MoistureB), at);
            patterns.ObserveAction(OperatorAction.Search(PumpA), at.AddSeconds(20));
        }

        patterns.ResetSessionContext();
    }

    private static LiveDevice Sensor(string mac, string name, string location, string metric, string unit, Thresholds t) => new()
    {
        MacAddress = mac, DisplayName = name, Category = DeviceCategories.Environmental,
        Location = location, Metric = metric, Unit = unit, Thresholds = t
    };

    private static LiveDevice Meter(string mac, string name, string location, Thresholds t) => new()
    {
        MacAddress = mac, DisplayName = name, Category = DeviceCategories.Power,
        Location = location, Metric = "power", Unit = "W", Thresholds = t
    };

    private static LiveDevice Actuator(string mac, string name, string location, string zone, bool on = false) => new()
    {
        MacAddress = mac, DisplayName = name, Category = DeviceCategories.Actuator,
        Location = location, Metric = "state", Unit = "", ServesZone = zone, ActuatorOn = on,
        StatusMessage = on ? "Running" : "Idle"
    };
}
