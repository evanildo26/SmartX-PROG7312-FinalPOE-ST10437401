using SmartX.Shared;

namespace SmartX.Client.Services;

/// <summary>
/// Form + list state for the Part 1 Ingestion page. Kept in a session-lifetime service so a
/// half-typed registration or the chosen upload target is still there after visiting another pillar.
/// </summary>
public sealed class IngestionState
{
    public string Mac { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public string Category { get; set; } = DeviceCategories.Environmental;
    public string PathInput { get; set; } = "Facility A/Zone 1/Sub-Zone B";
    public bool? PathResult { get; set; }
    public string UploadTargetMac { get; set; } = string.Empty;
    public List<SensorProfile> Sensors { get; set; } = new();
    public string? Message { get; set; }
    public bool MessageIsError { get; set; }
}
