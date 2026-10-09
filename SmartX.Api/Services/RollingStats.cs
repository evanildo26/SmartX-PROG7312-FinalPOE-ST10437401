namespace SmartX.Api.Services;

/// <summary>
/// Exponentially-weighted moving mean/variance. O(1) memory per sensor, so the
/// gateway can score every packet for anomalies without re-scanning history.
/// </summary>
public sealed class RollingStats
{
    private const double Alpha = 0.15;
    public int Count { get; private set; }
    public double Mean { get; private set; }
    public double Variance { get; private set; }

    public double StdDev => Math.Sqrt(Variance);

    /// <summary>How many standard deviations a value is from the running mean (0 until warmed up).</summary>
    public double ZScore(double value)
    {
        if (Count < 8) return 0;
        double sd = Math.Max(StdDev, 0.5);   // floor avoids divide-by-tiny on very stable sensors
        return Math.Abs(value - Mean) / sd;
    }

    public void Update(double value)
    {
        if (Count == 0) { Mean = value; Variance = 0; }
        else
        {
            double diff = value - Mean;
            double incr = Alpha * diff;
            Mean += incr;
            Variance = (1 - Alpha) * (Variance + diff * incr);
        }
        Count++;
    }
}
