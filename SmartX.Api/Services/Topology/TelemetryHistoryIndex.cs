using System.Diagnostics;
using SmartX.Shared;

namespace SmartX.Api.Services.Topology;

/// <summary>
/// Long-term, tree-indexed telemetry history: one <see cref="AvlTree{TKey,TValue}"/> per sensor,
/// keyed by timestamp. The Part 2 SortedDictionary keeps ~12 minutes for the live chart; this index
/// keeps days of readings so the topology page can answer "all moisture readings between 14:00 and
/// 16:00 for Node A" in O(log n + k) instead of scanning every reading.
///
/// Not thread-safe on its own: <see cref="MeshTopologyEngine"/> guards it with its lock.
/// </summary>
public sealed class TelemetryHistoryIndex
{
    /// <summary>Upper bound per sensor (~2 weeks at 30 s, or ~14 hours of 1 Hz live data on top of the seed).</summary>
    public const int DefaultCapacityPerSensor = 60_000;

    private readonly Dictionary<string, AvlTree<DateTime, double>> _trees = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _capacity;

    public TelemetryHistoryIndex(int capacityPerSensor = DefaultCapacityPerSensor) =>
        _capacity = Math.Max(10, capacityPerSensor);

    public int SensorCount => _trees.Count;
    public long TotalPoints => _trees.Values.Sum(t => (long)t.Count);
    public IEnumerable<string> Macs => _trees.Keys;

    /// <summary>Insert (or overwrite) one reading. O(log n). Trims the oldest reading when the sensor is full.</summary>
    public void Add(string mac, DateTime atUtc, double value)
    {
        if (!_trees.TryGetValue(mac, out var tree))
            _trees[mac] = tree = new AvlTree<DateTime, double>();
        tree.Insert(atUtc, value);
        while (tree.Count > _capacity) tree.RemoveMin();
    }

    public bool TryGetTree(string mac, out AvlTree<DateTime, double> tree) => _trees.TryGetValue(mac, out tree!);

    /// <summary>
    /// Range query: every reading with from ≤ timestamp ≤ to, in time order, plus statistics.
    /// The returned points are evenly thinned to <paramref name="maxPoints"/> for transfer; the
    /// statistics always cover every match.
    /// </summary>
    public HistoryQueryResult Query(LiveDevice device, DateTime fromUtc, DateTime toUtc, int maxPoints)
    {
        if (fromUtc > toUtc) (fromUtc, toUtc) = (toUtc, fromUtc);
        maxPoints = Math.Clamp(maxPoints, 2, 5000);

        if (!_trees.TryGetValue(device.MacAddress, out var tree))
            return new HistoryQueryResult(device.MacAddress, device.DisplayName, device.Unit, fromUtc, toUtc,
                0, null, null, null, new List<TimelinePoint>(), false, 0, 0, 0, 0, 0);

        var watch = Stopwatch.StartNew();
        var matches = tree.Range(fromUtc, toUtc, out int visited);
        watch.Stop();

        double? min = null, max = null, avg = null;
        if (matches.Count > 0)
        {
            double lo = double.MaxValue, hi = double.MinValue, sum = 0;
            foreach (var (_, value) in matches)
            {
                if (value < lo) lo = value;
                if (value > hi) hi = value;
                sum += value;
            }
            min = Math.Round(lo, 2);
            max = Math.Round(hi, 2);
            avg = Math.Round(sum / matches.Count, 2);
        }

        var points = new List<TimelinePoint>(Math.Min(matches.Count, maxPoints));
        bool truncated = matches.Count > maxPoints;
        if (!truncated)
        {
            foreach (var (at, value) in matches) points.Add(new TimelinePoint(at, value));
        }
        else
        {
            double step = (matches.Count - 1) / (double)(maxPoints - 1);
            for (int i = 0; i < maxPoints; i++)
            {
                var (at, value) = matches[(int)Math.Round(i * step)];
                points.Add(new TimelinePoint(at, value));
            }
        }

        return new HistoryQueryResult(device.MacAddress, device.DisplayName, device.Unit, fromUtc, toUtc,
            matches.Count, min, max, avg, points, truncated,
            tree.Count, tree.Height, visited, tree.Count, Math.Round(watch.Elapsed.TotalMicroseconds, 1));
    }

    public HistoryIndexInfo Describe(LiveDevice device)
    {
        if (!_trees.TryGetValue(device.MacAddress, out var tree))
            return new HistoryIndexInfo(device.MacAddress, device.DisplayName, device.Unit, 0, 0, null, null);
        DateTime? oldest = tree.TryGetMin(out var first, out _) ? first : null;
        DateTime? newest = tree.TryGetMax(out var last, out _) ? last : null;
        return new HistoryIndexInfo(device.MacAddress, device.DisplayName, device.Unit, tree.Count, tree.Height, oldest, newest);
    }

    // =====================================================================================
    //  Seeding: days of realistic history so range queries run on a large data set
    // =====================================================================================

    /// <summary>
    /// Generates <paramref name="span"/> of readings at <paramref name="step"/> resolution, ending at
    /// <paramref name="untilUtc"/>, following the farm's daily rhythm: temperature and humidity follow the sun,
    /// soil moisture falls between irrigation cycles every 6 hours, pumps and valves run for 20 minutes per cycle,
    /// grow lights run 06:00–22:00, and power meters add up whatever is switched on.
    /// </summary>
    public void SeedSynthetic(IEnumerable<LiveDevice> devices, DateTime untilUtc, TimeSpan span, TimeSpan step, int seed = 7312)
    {
        var rnd = new Random(seed);
        var start = untilUtc - span;
        var fleet = devices.ToList();

        foreach (var device in fleet)
        {
            double phase = PhaseMinutes(device.MacAddress);
            for (var at = start; at <= untilUtc; at = at.Add(step))
                Add(device.MacAddress, at, Math.Round(Synthesise(device, at, phase, rnd), 2));
        }
    }

    private static double PhaseMinutes(string mac)
    {
        int hash = 7;
        foreach (char c in mac) hash = unchecked(hash * 31 + c);
        return (hash & int.MaxValue) % 90;     // irrigation zones start a little apart
    }

    private static double Synthesise(LiveDevice d, DateTime atUtc, double phase, Random rnd)
    {
        var local = atUtc.AddHours(2);                         // South African Standard Time (UTC+2)
        double hour = local.Hour + local.Minute / 60.0;
        double sun = Math.Sin(2 * Math.PI * (hour - 9) / 24);  // peaks at 15:00, lowest at 03:00
        double minutesIntoCycle = ((atUtc - DateTime.UnixEpoch).TotalMinutes + phase) % 360;
        bool irrigating = minutesIntoCycle < 20;
        bool lightsOn = hour >= 6 && hour < 22;
        double Noise(double amplitude) => (rnd.NextDouble() - 0.5) * 2 * amplitude;

        switch (d.Metric)
        {
            case "moisture":
                return Math.Clamp(72 - 0.085 * minutesIntoCycle + Noise(0.5), 5, 100);
            case "temperature":
                return 21 + 6 * sun + (d.Location.Contains("Zone 1") ? 1 : 0) + Noise(0.3);
            case "humidity":
                return Math.Clamp(66 - 12 * sun + Noise(1.0), 20, 100);
            case "power":
                double pumps = irrigating ? 900 : 0;
                double lights = lightsOn ? 420 : 0;
                double watts = d.DisplayName.Contains("Main", StringComparison.OrdinalIgnoreCase) ? 1500 + lights + pumps
                             : d.DisplayName.Contains("Pump House", StringComparison.OrdinalIgnoreCase) ? 120 + pumps
                             : lightsOn ? 410 : 4;
                return watts + Noise(watts * 0.03);
            case "state":
                string name = d.DisplayName;
                bool on = name.Contains("Pump", StringComparison.OrdinalIgnoreCase) || name.Contains("Valve Line", StringComparison.OrdinalIgnoreCase)
                    ? irrigating
                    : name.Contains("Grow Light", StringComparison.OrdinalIgnoreCase) ? lightsOn
                    : 21 + 6 * sun > 26.5;                    // misting valve: on during the hottest hours
                return on ? 1 : 0;
            default:
                return 50 + Noise(5);
        }
    }
}
