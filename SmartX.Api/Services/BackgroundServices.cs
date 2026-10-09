using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace SmartX.Api.Services;

/// <summary>Tunable gateway limits (appsettings.json → "Gateway").</summary>
public sealed class GatewayOptions
{
    /// <summary>How often the processor wakes up.</summary>
    public int TickMilliseconds { get; set; } = 100;
    /// <summary>Packets processed per tick — models the throughput budget of a constrained gateway.</summary>
    public int PacketsPerTick { get; set; } = 25;
    /// <summary>A node silent for longer than this is marked disconnected.</summary>
    public int HeartbeatTimeoutSeconds { get; set; } = 8;
    /// <summary>How often a snapshot is pushed to dashboards over SignalR.</summary>
    public int BroadcastMilliseconds { get; set; } = 1000;
}

/// <summary>SignalR hub. Clients only listen ("snapshot" messages); commands go through REST.</summary>
public sealed class TelemetryHub : Hub
{
    private readonly CommandStreamEngine _engine;
    public TelemetryHub(CommandStreamEngine engine) => _engine = engine;

    /// <summary>Sends the current state to a client the moment it connects (no waiting for the next tick).</summary>
    public override async Task OnConnectedAsync()
    {
        await Clients.Caller.SendAsync("snapshot", _engine.GetSnapshot());
        await base.OnConnectedAsync();
    }
}

/// <summary>
/// Drains the ingestion queues on a fixed tick (priority lane first) and runs the
/// heartbeat watchdog once per second. Runs off the request thread so the ingest
/// endpoint only does an O(1) enqueue and returns 202 immediately.
/// </summary>
public sealed class StreamProcessorService : BackgroundService
{
    private readonly CommandStreamEngine _engine;
    private readonly GatewayOptions _options;
    private readonly ILogger<StreamProcessorService> _logger;

    public StreamProcessorService(CommandStreamEngine engine, IOptions<GatewayOptions> options, ILogger<StreamProcessorService> logger)
    {
        _engine = engine;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(Math.Max(10, _options.TickMilliseconds)));
        var timeout = TimeSpan.FromSeconds(_options.HeartbeatTimeoutSeconds);
        var lastWatchdog = DateTime.UtcNow;

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    _engine.ProcessBatch(_options.PacketsPerTick);

                    if (DateTime.UtcNow - lastWatchdog >= TimeSpan.FromSeconds(1))
                    {
                        lastWatchdog = DateTime.UtcNow;
                        int dropped = _engine.CheckHeartbeats(timeout);
                        if (dropped > 0) _logger.LogWarning("{Count} node(s) stopped sending heartbeats", dropped);
                    }
                }
                catch (Exception ex)
                {
                    // A bad packet must never kill the gateway loop.
                    _logger.LogError(ex, "Stream processing tick failed");
                }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }
}

/// <summary>Pushes a dashboard snapshot to every connected client on a fixed cadence.</summary>
public sealed class SnapshotBroadcaster : BackgroundService
{
    private readonly CommandStreamEngine _engine;
    private readonly IHubContext<TelemetryHub> _hub;
    private readonly GatewayOptions _options;
    private readonly ILogger<SnapshotBroadcaster> _logger;

    public SnapshotBroadcaster(CommandStreamEngine engine, IHubContext<TelemetryHub> hub,
                               IOptions<GatewayOptions> options, ILogger<SnapshotBroadcaster> logger)
    {
        _engine = engine;
        _hub = hub;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(Math.Max(250, _options.BroadcastMilliseconds)));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await _hub.Clients.All.SendAsync("snapshot", _engine.GetSnapshot(), stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Snapshot broadcast failed");
                }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }
}
