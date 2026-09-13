using System;
using System.Collections.Generic;
using System.Text;

namespace SmartX.Shared;

/// <summary>
/// Represents a power/wattage reading from a smart meter node.
/// Operators let us aggregate or diff two meters directly in code.
/// </summary>
public class PowerReading
{
    public string SensorId { get; set; } = string.Empty;
    public double Watts { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    public static PowerReading operator +(PowerReading a, PowerReading b) =>
        new() { SensorId = $"{a.SensorId}+{b.SensorId}", Watts = a.Watts + b.Watts };

    public static PowerReading operator -(PowerReading a, PowerReading b) =>
        new() { SensorId = $"{a.SensorId}-{b.SensorId}", Watts = a.Watts - b.Watts };

    public static bool operator >(PowerReading a, PowerReading b) => a.Watts > b.Watts;
    public static bool operator <(PowerReading a, PowerReading b) => a.Watts < b.Watts;

    public static bool operator ==(PowerReading? a, PowerReading? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        return Math.Abs(a.Watts - b.Watts) < 0.0001;
    }
    public static bool operator !=(PowerReading? a, PowerReading? b) => !(a == b);

    public override bool Equals(object? obj) => obj is PowerReading p && this == p;
    public override int GetHashCode() => Watts.GetHashCode();
    public override string ToString() => $"{SensorId}: {Watts:F1}W";
}