using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace SmartX.Api.Services.Topology;

/// <summary>
/// Once per broadcast tick: syncs the mesh with the live device registry (new devices, nodes that
/// went offline, new readings for the AVL history) and pushes a topology snapshot to every dashboard
/// over the same SignalR hub as Part 2, under the message name "topology".
/// </summary>
public sealed class TopologyBroadcaster : BackgroundService
{
    private readonly MeshTopologyEngine _topology;
    private readonly IHubContext<TelemetryHub> _hub;
    private readonly GatewayOptions _options;
    private readonly ILogger<TopologyBroadcaster> _logger;

    public TopologyBroadcaster(MeshTopologyEngine topology, IHubContext<TelemetryHub> hub,
                               IOptions<GatewayOptions> options, ILogger<TopologyBroadcaster> logger)
    {
        _topology = topology;
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
                    _topology.Sync();
                    await _hub.Clients.All.SendAsync("topology", _topology.GetSnapshot(), stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A failed tick must never stop the mesh from being monitored.
                    _logger.LogError(ex, "Topology sync/broadcast failed");
                }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }
}
