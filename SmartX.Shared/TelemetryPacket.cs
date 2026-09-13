using System;
using System.Collections.Generic;
using System.Text;

namespace SmartX.Shared;

/// <summary>
/// Generic wrapper so the gateway can carry any telemetry payload type
/// (float, int, bool, etc.) without boxing/unboxing or type-coercion overhead.
/// </summary>
/// 
public class TelemetryPacket<T>
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string SensorId { get; set; } = string.Empty;
    public T Value { get; set; } = default!;
    public string Unit { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string DataType => typeof(T).Name;

    public override string ToString() => $"[{Timestamp:HH:mm:ss}] {SensorId}: {Value}{Unit} ({DataType})";
}