using SmartX.Shared;

namespace SmartX.Api.Services.Topology;

/// <summary>
/// Physical layout of the Facility A deployment and the radio model used to weight the mesh links.
///
/// Radio model (simplified, but grounded in how ESP-NOW / Zigbee links behave):
///  • RSSI from the log-distance path-loss model: RSSI = −40 dBm − 10·n·log₁₀(d), with n = 2.7 for a greenhouse.
///  • Packet reception ratio (PRR) falls from 100 % at −70 dBm to 10 % near −92 dBm.
///  • Transmit energy grows with the square of the distance (free-space loss), and every lost packet is resent,
///    so the expected energy per delivered packet is (d/10)² ÷ PRR. That is the edge weight ("cost").
/// A minimum spanning tree over these weights is therefore the cheapest-energy backbone that still reaches every node.
/// </summary>
public static class MeshLayout
{
    public const double MapWidth = 120;
    public const double MapHeight = 80;
    /// <summary>Two nodes closer than this hear each other directly.</summary>
    public const double RadioRangeMeters = 26;
    public const double PathLossExponent = 2.7;

    public static readonly IReadOnlyList<MeshZoneDto> Zones = new List<MeshZoneDto>
    {
        new("Zone 1", 4, 8, 46, 68),
        new("Zone 2", 70, 4, 44, 36),
        new("Plant Room", 54, 38, 18, 20),
        new("Pump House", 80, 48, 24, 26),
    };

    /// <summary>Surveyed positions (metres) of the seeded fleet, keyed by MAC address.</summary>
    public static readonly IReadOnlyDictionary<string, (double X, double Y)> KnownPositions =
        new Dictionary<string, (double X, double Y)>(StringComparer.OrdinalIgnoreCase)
        {
            [MeshTopologyEngine.GatewayMac] = (60, 45),
            [FleetSeeder.MeterMain] = (66, 53),
            [FleetSeeder.MeterPumpHouse] = (86, 60),
            [FleetSeeder.PumpA] = (95, 55),
            [FleetSeeder.PumpB] = (97, 66),
            [FleetSeeder.ValveLine1] = (41, 40),
            [FleetSeeder.PlugGrowLights] = (43, 57),
            [FleetSeeder.RelayGrowLights] = (31, 63),
            [FleetSeeder.TempGh1] = (28, 45),
            [FleetSeeder.Humidity1] = (22, 30),
            [FleetSeeder.MistingValve] = (17, 53),
            [FleetSeeder.MoistureA] = (12, 20),
            [FleetSeeder.MoistureB] = (15, 70),
            [FleetSeeder.ValveLine2] = (79, 31),
            [FleetSeeder.TempGh2] = (92, 22),
            [FleetSeeder.MoistureC] = (104, 12),
        };

    /// <summary>"Facility A/Zone 1/Sub-Zone B" → "Zone 1"; "Facility A/Pump House" → "Pump House".</summary>
    public static string ZoneOf(string? location)
    {
        var parts = (location ?? string.Empty).Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? parts[1] : parts.FirstOrDefault() ?? "Plant Room";
    }

    /// <summary>
    /// Position for a device that was registered later (e.g. on the Ingestion page): inside its zone,
    /// offset by a stable hash of its MAC so it lands in the same spot every time the API starts.
    /// </summary>
    public static (double X, double Y) PositionFor(string mac, string zone)
    {
        if (KnownPositions.TryGetValue(mac, out var known)) return known;

        var area = Zones.FirstOrDefault(z => string.Equals(z.Name, zone, StringComparison.OrdinalIgnoreCase))
                   ?? Zones.First(z => z.Name == "Plant Room");
        int hash = 17;
        foreach (char c in mac) hash = unchecked(hash * 31 + c);
        hash &= int.MaxValue;
        double x = area.X + 3 + hash % 1000 / 1000.0 * (area.Width - 6);
        double y = area.Y + 3 + hash / 1000 % 1000 / 1000.0 * (area.Height - 6);
        return (Math.Round(x, 1), Math.Round(y, 1));
    }

    public static double Distance((double X, double Y) a, (double X, double Y) b) =>
        Math.Round(Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2)), 1);

    /// <summary>Expected RSSI (dBm) at a given distance, from the log-distance path-loss model.</summary>
    public static int EstimateRssi(double distanceMeters) =>
        (int)Math.Round(-40 - 10 * PathLossExponent * Math.Log10(Math.Max(distanceMeters, 1)));

    /// <summary>Share of packets that arrive first time at this signal strength (0.1 – 1.0).</summary>
    public static double PacketReceptionRatio(int rssi) => Math.Clamp((rssi + 95) / 25.0, 0.1, 1.0);

    /// <summary>Relative radio energy to deliver one packet over the link: (d/10)² ÷ PRR.</summary>
    public static double LinkCost(double distanceMeters, int rssi) =>
        Math.Round(Math.Max(0.5, Math.Pow(distanceMeters / 10.0, 2)) / PacketReceptionRatio(rssi), 2);
}
