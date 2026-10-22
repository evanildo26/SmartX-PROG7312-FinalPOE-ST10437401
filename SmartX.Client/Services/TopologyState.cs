using System.Net.Http.Json;
using SmartX.Shared;

namespace SmartX.Client.Services;

/// <summary>
/// Session-wide state for the Network Topology &amp; Mesh Routing pillar. The live mesh arrives through
/// <see cref="StreamStateService"/> (one SignalR connection for the whole app); this service keeps the
/// operator's choices — selected node, tracked route, traversal, MST algorithm, history range — so they
/// survive navigation to the other pillars and back, and calls the topology REST API.
/// </summary>
public sealed class TopologyState : IDisposable
{
    private readonly HttpClient _http;
    private readonly StreamStateService _stream;
    private CancellationTokenSource? _animation;
    private bool _started;
    private bool _refreshingRoute;
    private double _lastMstCost = double.NaN;

    public TopologyState(HttpClient http, StreamStateService stream)
    {
        _http = http;
        _stream = stream;
        _stream.TopologyChanged += OnTopologyChanged;
    }

    public TopologySnapshot? Snapshot => _stream.Topology;
    public event Action? Changed;
    private void Notify() => Changed?.Invoke();

    // ---- UI state preserved across page navigation -------------------------------------
    /// <summary>links | mst | traversal | route — what the map highlights.</summary>
    public string View { get; private set; } = "mst";
    public string? SelectedMac { get; private set; }
    public string RouteInput { get; set; } = string.Empty;
    public RouteResult? Route { get; private set; }

    public TraversalResult? Traversal { get; private set; }
    public int TraversalVisible { get; private set; }
    public bool TraversalAnimating { get; private set; }
    public string TraversalZone { get; set; } = string.Empty;

    public string MstAlgorithm { get; private set; } = "prim";
    public MstResult? Mst { get; private set; }

    public List<HistoryIndexInfo>? HistoryIndex { get; private set; }
    public string HistoryMac { get; set; } = string.Empty;
    /// <summary>Local (browser) time, bound to the datetime-local inputs.</summary>
    public DateTime HistoryFrom { get; set; }
    public DateTime HistoryTo { get; set; }
    public HistoryQueryResult? History { get; private set; }
    public bool HistoryBusy { get; private set; }

    public ScaleBenchmarkResult? Benchmark { get; private set; }
    public bool BenchmarkBusy { get; private set; }

    public List<string> ActivityLog { get; } = new();

    public async Task EnsureStartedAsync()
    {
        await _stream.EnsureStartedAsync();
        if (_started) return;
        _started = true;

        if (Snapshot is null) await _stream.RefreshTopologyAsync();
        if (HistoryTo == default)
        {
            HistoryTo = DateTime.Now;
            HistoryFrom = HistoryTo.AddHours(-2);
        }
        await LoadMstAsync(MstAlgorithm, switchView: false);
        await LoadHistoryIndexAsync();
        // Default to Moisture Sensor A — the brief's own example ("moisture readings … for Node A").
        if (HistoryMac.Length == 0 &&
            (HistoryIndex?.FirstOrDefault(i => i.DisplayName == "Moisture Sensor A") ?? HistoryIndex?.FirstOrDefault(i => i.Unit == "%")) is { } first)
        {
            HistoryMac = first.MacAddress;
            await QueryHistoryAsync();
        }
        Notify();
    }

    // =====================================================================================
    //  Live updates
    // =====================================================================================

    private void OnTopologyChanged()
    {
        Notify();
        if (Snapshot is not { } snap) return;

        // Keep the tracked route and the MST table in step with the healing mesh.
        if (SelectedMac is not null && !_refreshingRoute) _ = RefreshRouteAsync();
        double cost = MstAlgorithm == "kruskal" ? snap.Summary.KruskalCost : snap.Summary.PrimCost;
        if (!double.IsNaN(_lastMstCost) && Math.Abs(cost - _lastMstCost) > 0.001) _ = LoadMstAsync(MstAlgorithm, switchView: false);
        _lastMstCost = cost;
    }

    private async Task RefreshRouteAsync()
    {
        _refreshingRoute = true;
        try { await LoadRouteAsync(); }
        finally { _refreshingRoute = false; }
        Notify();
    }

    public void SetView(string view)
    {
        View = view;
        Notify();
    }

    public MeshNodeDto? Node(string? mac) =>
        mac is null ? null : Snapshot?.Nodes.FirstOrDefault(n => n.MacAddress.Equals(mac, StringComparison.OrdinalIgnoreCase));

    public string NameOf(string? mac) => Node(mac)?.DisplayName ?? mac ?? "—";

    // =====================================================================================
    //  Route tracking
    // =====================================================================================

    /// <summary>Tracks the route of whatever was typed: a MAC address, or (part of) a device name.</summary>
    public async Task TrackRouteAsync()
    {
        string query = RouteInput.Trim();
        if (query.Length == 0 || Snapshot is null) return;
        var match = Snapshot.Nodes.FirstOrDefault(n => n.MacAddress.Equals(query, StringComparison.OrdinalIgnoreCase))
                    ?? Snapshot.Nodes.FirstOrDefault(n => n.DisplayName.Equals(query, StringComparison.OrdinalIgnoreCase))
                    ?? Snapshot.Nodes.FirstOrDefault(n => n.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            Log($"✖ No node matches “{query}”. Use a MAC like 24:6F:28:A1:00:02 or a name like Moisture Sensor B.");
            return;
        }
        await SelectNodeAsync(match.MacAddress);
    }

    public async Task SelectNodeAsync(string mac)
    {
        SelectedMac = mac;
        RouteInput = mac;
        View = "route";
        Notify();
        await LoadRouteAsync();
        if (HistoryIndex?.Any(i => i.MacAddress.Equals(mac, StringComparison.OrdinalIgnoreCase)) == true &&
            !HistoryMac.Equals(mac, StringComparison.OrdinalIgnoreCase))
        {
            HistoryMac = mac;
            await QueryHistoryAsync();
        }
        Notify();
    }

    public void ClearSelection()
    {
        SelectedMac = null;
        Route = null;
        if (View == "route") View = "mst";
        Notify();
    }

    private async Task LoadRouteAsync()
    {
        if (SelectedMac is null) return;
        try { Route = await _http.GetFromJsonAsync<RouteResult>($"/api/topology/route/{Uri.EscapeDataString(SelectedMac)}"); }
        catch { /* keep the last route if the API blips */ }
    }

    // =====================================================================================
    //  BFS / DFS
    // =====================================================================================

    public async Task RunTraversalAsync(string algorithm, string? startMac = null)
    {
        _animation?.Cancel();
        var cts = _animation = new CancellationTokenSource();
        try
        {
            string url = $"/api/topology/{algorithm}?start={Uri.EscapeDataString(startMac ?? string.Empty)}" +
                         $"&zone={Uri.EscapeDataString(TraversalZone)}";
            Traversal = await _http.GetFromJsonAsync<TraversalResult>(url);
        }
        catch (Exception ex)
        {
            Log($"✖ Traversal failed: {ex.Message}");
            return;
        }
        if (Traversal is null) return;

        View = "traversal";
        TraversalVisible = 0;
        TraversalAnimating = true;
        Log($"{Traversal.Algorithm} from {Traversal.StartName}{(TraversalZone.Length > 0 ? $" (subnet {TraversalZone})" : "")}: " +
            $"{Traversal.VisitedCount} reachable, {Traversal.Down.Count} down/unreachable.");

        // Reveal the visit order one node at a time so the Queue (BFS) or Stack (DFS) order is visible.
        try
        {
            while (TraversalVisible < Traversal.Order.Count)
            {
                TraversalVisible++;
                Notify();
                await Task.Delay(380, cts.Token);
            }
        }
        catch (OperationCanceledException) { return; }
        TraversalAnimating = false;
        Notify();
    }

    public void ShowWholeTraversal()
    {
        _animation?.Cancel();
        if (Traversal is not null) TraversalVisible = Traversal.Order.Count;
        TraversalAnimating = false;
        Notify();
    }

    // =====================================================================================
    //  MST
    // =====================================================================================

    public async Task LoadMstAsync(string algorithm, bool switchView = true)
    {
        MstAlgorithm = algorithm;
        if (switchView) View = "mst";
        try { Mst = await _http.GetFromJsonAsync<MstResult>($"/api/topology/mst?algorithm={algorithm}"); }
        catch (Exception ex) { Log($"✖ MST failed: {ex.Message}"); }
        Notify();
    }

    // =====================================================================================
    //  Simulated failures
    // =====================================================================================

    public Task ToggleLinkAsync(string a, string b) =>
        PostActionAsync("/api/topology/links/toggle", new LinkToggleRequest(a, b));

    public Task ToggleNodeAsync(string mac) =>
        PostActionAsync($"/api/topology/nodes/{Uri.EscapeDataString(mac)}/toggle", null);

    public Task ResetAsync() => PostActionAsync("/api/topology/reset", null);

    private async Task PostActionAsync(string url, object? body)
    {
        TopologyActionResult? result;
        try
        {
            using var response = body is null ? await _http.PostAsync(url, null) : await _http.PostAsJsonAsync(url, body);
            result = await response.Content.ReadFromJsonAsync<TopologyActionResult>();
        }
        catch (Exception ex)
        {
            result = new TopologyActionResult(false, $"Gateway unreachable: {ex.Message}");
        }
        Log(result is null ? "✖ No response from the gateway." : (result.Success ? "✔ " : "✖ ") + result.Message);

        await _stream.RefreshTopologyAsync();          // redraw at once instead of waiting for the next push
        await LoadMstAsync(MstAlgorithm, switchView: false);
        if (SelectedMac is not null) await LoadRouteAsync();
        if (Traversal is not null) await RunTraversalAsync(Traversal.Algorithm.ToLowerInvariant(), Traversal.StartMac);
        Notify();
    }

    // =====================================================================================
    //  Historical telemetry (AVL range queries)
    // =====================================================================================

    public async Task LoadHistoryIndexAsync()
    {
        try { HistoryIndex = await _http.GetFromJsonAsync<List<HistoryIndexInfo>>("/api/topology/history"); }
        catch { HistoryIndex ??= new(); }
    }

    public async Task SetHistoryPresetAsync(string preset)
    {
        var now = DateTime.Now;
        (HistoryFrom, HistoryTo) = preset switch
        {
            "1h" => (now.AddHours(-1), now),
            "6h" => (now.AddHours(-6), now),
            "24h" => (now.AddDays(-1), now),
            "7d" => (now.AddDays(-7), now),
            "yesterday" => (now.Date.AddDays(-1).AddHours(14), now.Date.AddDays(-1).AddHours(16)),
            _ => (now.AddHours(-2), now)
        };
        await QueryHistoryAsync();
    }

    public async Task QueryHistoryAsync()
    {
        if (HistoryMac.Length == 0) return;
        HistoryBusy = true;
        Notify();
        try
        {
            long fromMs = new DateTimeOffset(DateTime.SpecifyKind(HistoryFrom, DateTimeKind.Local)).ToUnixTimeMilliseconds();
            long toMs = new DateTimeOffset(DateTime.SpecifyKind(HistoryTo, DateTimeKind.Local)).ToUnixTimeMilliseconds();
            History = await _http.GetFromJsonAsync<HistoryQueryResult>(
                $"/api/topology/history/{Uri.EscapeDataString(HistoryMac)}?fromMs={fromMs}&toMs={toMs}&maxPoints=600");
        }
        catch (Exception ex)
        {
            Log($"✖ History query failed: {ex.Message}");
        }
        HistoryBusy = false;
        Notify();
    }

    // =====================================================================================
    //  Scale benchmark
    // =====================================================================================

    public async Task RunBenchmarkAsync(int nodes = 2000)
    {
        BenchmarkBusy = true;
        Notify();
        try { Benchmark = await _http.GetFromJsonAsync<ScaleBenchmarkResult>($"/api/topology/benchmark?nodes={nodes}"); }
        catch (Exception ex) { Log($"✖ Benchmark failed: {ex.Message}"); }
        BenchmarkBusy = false;
        Notify();
    }

    private void Log(string message)
    {
        ActivityLog.Insert(0, $"{DateTime.Now:HH:mm:ss}  {message}");
        if (ActivityLog.Count > 10) ActivityLog.RemoveAt(ActivityLog.Count - 1);
        Notify();
    }

    public void Dispose()
    {
        _animation?.Cancel();
        _stream.TopologyChanged -= OnTopologyChanged;
    }
}
