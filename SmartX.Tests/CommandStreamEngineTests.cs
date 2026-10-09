using SmartX.Api.Services;
using SmartX.Shared;

namespace SmartX.Tests;

/// <summary>
/// Proves each Part 2 data structure does its job. The engine takes an injectable clock,
/// so time-based behaviour (latency, heartbeats, learning windows) is deterministic.
/// </summary>
public class CommandStreamEngineTests
{
    private DateTime _now = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
    private const string Moisture = "24:6F:28:00:00:01";
    private const string Moisture2 = "24:6F:28:00:00:02";
    private const string Pump = "24:6F:28:00:00:10";
    private const string Valve1 = "24:6F:28:00:00:11";
    private const string Valve2 = "24:6F:28:00:00:12";

    private CommandStreamEngine NewEngine()
    {
        var engine = new CommandStreamEngine(new ActionPatternEngine(), () => _now);
        engine.RegisterDevice(Sensor(Moisture, "Moisture 1"));
        engine.RegisterDevice(Sensor(Moisture2, "Moisture 2"));
        engine.RegisterDevice(Actuator(Pump, "Pump A"));
        engine.RegisterDevice(Actuator(Valve1, "Valve Line 1"));
        engine.RegisterDevice(Actuator(Valve2, "Valve Line 2"));
        return engine;
    }

    private static LiveDevice Sensor(string mac, string name) => new()
    {
        MacAddress = mac, DisplayName = name, Category = DeviceCategories.Environmental,
        Metric = "moisture", Unit = "%", Location = "Facility A/Zone 1", Thresholds = new(15, 25, 85, 95)
    };

    private static LiveDevice Actuator(string mac, string name) => new()
    {
        MacAddress = mac, DisplayName = name, Category = DeviceCategories.Actuator, Metric = "state", Location = "Pump House"
    };

    // ---------------------------------------------------------------- Queue / PriorityQueue

    [Fact]
    public void Critical_packet_bypasses_a_backlog_in_the_standard_queue()
    {
        var engine = NewEngine();
        for (int i = 0; i < 50; i++)
            Assert.Equal(QueueLane.Standard, engine.Enqueue(new StreamIngestRequest(Moisture, 50))!.Lane);

        var critical = engine.Enqueue(new StreamIngestRequest(Moisture2, 5))!;
        Assert.Equal(QueueLane.Priority, critical.Lane);
        Assert.Equal(50, critical.StandardQueueDepth);

        engine.ProcessBatch(1);   // only ONE packet processed...

        var processed = engine.GetSnapshot().RecentProcessed.Single();
        Assert.Equal(critical.PacketId, processed.PacketId);          // ...and it is the critical one
        Assert.Equal(QueueLane.Priority, processed.Lane);
        Assert.Equal(50, engine.GetStats().StandardQueueDepth);       // backlog still waiting
    }

    [Fact]
    public void Standard_queue_is_first_in_first_out()
    {
        var engine = NewEngine();
        var first = engine.Enqueue(new StreamIngestRequest(Moisture, 50))!;
        var second = engine.Enqueue(new StreamIngestRequest(Moisture, 51))!;
        engine.ProcessBatch(10);

        var log = engine.GetSnapshot().RecentProcessed;      // newest first
        Assert.Equal(second.PacketId, log[0].PacketId);
        Assert.Equal(first.PacketId, log[1].PacketId);
    }

    [Fact]
    public void Priority_queue_keeps_arrival_order_for_equal_severity()
    {
        var engine = NewEngine();
        var a = engine.Enqueue(new StreamIngestRequest(Moisture, 4))!;
        var b = engine.Enqueue(new StreamIngestRequest(Moisture2, 3))!;
        engine.ProcessBatch(1);
        Assert.Equal(a.PacketId, engine.GetSnapshot().RecentProcessed[0].PacketId);
        engine.ProcessBatch(1);
        Assert.Equal(b.PacketId, engine.GetSnapshot().RecentProcessed[0].PacketId);
    }

    [Fact]
    public void Unknown_device_is_rejected()
    {
        var engine = NewEngine();
        Assert.Null(engine.Enqueue(new StreamIngestRequest("FF:FF:FF:FF", 1)));
    }

    // ---------------------------------------------------------------- Stack (undo / redo)

    [Fact]
    public void Undo_reverts_commands_in_last_in_first_out_order()
    {
        var engine = NewEngine();
        Assert.True(engine.IssueCommand(new DeviceCommandRequest(Pump, "ON")).Success);
        Assert.True(engine.IssueCommand(new DeviceCommandRequest(Valve1, "ON")).Success);

        engine.Undo();                                            // pops the valve command
        Assert.False(engine.GetDevice(Valve1)!.ActuatorOn);
        Assert.True(engine.GetDevice(Pump)!.ActuatorOn);

        engine.Undo();                                            // then the pump command
        Assert.False(engine.GetDevice(Pump)!.ActuatorOn);
        Assert.False(engine.Undo().Success);                      // empty stack is handled
    }

    [Fact]
    public void Bulk_command_is_one_stack_entry_and_one_undo_reverts_every_device()
    {
        var engine = NewEngine();
        engine.IssueCommand(new DeviceCommandRequest(Valve1, "ON"));
        engine.IssueCommand(new DeviceCommandRequest(Valve2, "ON"));

        var bulk = engine.IssueBulkCommand(new BulkCommandRequest("OFF", "Valve", "Shut down all valves"));
        Assert.True(bulk.Success);
        Assert.Equal(2, bulk.Record!.Changes.Count);
        Assert.Equal(3, engine.GetStats().UndoDepth);

        engine.Undo();
        Assert.True(engine.GetDevice(Valve1)!.ActuatorOn);
        Assert.True(engine.GetDevice(Valve2)!.ActuatorOn);
    }

    [Fact]
    public void Redo_reapplies_and_a_new_command_clears_the_redo_stack()
    {
        var engine = NewEngine();
        engine.IssueCommand(new DeviceCommandRequest(Pump, "ON"));
        engine.Undo();
        Assert.Equal(1, engine.GetStats().RedoDepth);

        engine.Redo();
        Assert.True(engine.GetDevice(Pump)!.ActuatorOn);

        engine.Undo();
        engine.IssueCommand(new DeviceCommandRequest(Valve1, "ON"));
        Assert.Equal(0, engine.GetStats().RedoDepth);
    }

    [Fact]
    public void Sensors_cannot_be_switched_and_no_op_commands_are_not_stacked()
    {
        var engine = NewEngine();
        Assert.False(engine.IssueCommand(new DeviceCommandRequest(Moisture, "ON")).Success);
        Assert.False(engine.IssueCommand(new DeviceCommandRequest(Pump, "OFF")).Success);   // already off
        Assert.Equal(0, engine.GetStats().UndoDepth);
    }

    [Fact]
    public void Commands_reach_the_device_outbox_in_fifo_order()
    {
        var engine = NewEngine();
        engine.IssueCommand(new DeviceCommandRequest(Pump, "ON"));
        engine.Undo();
        var outbox = engine.DrainOutbox(Pump);
        Assert.Equal(new[] { true, false }, outbox.Select(c => c.TargetState));
        Assert.Empty(engine.DrainOutbox(Pump));                   // drained exactly once
    }

    // ---------------------------------------------------------------- Dictionary / SortedDictionary

    [Fact]
    public void Device_lookup_by_mac_is_case_insensitive()
    {
        var engine = NewEngine();
        Assert.Equal("Pump A", engine.GetDevice(Pump.ToLowerInvariant())!.DisplayName);
    }

    [Fact]
    public void Search_uses_category_index_text_and_state_filters()
    {
        var engine = NewEngine();
        Assert.Equal(3, engine.Search(null, DeviceCategories.Actuator, null, false).Count);
        Assert.Equal(2, engine.Search("valve", DeviceCategories.Actuator, null, false).Count);
        Assert.Single(engine.Search(Pump, null, null, false));     // exact MAC → direct hit

        engine.Enqueue(new StreamIngestRequest(Moisture, 5));
        engine.ProcessBatch(10);
        var critical = engine.Search(null, null, "critical", false);
        Assert.Equal(Moisture, Assert.Single(critical).MacAddress);
    }

    [Fact]
    public void Timeline_is_time_ordered_and_bounded()
    {
        var engine = NewEngine();
        var shuffled = Enumerable.Range(0, 800)
            .Select(i => (_now.AddSeconds(-i), (double)i))
            .OrderBy(_ => Guid.NewGuid())
            .ToList();
        engine.SeedHistory(Moisture, shuffled);

        var timeline = engine.GetTimeline(Moisture, TimeSpan.FromHours(1))!;
        Assert.Equal(CommandStreamEngine.TimelineCapacity, timeline.Count);
        Assert.True(timeline.Zip(timeline.Skip(1)).All(p => p.First.TimestampUtc < p.Second.TimestampUtc));
        Assert.Equal(_now, timeline[^1].TimestampUtc);            // newest kept, oldest evicted
    }

    // ---------------------------------------------------------------- HashSet

    [Fact]
    public void Repeated_alert_for_the_same_device_is_suppressed_by_the_set()
    {
        var engine = NewEngine();
        engine.Enqueue(new StreamIngestRequest(Moisture, 5));
        engine.Enqueue(new StreamIngestRequest(Moisture, 4));
        engine.Enqueue(new StreamIngestRequest(Moisture, 3));
        engine.ProcessBatch(10);

        var stats = engine.GetStats();
        Assert.Equal(1, stats.ActiveAlertCount);
        Assert.Equal(2, stats.DuplicateAlertsSuppressed);
    }

    [Fact]
    public void Alert_clears_when_the_reading_returns_to_normal()
    {
        var engine = NewEngine();
        engine.Enqueue(new StreamIngestRequest(Moisture, 5));
        engine.ProcessBatch(10);
        engine.Enqueue(new StreamIngestRequest(Moisture, 50));
        engine.ProcessBatch(10);
        Assert.Equal(0, engine.GetStats().ActiveAlertCount);
    }

    [Fact]
    public void Watchdog_raises_exactly_one_disconnect_per_node_and_reconnect_clears_it()
    {
        var engine = NewEngine();
        engine.Enqueue(new StreamIngestRequest(Moisture, 50));
        engine.ProcessBatch(10);

        _now = _now.AddSeconds(30);
        Assert.Equal(1, engine.CheckHeartbeats(TimeSpan.FromSeconds(8)));
        Assert.Equal(0, engine.CheckHeartbeats(TimeSpan.FromSeconds(8)));   // already in the set
        var stats = engine.GetStats();
        Assert.Equal(1, stats.DisconnectedCount);
        Assert.Equal(1, stats.DuplicateDisconnectsSuppressed);

        engine.Enqueue(new StreamIngestRequest(Moisture, 50));
        engine.ProcessBatch(10);
        Assert.Equal(0, engine.GetStats().DisconnectedCount);
        Assert.True(engine.GetDevice(Moisture)!.IsOnline);
    }

    // ---------------------------------------------------------------- Predictive engine

    [Fact]
    public void Engine_learns_that_operators_start_the_pump_after_low_moisture()
    {
        var engine = NewEngine();
        for (int i = 0; i < 3; i++)
        {
            engine.Enqueue(new StreamIngestRequest(Moisture, 10));        // trigger: MOISTURE_LOW
            engine.ProcessBatch(10);
            _now = _now.AddSeconds(45);
            engine.IssueCommand(new DeviceCommandRequest(Pump, "ON"));     // operator response
            engine.Enqueue(new StreamIngestRequest(Moisture, 50));         // recovers
            engine.ProcessBatch(10);
            _now = _now.AddSeconds(30);
            engine.IssueCommand(new DeviceCommandRequest(Pump, "OFF"));
            _now = _now.AddMinutes(20);
        }

        engine.Enqueue(new StreamIngestRequest(Moisture, 10));            // happens again
        engine.ProcessBatch(10);

        var suggestion = engine.GetSuggestions().First(s => s.TargetMac == Pump);
        Assert.Equal("ON", suggestion.CommandAction);
        Assert.Equal("Suggested Action", suggestion.Kind);
        Assert.True(suggestion.Confidence >= 0.75);
    }

    [Fact]
    public void Suggestion_disappears_once_the_action_has_been_taken()
    {
        var engine = new CommandStreamEngine(new ActionPatternEngine(), () => _now);
        FleetSeeder.Seed(engine, _now);
        engine.Enqueue(new StreamIngestRequest(FleetSeeder.MoistureB, 12));
        engine.ProcessBatch(50);

        Assert.Contains(engine.GetSuggestions(), s => s.TargetMac == FleetSeeder.PumpA && s.CommandAction == "ON");

        engine.IssueCommand(new DeviceCommandRequest(FleetSeeder.PumpA, "ON"));
        Assert.DoesNotContain(engine.GetSuggestions(), s => s.TargetMac == FleetSeeder.PumpA && s.CommandAction == "ON");
    }

    [Fact]
    public void Trend_forecaster_predicts_time_to_breach()
    {
        var device = Sensor(Moisture, "Moisture 1");
        // Falling 2 %/min from 30 %: should reach 15 % in ~7.5 minutes.
        var points = Enumerable.Range(0, 20)
            .Select(i => new KeyValuePair<DateTime, double>(_now.AddSeconds(i * 6), 30 - 2 * (i * 6) / 60.0 + 0.2))
            .ToList();
        var forecast = TrendForecaster.Forecast(device, points);
        Assert.NotNull(forecast);
        Assert.Equal("MOISTURE_LOW", forecast!.AlertCode);
        Assert.InRange(forecast.MinutesToBreach, 5, 8);
    }

    [Fact]
    public void Stable_sensor_produces_no_forecast()
    {
        var device = Sensor(Moisture, "Moisture 1");
        var points = Enumerable.Range(0, 20)
            .Select(i => new KeyValuePair<DateTime, double>(_now.AddSeconds(i * 6), 50))
            .ToList();
        Assert.Null(TrendForecaster.Forecast(device, points));
    }

    [Fact]
    public void Injected_critical_reading_uses_the_priority_lane()
    {
        var engine = NewEngine();
        var ack = engine.InjectCritical(Moisture);

        Assert.NotNull(ack);
        Assert.Equal(QueueLane.Priority, ack!.Lane);
        Assert.Equal(AlertSeverity.Critical, ack.Severity);
        Assert.Null(engine.InjectCritical(Pump));   // actuators have no reading to inject
    }

    [Fact]
    public void Seeded_operator_history_appears_as_learned_patterns()
    {
        var engine = new CommandStreamEngine(new ActionPatternEngine(), () => _now);
        FleetSeeder.Seed(engine, _now);

        var rule = engine.GetLearnedPatterns()
            .First(p => p.TriggerLabel == "Moisture Sensor B: moisture low" && p.ActionLabel == "Turn ON Pump A");

        Assert.Equal(5, rule.Count);
        Assert.Equal(6, rule.TriggerOccurrences);
    }

    [Fact]
    public void Redo_with_nothing_to_redo_is_handled()
    {
        var engine = NewEngine();
        Assert.False(engine.Redo().Success);
    }

    [Fact]
    public void Bulk_command_with_nothing_to_change_is_rejected()
    {
        var engine = NewEngine();   // all valves start OFF
        var result = engine.IssueBulkCommand(new BulkCommandRequest("OFF", "Valve", "Shut down all valves"));

        Assert.False(result.Success);
        Assert.Equal(0, engine.GetStats().UndoDepth);   // nothing pushed onto the stack
    }

    [Fact]
    public void Toggle_flips_the_actuator_state()
    {
        var engine = NewEngine();

        engine.IssueCommand(new DeviceCommandRequest(Pump, "TOGGLE"));
        Assert.True(engine.GetDevice(Pump)!.ActuatorOn);

        engine.IssueCommand(new DeviceCommandRequest(Pump, "TOGGLE"));
        Assert.False(engine.GetDevice(Pump)!.ActuatorOn);
    }
}
