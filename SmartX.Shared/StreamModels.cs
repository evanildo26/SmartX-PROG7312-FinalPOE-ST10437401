namespace SmartX.Shared;

// ============================================================================
//  Part 2 — Real-Time Command Stream: shared contracts between API, client
//  and the ESP32 fleet simulator.
// ============================================================================

/// <summary>Alert level of a reading or a device. Numeric order matters (higher = worse).</summary>
public enum AlertSeverity { Normal = 0, Warning = 1, Critical = 2 }

/// <summary>Which ingestion lane a packet travelled through.</summary>
public enum QueueLane { Standard = 0, Priority = 1 }

public static class DeviceCategories
{
    public const string Environmental = "Environmental";
    public const string Power = "Power Consumption";
    public const string Actuator = "Actuator";
    public static readonly string[] All = { Environmental, Power, Actuator };
}

/// <summary>Operating limits for a sensor. Any bound may be null (not monitored).</summary>
public record Thresholds(double? CriticalLow, double? WarningLow, double? WarningHigh, double? CriticalHigh);

/// <summary>
/// The live state of one registered device, stored in the gateway's
/// Dictionary&lt;string, LiveDevice&gt; keyed by MAC address.
/// </summary>
public class LiveDevice
{
    public string MacAddress { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Category { get; set; } = DeviceCategories.Environmental;
    public string Location { get; set; } = string.Empty;
    /// <summary>moisture, temperature, humidity, power or state (actuators).</summary>
    public string Metric { get; set; } = string.Empty;
    public string Unit { get; set; } = string.Empty;
    public Thresholds? Thresholds { get; set; }

    public double? LastValue { get; set; }
    public DateTime? LastSeenUtc { get; set; }
    public bool IsOnline { get; set; }
    public long ReadingCount { get; set; }

    /// <summary>For actuators (pumps, valves, relays): current commanded state.</summary>
    public bool ActuatorOn { get; set; }
    /// <summary>For actuators: the zone this actuator serves (used by the simulator's physics).</summary>
    public string? ServesZone { get; set; }

    public AlertSeverity Severity { get; set; }
    public string StatusMessage { get; set; } = "Awaiting first packet";

    public bool IsActuator => Category == DeviceCategories.Actuator;

    public LiveDevice Clone() => (LiveDevice)MemberwiseClone();
}

// ---- Device uplink / downlink ---------------------------------------------

/// <summary>Body an ESP32 node POSTs to /api/stream/ingest.</summary>
public record StreamIngestRequest(string MacAddress, double Value, string? Unit = null);

/// <summary>Gateway acknowledgement: tells the device which lane its packet entered.</summary>
public record IngestAck(Guid PacketId, string MacAddress, QueueLane Lane, AlertSeverity Severity,
                        int StandardQueueDepth, int PriorityQueueDepth);

/// <summary>A command waiting in a device's outbox until the device polls for it.</summary>
public record OutboxCommand(Guid CommandId, string MacAddress, bool TargetState, DateTime IssuedUtc);

// ---- Operator commands ------------------------------------------------------

/// <summary>Action is ON, OFF or TOGGLE.</summary>
public record DeviceCommandRequest(string MacAddress, string Action, string? Source = null);

/// <summary>Bulk override, e.g. "shut down all valves". Filters are combined (AND).</summary>
public record BulkCommandRequest(string Action, string? NameContains = null, string? Label = null, string? Source = null);

public record CommandChange(string MacAddress, string DisplayName, bool PreviousState, bool NewState);

/// <summary>One entry on the undo Stack&lt;CommandRecord&gt;. A bulk command is ONE record with many changes.</summary>
public class CommandRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Label { get; set; } = string.Empty;
    public string Source { get; set; } = "operator";
    public DateTime IssuedUtc { get; set; }
    public List<CommandChange> Changes { get; set; } = new();
    /// <summary>Applied, Undone or Redone.</summary>
    public string Status { get; set; } = "Applied";
}

public record CommandResult(bool Success, string Message, CommandRecord? Record = null);

// ---- Monitoring -------------------------------------------------------------

public record ProcessedPacketLog(Guid PacketId, string MacAddress, string DeviceName, double Value, string Unit,
                                 QueueLane Lane, AlertSeverity Severity, DateTime ReceivedUtc,
                                 DateTime ProcessedUtc, double QueueLatencyMs);

public record AlertEntry(string MacAddress, string DeviceName, string Code, AlertSeverity Severity,
                         string Message, DateTime RaisedUtc);

public record StreamStats(
    int DevicesTotal, int DevicesOnline,
    int StandardQueueDepth, int PriorityQueueDepth,
    long TotalProcessed, long PriorityProcessed,
    double AvgStandardLatencyMs, double AvgPriorityLatencyMs,
    int ActiveAlertCount, int CriticalAlertCount,
    long DuplicateAlertsSuppressed, long DuplicateDisconnectsSuppressed,
    int DisconnectedCount, int UndoDepth, int RedoDepth,
    string? NextUndoLabel, string? NextRedoLabel);

public record TimelinePoint(DateTime TimestampUtc, double Value);

/// <summary>Kind is "Suggested Action" (has a one-click command) or "Automated Insight".</summary>
public record SuggestedAction(string Id, string Kind, string Source, string Title, string Reason,
                              double Score, double Confidence, string? TargetMac, string? CommandAction);

/// <summary>A pattern the predictive engine has learned: "after Trigger, operators do Action".</summary>
public record LearnedPattern(string Trigger, string TriggerLabel, string Action, string ActionLabel,
                             int Count, int TriggerOccurrences, double Confidence);

public record BurstRequest(int StandardCount = 600, bool IncludeCritical = true, string? CriticalMac = null);
public record BurstResult(int StandardEnqueued, Guid? CriticalPacketId, string? CriticalDevice,
                          int StandardQueueDepth, int PriorityQueueDepth);

/// <summary>Everything the Command Stream dashboard needs, pushed once per second over SignalR.</summary>
public record StreamSnapshot(
    List<LiveDevice> Devices,
    StreamStats Stats,
    List<AlertEntry> ActiveAlerts,
    List<string> DisconnectedNodes,
    List<CommandRecord> CommandHistory,
    List<ProcessedPacketLog> RecentProcessed,
    List<SuggestedAction> Suggestions,
    DateTime GeneratedUtc);
