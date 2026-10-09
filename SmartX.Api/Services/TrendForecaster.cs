using SmartX.Shared;

namespace SmartX.Api.Services;

/// <summary>A forecast that a sensor will cross a critical threshold soon.</summary>
public record BreachForecast(string MacAddress, string AlertCode, double CurrentValue, double Threshold,
                             double SlopePerMinute, double MinutesToBreach);

/// <summary>
/// Predicts threshold breaches BEFORE they happen using an ordinary least-squares
/// linear regression over the most recent points of a sensor's timeline.
/// slope = Σ(x-x̄)(y-ȳ) / Σ(x-x̄)²  ;  minutes to breach = distance to threshold / |slope|
/// </summary>
public static class TrendForecaster
{
    public const int SampleSize = 30;
    public const int MinimumSamples = 10;
    public const double HorizonMinutes = 10;

    /// <param name="recentPoints">Points in ascending time order (the SortedDictionary guarantees this).</param>
    public static BreachForecast? Forecast(LiveDevice device, IReadOnlyList<KeyValuePair<DateTime, double>> recentPoints)
    {
        if (device.IsActuator || device.Thresholds is null || recentPoints.Count < MinimumSamples) return null;

        var t0 = recentPoints[0].Key;
        double n = recentPoints.Count, sumX = 0, sumY = 0;
        foreach (var p in recentPoints) { sumX += (p.Key - t0).TotalMinutes; sumY += p.Value; }
        double meanX = sumX / n, meanY = sumY / n, num = 0, den = 0;
        foreach (var p in recentPoints)
        {
            double dx = (p.Key - t0).TotalMinutes - meanX;
            num += dx * (p.Value - meanY);
            den += dx * dx;
        }
        if (den < 1e-9) return null;
        double slope = num / den;                         // units per minute
        double current = recentPoints[^1].Value;
        var t = device.Thresholds;
        string metric = device.Metric.ToUpperInvariant();

        if (slope < -1e-6 && t.CriticalLow is { } low && current > low)
        {
            double eta = (current - low) / -slope;
            if (eta <= HorizonMinutes) return new(device.MacAddress, $"{metric}_LOW", current, low, slope, eta);
        }
        if (slope > 1e-6 && t.CriticalHigh is { } high && current < high)
        {
            double eta = (high - current) / slope;
            if (eta <= HorizonMinutes) return new(device.MacAddress, $"{metric}_HIGH", current, high, slope, eta);
        }
        return null;
    }
}
