using SmartX.Shared;

namespace SmartX.Api.Services;

/// <summary>
/// The heart of Part 2: the gateway's real-time processing engine.
///
/// ┌──────────── Data structure ────────────────────────────┬──── Why ─────────────────────────────────────┐
/// │ Queue&lt;TelemetryPacket&lt;double&gt;&gt;          _standardQueue │ FIFO lane for normal telemetry               │
/// │ PriorityQueue&lt;TelemetryPacket&lt;double&gt;,(int,long)&gt;    │ critical packets bypass the FIFO lane        │
/// │ Stack&lt;CommandRecord&gt;  _undoStack / _redoStack           │ LIFO undo / redo of manual overrides         │
/// │ Dictionary&lt;string, LiveDevice&gt;   _devices (MAC key)   │ O(1) lookup on every packet                  │
/// │ Dictionary&lt;string, HashSet&lt;string&gt;&gt; _categoryIndex   │ O(1) jump to all devices of a category       │
/// │ Dictionary&lt;string, SortedDictionary&lt;DateTime,double&gt;&gt; │ per-sensor timeline, always time-ordered     │
/// │ HashSet&lt;AlertEntry&gt;  _activeAlerts (MAC+code identity) │ unique alert states, drops duplicates        │
/// │ HashSet&lt;string&gt;      _disconnectedNodes                │ unique offline nodes, one disconnect alert   │
/// │ Dictionary&lt;string, Queue&lt;OutboxCommand&gt;&gt; _outbox     │ per-device downlink mailbox (FIFO)           │
/// └────────────────────────────────────────────────────────┴──────────────────────────────────────────────┘
///
/// The collections in System.Collections.Generic are not thread-safe, and the engine is
/// touched by HTTP requests, the background processor and the SignalR broadcaster at the
/// same time, so every public member takes a single lock. Each critical section is short
/// (O(1) or O(log n) work), so contention is negligible at gateway packet rates.
/// </summary>
public sealed class CommandStreamEngine
{
    public const int TimelineCapacity = 720;      // ~12 minutes at 1 Hz per sensor
    public const int RecentProcessedCapacity = 40;
    public const int CommandLogCapacity = 60;
    public const int AlertHistoryCapacity = 60;
    private const double SpikeZScore = 4.0;

    private readonly object _gate = new();
    private readonly Func<DateTime> _clock;
    private readonly ActionPatternEngine _patterns;

    // ---- Stacks, Queues, Priority Queues -----------------------------------------------
    private readonly Queue<TelemetryPacket<double>> _standardQueue = new();
    // Priority = (rank, sequence). Lower rank is served first; the sequence number keeps
    // FIFO order between packets of the same rank (PriorityQueue itself is not stable).
    private readonly PriorityQueue<TelemetryPacket<double>, (int Rank, long Seq)> _priorityQueue = new();
    private long _sequence;
    private readonly Stack<CommandRecord> _undoStack = new();
    private readonly Stack<CommandRecord> _redoStack = new();

    // ---- Hash tables, Dictionaries, Sorted Dictionaries ---------------------------------
    private readonly Dictionary<string, LiveDevice> _devices = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _categoryIndex = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SortedDictionary<DateTime, double>> _timelines = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RollingStats> _stats = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Queue<OutboxCommand>> _outbox = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _disconnectCounts = new(StringComparer.OrdinalIgnoreCase);

    // ---- Sets ---------------------------------------------------------------------------
    private readonly HashSet<AlertEntry> _activeAlerts = new(AlertIdentityComparer.Instance);
    private readonly HashSet<string> _disconnectedNodes = new(StringComparer.OrdinalIgnoreCase);

    // ---- Rolling logs (bounded FIFO buffers) ---------------------------------------------
    private readonly Queue<ProcessedPacketLog> _recentProcessed = new();
    private readonly Queue<ProcessedPacketLog> _recentPriority = new();   // last few priority-lane packets, pinned on the dashboard
    private readonly LinkedList<CommandRecord> _commandLog = new();   // newest first
    private readonly Queue<AlertEntry> _alertHistory = new();

    // ---- Counters -----------------------------------------------------------------------
    private long _totalProcessed, _priorityProcessed, _duplicateAlerts, _duplicateDisconnects;
    private double _standardLatencySum, _priorityLatencySum;
    private long _standardLatencyCount;

    public CommandStreamEngine(ActionPatternEngine? patterns = null, Func<DateTime>? clock = null)
    {
        _patterns = patterns ?? new ActionPatternEngine();
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    public ActionPatternEngine Patterns => _patterns;

    public static string NormalizeMac(string mac) => (mac ?? string.Empty).Trim().ToUpperInvariant();

    // =====================================================================================
    //  Registration
    // =====================================================================================

    public LiveDevice RegisterDevice(LiveDevice device)
    {
        lock (_gate)
        {
            device.MacAddress = NormalizeMac(device.MacAddress);
            if (string.IsNullOrWhiteSpace(device.DisplayName)) device.DisplayName = device.MacAddress;

            // Re-registering replaces the record; remove it from its old category bucket first.
            if (_devices.TryGetValue(device.MacAddress, out var old) &&
                _categoryIndex.TryGetValue(old.Category, out var oldBucket))
                oldBucket.Remove(old.MacAddress);

            _devices[device.MacAddress] = device;
            if (!_categoryIndex.TryGetValue(device.Category, out var bucket))
                _categoryIndex[device.Category] = bucket = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bucket.Add(device.MacAddress);

            if (!_timelines.ContainsKey(device.MacAddress))
                _timelines[device.MacAddress] = new SortedDictionary<DateTime, double>();
            if (!_stats.ContainsKey(device.MacAddress))
                _stats[device.MacAddress] = new RollingStats();
            return device.Clone();
        }
    }

    public bool IsRegistered(string mac)
    {
        lock (_gate) return _devices.ContainsKey(NormalizeMac(mac));
    }

    /// <summary>Seeds historical readings straight into a timeline (used at start-up).</summary>
    public void SeedHistory(string mac, IEnumerable<(DateTime At, double Value)> points)
    {
        lock (_gate)
        {
            mac = NormalizeMac(mac);
            if (!_devices.TryGetValue(mac, out var device)) return;
            var timeline = _timelines[mac];
            var stats = _stats[mac];
            foreach (var (at, value) in points)
            {
                AddToTimeline(timeline, at, value);
                stats.Update(value);
                device.LastValue = value;
                device.LastSeenUtc = at;
                device.ReadingCount++;
            }
            device.IsOnline = device.LastSeenUtc is not null;
            if (device.LastValue is { } last)
            {
                var (sev, _, msg) = Evaluate(device, last, stats);
                device.Severity = sev;
                device.StatusMessage = msg ?? "Nominal";
            }
        }
    }

    // =====================================================================================
    //  Ingestion: Queue<T> + PriorityQueue
    // =====================================================================================

    /// <summary>
    /// Device uplink. Classifies the packet in O(1) and routes it to either the
    /// standard FIFO queue or the priority queue. Returns null for unknown devices.
    /// </summary>
    public IngestAck? Enqueue(StreamIngestRequest request, QueueLane? forceLane = null)
    {
        lock (_gate)
        {
            string mac = NormalizeMac(request.MacAddress);
            if (!_devices.TryGetValue(mac, out var device)) return null;

            var packet = new TelemetryPacket<double>
            {
                SensorId = mac,
                Value = request.Value,
                Unit = request.Unit ?? device.Unit,
                Timestamp = _clock()
            };

            var (severity, code, _) = Evaluate(device, request.Value, _stats[mac]);
            QueueLane lane = forceLane ?? (severity == AlertSeverity.Critical ? QueueLane.Priority : QueueLane.Standard);

            if (lane == QueueLane.Priority)
            {
                // Rank 0 = hard threshold breach, rank 1 = statistical spike.
                int rank = code == "ANOMALY_SPIKE" ? 1 : 0;
                _priorityQueue.Enqueue(packet, (rank, _sequence++));
            }
            else
            {
                _standardQueue.Enqueue(packet);
            }

            return new IngestAck(packet.Id, mac, lane, severity, _standardQueue.Count, _priorityQueue.Count);
        }
    }

    /// <summary>
    /// Processes up to <paramref name="maxPackets"/> packets. The priority queue is ALWAYS
    /// drained first, so a critical alert never waits behind a backlog of routine readings.
    /// </summary>
    public int ProcessBatch(int maxPackets)
    {
        lock (_gate)
        {
            int processed = 0;
            while (processed < maxPackets)
            {
                if (_priorityQueue.TryDequeue(out var urgent, out _))
                    ProcessPacketLocked(urgent, QueueLane.Priority);
                else if (_standardQueue.TryDequeue(out var routine))
                    ProcessPacketLocked(routine, QueueLane.Standard);
                else
                    break;
                processed++;
            }
            return processed;
        }
    }

    private void ProcessPacketLocked(TelemetryPacket<double> packet, QueueLane lane)
    {
        var now = _clock();
        if (!_devices.TryGetValue(packet.SensorId, out var device)) return;   // O(1) MAC lookup
        var stats = _stats[packet.SensorId];

        // A packet from a node in the disconnected set means it has come back online.
        if (_disconnectedNodes.Remove(device.MacAddress))
            _activeAlerts.RemoveWhere(a => a.MacAddress == device.MacAddress && a.Code == "OFFLINE");
        device.IsOnline = true;

        var (severity, code, message) = Evaluate(device, packet.Value, stats);
        stats.Update(packet.Value);

        device.LastValue = packet.Value;
        device.LastSeenUtc = packet.Timestamp;
        device.ReadingCount++;
        AddToTimeline(_timelines[device.MacAddress], packet.Timestamp, packet.Value);

        if (code is not null)
        {
            // Clear a different value alert first (e.g. LOW resolved, now HIGH).
            _activeAlerts.RemoveWhere(a => a.MacAddress == device.MacAddress && a.Code != code && a.Code != "OFFLINE");
            RaiseAlertLocked(device, code, severity, message!, now);
        }
        else
        {
            _activeAlerts.RemoveWhere(a => a.MacAddress == device.MacAddress && a.Code != "OFFLINE");
        }
        device.Severity = severity;
        device.StatusMessage = message ?? (device.IsActuator ? (device.ActuatorOn ? "Running" : "Idle") : "Nominal");

        double latency = (now - packet.Timestamp).TotalMilliseconds;
        _totalProcessed++;
        if (lane == QueueLane.Priority) { _priorityProcessed++; _priorityLatencySum += latency; }
        else { _standardLatencyCount++; _standardLatencySum += latency; }

        _recentProcessed.Enqueue(new ProcessedPacketLog(packet.Id, device.MacAddress, device.DisplayName, packet.Value,
            packet.Unit, lane, severity, packet.Timestamp, now, Math.Round(latency, 1)));
        while (_recentProcessed.Count > RecentProcessedCapacity) _recentProcessed.Dequeue();
        if (lane == QueueLane.Priority)
        {
            _recentPriority.Enqueue(_recentProcessed.Last());
            while (_recentPriority.Count > 5) _recentPriority.Dequeue();
        }
    }

    private static void AddToTimeline(SortedDictionary<DateTime, double> timeline, DateTime at, double value)
    {
        // Keys must be unique: nudge by one tick on the (rare) identical timestamp.
        while (timeline.ContainsKey(at)) at = at.AddTicks(1);
        timeline[at] = value;                                   // O(log n), stays sorted
        while (timeline.Count > TimelineCapacity)
            timeline.Remove(timeline.Keys.First());             // oldest key = smallest key
    }

    /// <summary>Classifies a reading against the device thresholds, then against its own statistics.</summary>
    internal static (AlertSeverity Severity, string? Code, string? Message) Evaluate(LiveDevice d, double v, RollingStats stats)
    {
        if (d.IsActuator) return (AlertSeverity.Normal, null, null);
        string metric = string.IsNullOrWhiteSpace(d.Metric) ? "VALUE" : d.Metric.ToUpperInvariant();
        string unit = d.Unit;

        if (d.Thresholds is { } t)
        {
            if (t.CriticalLow is { } cl && v <= cl)
                return (AlertSeverity.Critical, $"{metric}_LOW", $"{d.DisplayName} critically low: {v:F1}{unit} (limit {cl}{unit})");
            if (t.CriticalHigh is { } ch && v >= ch)
                return (AlertSeverity.Critical, $"{metric}_HIGH", $"{d.DisplayName} critically high: {v:F1}{unit} (limit {ch}{unit})");
            if (t.WarningLow is { } wl && v <= wl)
                return (AlertSeverity.Warning, $"{metric}_LOW", $"{d.DisplayName} dropping: {v:F1}{unit} (warn below {wl}{unit})");
            if (t.WarningHigh is { } wh && v >= wh)
                return (AlertSeverity.Warning, $"{metric}_HIGH", $"{d.DisplayName} elevated: {v:F1}{unit} (warn above {wh}{unit})");
        }

        double z = stats.ZScore(v);
        if (z >= SpikeZScore)
            return (AlertSeverity.Critical, "ANOMALY_SPIKE",
                    $"{d.DisplayName} spiked to {v:F1}{unit} ({z:F1}σ from its rolling mean {stats.Mean:F1}{unit})");
        return (AlertSeverity.Normal, null, null);
    }

    // =====================================================================================
    //  Sets: unique alert states and disconnected nodes
    // =====================================================================================

    /// <returns>true when this is a NEW alert; false when the set rejected a duplicate.</returns>
    private bool RaiseAlertLocked(LiveDevice device, string code, AlertSeverity severity, string message, DateTime now)
    {
        var candidate = new AlertEntry(device.MacAddress, device.DisplayName, code, severity, message, now);

        if (_activeAlerts.TryGetValue(candidate, out var existing))
        {
            if (existing.Severity == severity)
            {
                // Same device + same code already active: the HashSet stops a duplicate alert.
                _duplicateAlerts++;
                return false;
            }
            // Escalation / de-escalation (e.g. Warning → Critical): replace, keep original raise time.
            _activeAlerts.Remove(existing);
            _activeAlerts.Add(candidate with { RaisedUtc = existing.RaisedUtc });
            if (severity > existing.Severity) AppendAlertHistory(candidate);
            return false;
        }

        _activeAlerts.Add(candidate);
        AppendAlertHistory(candidate);
        _patterns.ObserveTrigger(ActionPatternEngine.TriggerKey(code, device.MacAddress), now);
        return true;
    }

    private void AppendAlertHistory(AlertEntry entry)
    {
        _alertHistory.Enqueue(entry);
        while (_alertHistory.Count > AlertHistoryCapacity) _alertHistory.Dequeue();
    }

    /// <summary>
    /// Heartbeat watchdog. A node silent for longer than <paramref name="timeout"/> is added to the
    /// disconnected HashSet. HashSet.Add returns false if it is already there, so the watchdog can
    /// run every second without ever raising a second disconnect alert for the same node.
    /// </summary>
    public int CheckHeartbeats(TimeSpan timeout)
    {
        lock (_gate)
        {
            var now = _clock();
            int newlyDisconnected = 0;
            foreach (var device in _devices.Values)
            {
                if (device.LastSeenUtc is not { } seen || now - seen <= timeout) continue;

                if (_disconnectedNodes.Add(device.MacAddress))
                {
                    newlyDisconnected++;
                    device.IsOnline = false;
                    device.Severity = AlertSeverity.Critical;
                    _disconnectCounts[device.MacAddress] = _disconnectCounts.GetValueOrDefault(device.MacAddress) + 1;
                    device.StatusMessage = $"No heartbeat for {(int)(now - seen).TotalSeconds}s";
                    RaiseAlertLocked(device, "OFFLINE", AlertSeverity.Critical,
                        $"{device.DisplayName} stopped reporting (last packet {seen:HH:mm:ss} UTC)", now);
                }
                else
                {
                    _duplicateDisconnects++;   // already known to be down — suppressed
                    device.StatusMessage = $"No heartbeat for {(int)(now - seen).TotalSeconds}s";
                }
            }
            return newlyDisconnected;
        }
    }

    // =====================================================================================
    //  Commands: Stack<T> undo / redo + per-device outbox
    // =====================================================================================

    public CommandResult IssueCommand(DeviceCommandRequest request)
    {
        lock (_gate)
        {
            string mac = NormalizeMac(request.MacAddress);
            if (!_devices.TryGetValue(mac, out var device))
                return new(false, $"Unknown device {mac}.");
            if (!device.IsActuator)
                return new(false, $"{device.DisplayName} is a sensor, not an actuator — it cannot be switched.");

            bool? target = ParseAction(request.Action, device.ActuatorOn);
            if (target is null) return new(false, $"Unknown action '{request.Action}'. Use ON, OFF or TOGGLE.");
            if (target == device.ActuatorOn)
                return new(false, $"{device.DisplayName} is already {(device.ActuatorOn ? "ON" : "OFF")}.");

            var record = new CommandRecord
            {
                Label = $"{device.DisplayName} → {(target.Value ? "ON" : "OFF")}",
                Source = request.Source ?? "operator",
                IssuedUtc = _clock(),
                Changes = { new CommandChange(mac, device.DisplayName, device.ActuatorOn, target.Value) }
            };
            return CommitLocked(record);
        }
    }

    /// <summary>One bulk override = one stack entry, so a single Undo reverts every device it touched.</summary>
    public CommandResult IssueBulkCommand(BulkCommandRequest request)
    {
        lock (_gate)
        {
            if (!_categoryIndex.TryGetValue(DeviceCategories.Actuator, out var actuatorMacs))
                return new(false, "No actuators registered.");

            var record = new CommandRecord
            {
                Source = request.Source ?? "operator",
                IssuedUtc = _clock(),
            };
            foreach (var mac in actuatorMacs.OrderBy(m => m))
            {
                var device = _devices[mac];
                if (!string.IsNullOrWhiteSpace(request.NameContains) &&
                    !device.DisplayName.Contains(request.NameContains, StringComparison.OrdinalIgnoreCase)) continue;
                bool? target = ParseAction(request.Action, device.ActuatorOn);
                if (target is null) return new(false, $"Unknown action '{request.Action}'.");
                if (target == device.ActuatorOn) continue;
                record.Changes.Add(new CommandChange(mac, device.DisplayName, device.ActuatorOn, target.Value));
            }
            if (record.Changes.Count == 0) return new(false, "Nothing to change — all matching actuators are already in that state.");

            record.Label = request.Label ??
                $"Bulk {request.Action.ToUpperInvariant()} ({record.Changes.Count} device{(record.Changes.Count == 1 ? "" : "s")})";
            return CommitLocked(record);
        }
    }

    private CommandResult CommitLocked(CommandRecord record)
    {
        ApplyChangesLocked(record.Changes, useNewState: true);
        _undoStack.Push(record);
        _redoStack.Clear();                     // a new action invalidates the redo branch
        LogCommandLocked(record);

        if (record.Source != "seed")
            foreach (var change in record.Changes)
                _patterns.ObserveAction(OperatorAction.Command(change.MacAddress, change.NewState), record.IssuedUtc);

        return new(true, $"Applied: {record.Label}", record);
    }

    /// <summary>Pops the most recent command (LIFO) and reverts every device it changed.</summary>
    public CommandResult Undo()
    {
        lock (_gate)
        {
            if (!_undoStack.TryPop(out var record)) return new(false, "Nothing to undo.");
            ApplyChangesLocked(record.Changes, useNewState: false);
            record.Status = "Undone";
            _redoStack.Push(record);
            return new(true, $"Reverted: {record.Label}", record);
        }
    }

    public CommandResult Redo()
    {
        lock (_gate)
        {
            if (!_redoStack.TryPop(out var record)) return new(false, "Nothing to redo.");
            ApplyChangesLocked(record.Changes, useNewState: true);
            record.Status = "Redone";
            _undoStack.Push(record);
            return new(true, $"Re-applied: {record.Label}", record);
        }
    }

    private void ApplyChangesLocked(IEnumerable<CommandChange> changes, bool useNewState)
    {
        foreach (var change in changes)
        {
            if (!_devices.TryGetValue(change.MacAddress, out var device)) continue;
            bool state = useNewState ? change.NewState : change.PreviousState;
            device.ActuatorOn = state;
            device.LastValue = state ? 1 : 0;
            device.StatusMessage = state ? "Running" : "Idle";

            // Downlink: the device collects this on its next poll (FIFO per device).
            if (!_outbox.TryGetValue(device.MacAddress, out var box))
                _outbox[device.MacAddress] = box = new Queue<OutboxCommand>();
            box.Enqueue(new OutboxCommand(Guid.NewGuid(), device.MacAddress, state, _clock()));
        }
    }

    private void LogCommandLocked(CommandRecord record)
    {
        _commandLog.AddFirst(record);
        while (_commandLog.Count > CommandLogCapacity) _commandLog.RemoveLast();
    }

    private static bool? ParseAction(string action, bool current) => action?.Trim().ToUpperInvariant() switch
    {
        "ON" or "START" or "OPEN" => true,
        "OFF" or "STOP" or "CLOSE" => false,
        "TOGGLE" => !current,
        _ => null
    };

    /// <summary>Device downlink poll: hands over and removes all pending commands for this MAC.</summary>
    public List<OutboxCommand> DrainOutbox(string mac)
    {
        lock (_gate)
        {
            mac = NormalizeMac(mac);
            if (!_outbox.TryGetValue(mac, out var box) || box.Count == 0) return new();
            var list = new List<OutboxCommand>(box.Count);
            while (box.TryDequeue(out var cmd)) list.Add(cmd);
            return list;
        }
    }

    // =====================================================================================
    //  Search / filter
    // =====================================================================================

    /// <param name="query">Name, MAC or location fragment. An exact MAC is an O(1) dictionary hit.</param>
    /// <param name="category">Uses the category index instead of scanning every device.</param>
    /// <param name="state">all | critical | warning | normal | alerting | offline | online</param>
    /// <param name="logSearch">When true the search feeds the predictive engine.</param>
    public List<LiveDevice> Search(string? query, string? category, string? state, bool logSearch)
    {
        lock (_gate)
        {
            IEnumerable<LiveDevice> candidates;
            string q = query?.Trim() ?? string.Empty;

            if (q.Length > 0 && _devices.TryGetValue(NormalizeMac(q), out var exact))
                candidates = new[] { exact };                                       // O(1)
            else if (!string.IsNullOrWhiteSpace(category))
                candidates = _categoryIndex.TryGetValue(category, out var bucket)
                    ? bucket.Select(m => _devices[m])                               // O(k) for k devices in category
                    : Enumerable.Empty<LiveDevice>();
            else
                candidates = _devices.Values;

            if (!string.IsNullOrWhiteSpace(category))
                candidates = candidates.Where(d => d.Category.Equals(category, StringComparison.OrdinalIgnoreCase));

            if (q.Length > 0)
                candidates = candidates.Where(d =>
                    d.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    d.MacAddress.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    d.Location.Contains(q, StringComparison.OrdinalIgnoreCase));

            switch (state?.Trim().ToLowerInvariant())
            {
                case "critical": candidates = candidates.Where(d => d.Severity == AlertSeverity.Critical); break;
                case "warning": candidates = candidates.Where(d => d.Severity == AlertSeverity.Warning); break;
                case "normal": candidates = candidates.Where(d => d.Severity == AlertSeverity.Normal && d.IsOnline); break;
                case "offline": candidates = candidates.Where(d => _disconnectedNodes.Contains(d.MacAddress)); break;
                case "online": candidates = candidates.Where(d => !_disconnectedNodes.Contains(d.MacAddress)); break;
                case "alerting":
                    {
                        // Project the alert set to a set of MACs once, then each membership test is O(1).
                        var alerting = _activeAlerts.Select(a => a.MacAddress).ToHashSet(StringComparer.OrdinalIgnoreCase);
                        candidates = candidates.Where(d => alerting.Contains(d.MacAddress));
                        break;
                    }
            }

            var results = candidates
                .OrderByDescending(d => d.Severity)
                .ThenBy(d => d.Category)
                .ThenBy(d => d.DisplayName)
                .Select(d => d.Clone())
                .ToList();

            // A focused search (1–3 hits) is a strong signal of operator intent → teach the engine.
            if (logSearch && q.Length > 0 && results.Count is > 0 and <= 3)
                foreach (var d in results)
                    _patterns.ObserveAction(OperatorAction.Search(d.MacAddress), _clock());

            return results;
        }
    }

    public LiveDevice? GetDevice(string mac)
    {
        lock (_gate) return _devices.TryGetValue(NormalizeMac(mac), out var d) ? d.Clone() : null;
    }

    public List<LiveDevice> AllDevices()
    {
        lock (_gate) return _devices.Values.Select(d => d.Clone()).OrderBy(d => d.DisplayName).ToList();
    }

    /// <summary>Time-ordered readings for the chart. The SortedDictionary already iterates in key order.</summary>
    public List<TimelinePoint>? GetTimeline(string mac, TimeSpan window)
    {
        lock (_gate)
        {
            if (!_timelines.TryGetValue(NormalizeMac(mac), out var timeline)) return null;
            var from = _clock() - window;
            var points = new List<TimelinePoint>();
            foreach (var (at, value) in timeline)
                if (at >= from) points.Add(new TimelinePoint(at, value));
            return points;
        }
    }

    public List<AlertEntry> AlertHistory()
    {
        lock (_gate) return _alertHistory.Reverse().ToList();
    }

    // =====================================================================================
    //  Simulation helpers (for demos / README tests)
    // =====================================================================================

    /// <summary>
    /// Floods the standard queue with routine packets, THEN injects one critical packet.
    /// The processed log shows the critical packet handled first, proving the bypass.
    /// </summary>
    public BurstResult SimulateBurst(BurstRequest request)
    {
        List<LiveDevice> sensors;
        lock (_gate)
            sensors = _devices.Values.Where(d => !d.IsActuator && d.LastValue is not null).Select(d => d.Clone()).ToList();
        if (sensors.Count == 0) return new BurstResult(0, null, null, 0, 0);

        int count = Math.Clamp(request.StandardCount, 1, 5000);
        for (int i = 0; i < count; i++)
        {
            var s = sensors[i % sensors.Count];
            Enqueue(new StreamIngestRequest(s.MacAddress, s.LastValue!.Value), QueueLane.Standard);
        }

        Guid? criticalId = null;
        string? criticalName = null;
        if (request.IncludeCritical)
        {
            var target = (request.CriticalMac is { } m ? sensors.FirstOrDefault(s => s.MacAddress == NormalizeMac(m)) : null)
                         ?? sensors.FirstOrDefault(s => s.Thresholds?.CriticalLow is not null)
                         ?? sensors[0];
            var ack = InjectCritical(target.MacAddress);
            criticalId = ack?.PacketId;
            criticalName = target.DisplayName;
        }

        lock (_gate)
            return new BurstResult(count, criticalId, criticalName, _standardQueue.Count, _priorityQueue.Count);
    }

    /// <summary>Sends one reading beyond the device's critical limit through the normal ingest path.</summary>
    public IngestAck? InjectCritical(string mac)
    {
        LiveDevice? d = GetDevice(mac);
        if (d is null || d.IsActuator) return null;
        double value = d.Thresholds switch
        {
            { CriticalLow: { } low } => Math.Max(0, low - 4),
            { CriticalHigh: { } high } => high * 1.15,
            _ => (d.LastValue ?? 50) * 4 + 100
        };
        return Enqueue(new StreamIngestRequest(d.MacAddress, Math.Round(value, 1)));
    }

    // =====================================================================================
    //  Snapshot + recommendations
    // =====================================================================================

    public StreamStats GetStats()
    {
        lock (_gate) return BuildStatsLocked();
    }

    private StreamStats BuildStatsLocked() => new(
        _devices.Count,
        _devices.Count - _disconnectedNodes.Count(m => _devices.ContainsKey(m)) - _devices.Values.Count(d => d.LastSeenUtc is null),
        _standardQueue.Count, _priorityQueue.Count,
        _totalProcessed, _priorityProcessed,
        _standardLatencyCount == 0 ? 0 : Math.Round(_standardLatencySum / _standardLatencyCount, 1),
        _priorityProcessed == 0 ? 0 : Math.Round(_priorityLatencySum / _priorityProcessed, 1),
        _activeAlerts.Count, _activeAlerts.Count(a => a.Severity == AlertSeverity.Critical),
        _duplicateAlerts, _duplicateDisconnects,
        _disconnectedNodes.Count, _undoStack.Count, _redoStack.Count,
        _undoStack.TryPeek(out var u) ? u.Label : null,
        _redoStack.TryPeek(out var r) ? r.Label : null);

    public StreamSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            var now = _clock();
            return new StreamSnapshot(
                _devices.Values.Select(d => d.Clone())
                    .OrderByDescending(d => d.Severity).ThenBy(d => d.Category).ThenBy(d => d.DisplayName).ToList(),
                BuildStatsLocked(),
                _activeAlerts.OrderByDescending(a => a.Severity).ThenByDescending(a => a.RaisedUtc).ToList(),
                _disconnectedNodes.OrderBy(m => m).ToList(),
                _commandLog.Take(20).ToList(),
                _recentPriority.Reverse().Concat(_recentProcessed.Reverse()).DistinctBy(p => p.PacketId).Take(25).ToList(),
                BuildSuggestionsLocked(now),
                now);
        }
    }

    public List<SuggestedAction> GetSuggestions()
    {
        lock (_gate) return BuildSuggestionsLocked(_clock());
    }

    public List<LearnedPattern> GetLearnedPatterns()
    {
        lock (_gate)
        {
            return _patterns.AllPatterns()
                .Select(p => new LearnedPattern(p.Trigger, DescribeTrigger(p.Trigger), p.Action.ToString(),
                                                DescribeAction(p.Action), p.Count, p.Occurrences, Math.Round(p.Confidence, 2)))
                .ToList();
        }
    }

    /// <summary>
    /// Gathers candidate recommendations from four sources, de-duplicates them with a HashSet
    /// and ranks them with a max-heap (PriorityQueue with negated score) to return the top N.
    /// </summary>
    private List<SuggestedAction> BuildSuggestionsLocked(DateTime now, int top = 6)
    {
        var heap = new PriorityQueue<SuggestedAction, double>();
        var seen = new HashSet<string>();

        void Offer(SuggestedAction s)
        {
            if (seen.Add(s.Id)) heap.Enqueue(s, -s.Score);
        }

        // ---- 1. Learned trigger → action (active alerts) --------------------------------
        foreach (var alert in _activeAlerts)
        {
            string key = ActionPatternEngine.TriggerKey(alert.Code, alert.MacAddress);
            bool offeredLearned = false;
            foreach (var (action, count, occurrences, confidence) in _patterns.ActionsFor(key))
            {
                if (!_devices.TryGetValue(action.MacAddress, out var target)) continue;
                if (action.IsCommand && target.ActuatorOn == action.State) continue;   // already done

                double severityBonus = alert.Severity == AlertSeverity.Critical ? 25 : 10;
                Offer(action.IsCommand
                    ? new SuggestedAction($"cmd:{action}", "Suggested Action", "Learned from operator history",
                        $"Turn {(action.State == true ? "ON" : "OFF")} {target.DisplayName}",
                        $"{alert.DeviceName}: {FriendlyCode(alert.Code)}. In {count} of {occurrences} past occurrences operators " +
                        $"switched {target.DisplayName} {(action.State == true ? "on" : "off")} within {_patterns.CreditWindow.TotalMinutes:0} min.",
                        60 * confidence + severityBonus, confidence, target.MacAddress, action.State == true ? "ON" : "OFF")
                    : new SuggestedAction($"search:{action.MacAddress}", "Automated Insight", "Learned from operator history",
                        $"Inspect {target.DisplayName}",
                        $"When {alert.DeviceName} reports {FriendlyCode(alert.Code)}, operators checked {target.DisplayName} " +
                        $"{count} of {occurrences} times.",
                        40 * confidence + severityBonus, confidence, target.MacAddress, null));
                offeredLearned = true;
            }

            if (!offeredLearned)
            {
                Offer(new SuggestedAction($"investigate:{alert.MacAddress}:{alert.Code}", "Automated Insight", "Live alert",
                    alert.Code == "OFFLINE" ? $"Check node {alert.DeviceName}" : $"Investigate {alert.DeviceName}",
                    alert.Code == "OFFLINE"
                        ? $"{alert.Message}. Dropped {_disconnectCounts.GetValueOrDefault(alert.MacAddress)}× this session — check power and the nearest mesh hop."
                        : alert.Message,
                    alert.Severity == AlertSeverity.Critical ? 45 : 25, 1, alert.MacAddress, null));
            }
        }

        // ---- 2. Predictive: trend forecast BEFORE the threshold is crossed ---------------
        foreach (var device in _devices.Values)
        {
            if (device.IsActuator || !device.IsOnline) continue;
            var timeline = _timelines[device.MacAddress];
            if (timeline.Count < TrendForecaster.MinimumSamples) continue;

            var recent = timeline.Skip(Math.Max(0, timeline.Count - TrendForecaster.SampleSize)).ToList();
            if (now - recent[0].Key > TimeSpan.FromMinutes(5)) continue;     // stale seed data
            var forecast = TrendForecaster.Forecast(device, recent);
            if (forecast is null) continue;

            string key = ActionPatternEngine.TriggerKey(forecast.AlertCode, device.MacAddress);
            var learned = _patterns.ActionsFor(key).FirstOrDefault(a => a.Action.IsCommand &&
                              _devices.TryGetValue(a.Action.MacAddress, out var t) && t.ActuatorOn != a.Action.State);
            double urgency = Math.Max(0, TrendForecaster.HorizonMinutes - forecast.MinutesToBreach) * 4;
            string eta = forecast.MinutesToBreach < 1 ? "under a minute" : $"~{forecast.MinutesToBreach:F1} min";

            if (learned.Action.MacAddress is not null)
            {
                var target = _devices[learned.Action.MacAddress];
                Offer(new SuggestedAction($"cmd:{learned.Action}", "Suggested Action", "Predictive trend + learned response",
                    $"Pre-emptively turn {(learned.Action.State == true ? "ON" : "OFF")} {target.DisplayName}",
                    $"{device.DisplayName} is trending {(forecast.SlopePerMinute < 0 ? "down" : "up")} " +
                    $"{Math.Abs(forecast.SlopePerMinute):F2}{device.Unit}/min and will reach {forecast.Threshold}{device.Unit} in {eta}. " +
                    $"Operators responded this way {learned.Count} of {learned.Occurrences} times.",
                    50 + urgency + 20 * learned.Confidence, learned.Confidence, target.MacAddress,
                    learned.Action.State == true ? "ON" : "OFF"));
            }
            else
            {
                Offer(new SuggestedAction($"forecast:{device.MacAddress}", "Automated Insight", "Predictive trend",
                    $"{device.DisplayName} forecast to breach in {eta}",
                    $"Linear trend {forecast.SlopePerMinute:+0.00;-0.00}{device.Unit}/min from {forecast.CurrentValue:F1}{device.Unit} " +
                    $"towards the critical limit of {forecast.Threshold}{device.Unit}.",
                    35 + urgency, 0.7, device.MacAddress, null));
            }
        }

        // ---- 3. Markov chain: what operators usually do next -----------------------------
        foreach (var (prev, next, count, confidence) in _patterns.NextActions(now).Take(2))
        {
            if (!_devices.TryGetValue(next.MacAddress, out var target)) continue;
            if (next.IsCommand && target.ActuatorOn == next.State) continue;
            Offer(next.IsCommand
                ? new SuggestedAction($"cmd:{next}", "Suggested Action", "Next-step pattern",
                    $"Turn {(next.State == true ? "ON" : "OFF")} {target.DisplayName}",
                    $"After \"{DescribeAction(prev)}\" operators did this next {count} times ({confidence:P0}).",
                    30 * confidence + 15, confidence, target.MacAddress, next.State == true ? "ON" : "OFF")
                : new SuggestedAction($"search:{next.MacAddress}", "Automated Insight", "Next-step pattern",
                    $"You may want {target.DisplayName} next",
                    $"After \"{DescribeAction(prev)}\" operators opened {target.DisplayName} {count} times ({confidence:P0}).",
                    20 * confidence + 10, confidence, target.MacAddress, null));
        }

        // ---- 4. Unstable (flapping) nodes ------------------------------------------------
        foreach (var (mac, drops) in _disconnectCounts)
        {
            if (drops < 3 || _disconnectedNodes.Contains(mac) || !_devices.TryGetValue(mac, out var d)) continue;
            Offer(new SuggestedAction($"flap:{mac}", "Automated Insight", "Connectivity pattern",
                $"{d.DisplayName} is unstable",
                $"Disconnected {drops} times this session — likely weak mesh link or brown-out. Consider relocating or adding a repeater.",
                15 + drops * 3, 0.6, mac, null));
        }

        var result = new List<SuggestedAction>(top);
        while (result.Count < top && heap.TryDequeue(out var s, out _))
            result.Add(s with { Score = Math.Round(s.Score, 1), Confidence = Math.Round(s.Confidence, 2) });
        return result;
    }

    private string DescribeTrigger(string triggerKey)
    {
        int at = triggerKey.IndexOf('@');
        if (at < 0) return triggerKey;
        string code = triggerKey[..at], mac = triggerKey[(at + 1)..];
        string name = _devices.TryGetValue(mac, out var d) ? d.DisplayName : mac;
        return $"{name}: {FriendlyCode(code)}";
    }

    private string DescribeAction(OperatorAction action)
    {
        string name = _devices.TryGetValue(action.MacAddress, out var d) ? d.DisplayName : action.MacAddress;
        return action.IsCommand ? $"Turn {(action.State == true ? "ON" : "OFF")} {name}" : $"Search {name}";
    }

    private static string FriendlyCode(string code) => code switch
    {
        "OFFLINE" => "node offline",
        "ANOMALY_SPIKE" => "anomalous spike",
        _ => code.Replace('_', ' ').ToLowerInvariant()
    };
}

/// <summary>Two alerts are "the same" when they share device + code, regardless of message or time.</summary>
public sealed class AlertIdentityComparer : IEqualityComparer<AlertEntry>
{
    public static readonly AlertIdentityComparer Instance = new();

    public bool Equals(AlertEntry? x, AlertEntry? y) =>
        ReferenceEquals(x, y) ||
        (x is not null && y is not null &&
         string.Equals(x.MacAddress, y.MacAddress, StringComparison.OrdinalIgnoreCase) &&
         string.Equals(x.Code, y.Code, StringComparison.Ordinal));

    public int GetHashCode(AlertEntry obj) =>
        HashCode.Combine(obj.MacAddress.ToUpperInvariant(), obj.Code);
}