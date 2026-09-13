using System;
using System.Collections.Generic;
using System.Text;

namespace SmartX.Shared;

public class SensorProfile
{
    public string MacAddress { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty; // Environmental, Power Consumption, Actuator

    // Raw sequential batches arrive as a jagged array (each batch can differ in size)
    public double[][] RawHistoricalBatches { get; set; } = Array.Empty<double[]>();

    // Once validated, values are moved into an optimised List<T> for processing
    public List<double> ProcessedReadings { get; set; } = new();

    public List<string> AttachedFiles { get; set; } = new();
    public List<TelemetryPacket<double>> TelemetryHistory { get; set; } = new();

    /// <summary>Flattens the jagged array into the processed List, simulating the
    /// "raw arrays -> optimised collection" pipeline the brief asks for.</summary>
    public void FlattenBatchesIntoList()
    {
        ProcessedReadings.Clear();
        foreach (var batch in RawHistoricalBatches)
            ProcessedReadings.AddRange(batch);
    }
}

//---------------------------------------------------END-------------------------------//