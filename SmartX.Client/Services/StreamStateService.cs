using System.Net.Http.Json;
using Microsoft.AspNetCore.SignalR.Client;
using SmartX.Shared;

namespace SmartX.Client.Services;

public enum LinkState { Connecting, Live, Reconnecting, Polling, Offline }

public record UiToast(Guid Id, AlertSeverity Severity, string Title, string Message, string MacAddress, DateTime CreatedUtc);

/// <summary>
/// Application-wide state for the real-time pillars. Registered once per browser session,
/// so the SignalR connection, the latest snapshot, search filters and the selected device
/// all survive navigation between Home, Ingestion, Command Stream and Topology.
/// One hub connection carries both live feeds: "snapshot" (Part 2 command stream) and
/// "topology" (final PoE mesh), so every pillar shares the same link and link-state indicator.
///
/// Transport: SignalR push (1 snapshot/second). If the hub cannot connect, it falls back
/// to REST polling and keeps retrying the hub in the background, so the UI never freezes.
/// </summary>
public sealed class StreamStateService : IAsyncDisposable
{
    private readonly HttpClient _http;
    private HubConnection? _hub;
    private Task? _startTask;
    private CancellationTokenSource? _fallbackCts;
    private CancellationTokenSource? _searchDebounce;
    private HashSet<string> _knownAlertKeys = new();
    private bool _firstSnapshot = true;

    public StreamStateService(HttpClient http)
    {
        _http = http;
        ApiBase = http.BaseAddress?.ToString().TrimEnd('/') ?? string.Empty;
    }

    public string ApiBase { get; }
    public StreamSnapshot? Snapshot { get; private set; }
    public LinkState Link { get; private set; } = LinkState.Connecting;
    public DateTime? LastUpdateUtc { get; private set; }
    public List<UiToast> Toasts { get; } = new();
    public List<string> ActivityLog { get; } = new();

    // ---- UI state preserved across page navigation -------------------------------------
    public string SearchText { get; private set; } = string.Empty;
    public string CategoryFilter { get; private set; } = string.Empty;
    public string StateFilter { get; private set; } = string.Empty;
    public List<string>? FilteredMacs { get; private set; }
    public string? SelectedMac { get; set; }
    public int TimelineMinutes { get; set; } = 5;
    public List<TimelinePoint>? Timeline { get; private set; }
    public List<LearnedPattern>? LearnedPatterns { get; private set; }

    // ---- Final PoE: live mesh topology (same connection, separate message) ----------------
    public TopologySnapshot? Topology { get; private set; }
    public event Action? TopologyChanged;

    public bool FilterActive => SearchText.Length > 0 || CategoryFilter.Length > 0 || StateFilter.Length > 0;

    public event Action? Changed;
    private void Notify() => Changed?.Invoke();

    // =====================================================================================
    //  Connection
    // =====================================================================================

    public Task EnsureStartedAsync() => _startTask ??= StartAsync();

    private async Task StartAsync()
    {
        _hub = new HubConnectionBuilder()
            .WithUrl($"{ApiBase}/hubs/telemetry")
            .WithAutomaticReconnect(new[] { TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10) })
            .Build();

        _hub.On<StreamSnapshot>("snapshot", snapshot => ApplySnapshotAsync(snapshot));
        _hub.On<TopologySnapshot>("topology", topology => ApplyTopology(topology));
        _hub.Reconnecting += _ => { SetLink(LinkState.Reconnecting); return Task.CompletedTask; };
        _hub.Reconnected += _ => { SetLink(LinkState.Live); return Task.CompletedTask; };
        _hub.Closed += _ => { SetLink(LinkState.Offline); StartFallback(); return Task.CompletedTask; };

        try
        {
            await _hub.StartAsync();
            SetLink(LinkState.Live);
        }
        catch
        {
            SetLink(LinkState.Offline);
            StartFallback();
        }
    }

    private void SetLink(LinkState state)
    {
        if (Link == state) return;
        Link = state;
        Notify();
    }

    private void StartFallback()
    {
        if (_fallbackCts is not null) return;
        _fallbackCts = new CancellationTokenSource();
        _ = FallbackLoopAsync(_fallbackCts.Token);
    }

    /// <summary>REST polling while the hub is down; retries the hub every ~10 seconds.</summary>
    private async Task FallbackLoopAsync(CancellationToken ct)
    {
        int attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            if (await RefreshAsync()) SetLink(LinkState.Polling);
            else SetLink(LinkState.Offline);
            await RefreshTopologyAsync();

            if (++attempt % 5 == 0 && _hub is { State: HubConnectionState.Disconnected })
            {
                try
                {
                    await _hub.StartAsync(ct);
                    SetLink(LinkState.Live);
                    _fallbackCts = null;
                    return;
                }
                catch { /* keep polling */ }
            }

            try { await Task.Delay(2000, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Pulls a fresh snapshot immediately (used after a command so the UI reacts instantly).</summary>
    public async Task<bool> RefreshAsync()
    {
        try
        {
            var snapshot = await _http.GetFromJsonAsync<StreamSnapshot>("/api/stream/snapshot");
            if (snapshot is null) return false;
            await ApplySnapshotAsync(snapshot);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Pulls the mesh topology immediately (after an operator action, or while SignalR is down).</summary>
    public async Task<bool> RefreshTopologyAsync()
    {
        try
        {
            var topology = await _http.GetFromJsonAsync<TopologySnapshot>("/api/topology");
            if (topology is null) return false;
            ApplyTopology(topology);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void ApplyTopology(TopologySnapshot topology)
    {
        Topology = topology;
        TopologyChanged?.Invoke();
    }

    private async Task ApplySnapshotAsync(StreamSnapshot snapshot)
    {
        // New critical alerts become toasts (a HashSet diff, so each alert toasts exactly once).
        var keys = snapshot.ActiveAlerts.Select(a => $"{a.MacAddress}|{a.Code}").ToHashSet();
        if (!_firstSnapshot)
        {
            foreach (var alert in snapshot.ActiveAlerts.Where(a => a.Severity == AlertSeverity.Critical))
                if (!_knownAlertKeys.Contains($"{alert.MacAddress}|{alert.Code}"))
                    AddToast(alert);
        }
        _knownAlertKeys = keys;
        _firstSnapshot = false;

        Snapshot = snapshot;
        LastUpdateUtc = DateTime.UtcNow;
        Toasts.RemoveAll(t => DateTime.UtcNow - t.CreatedUtc > TimeSpan.FromSeconds(8));

        // Keep server-side filter results and the chart in step with live data.
        if (FilterActive) await RunSearchAsync(log: false, notify: false);
        if (SelectedMac is not null) await LoadTimelineAsync(notify: false);
        Notify();
    }

    private void AddToast(AlertEntry alert)
    {
        Toasts.Insert(0, new UiToast(Guid.NewGuid(), alert.Severity, alert.DeviceName, alert.Message, alert.MacAddress, DateTime.UtcNow));
        if (Toasts.Count > 4) Toasts.RemoveAt(Toasts.Count - 1);
    }

    public void DismissToast(Guid id)
    {
        Toasts.RemoveAll(t => t.Id == id);
        Notify();
    }

    // =====================================================================================
    //  Search / filter (server-side: MAC dictionary + category index)
    // =====================================================================================

    public async Task SetSearchTextAsync(string text)
    {
        SearchText = text?.Trim() ?? string.Empty;
        // Debounce typing: only the final query after 350 ms is sent and learned from.
        _searchDebounce?.Cancel();
        var cts = _searchDebounce = new CancellationTokenSource();
        try { await Task.Delay(350, cts.Token); }
        catch (OperationCanceledException) { return; }
        await RunSearchAsync(log: true, notify: true);
    }

    public async Task SetCategoryAsync(string category)
    {
        CategoryFilter = category ?? string.Empty;
        await RunSearchAsync(log: false, notify: true);
    }

    public async Task SetStateFilterAsync(string state)
    {
        StateFilter = state ?? string.Empty;
        await RunSearchAsync(log: false, notify: true);
    }

    public async Task ClearFiltersAsync()
    {
        SearchText = CategoryFilter = StateFilter = string.Empty;
        FilteredMacs = null;
        Notify();
        await Task.CompletedTask;
    }

    private async Task RunSearchAsync(bool log, bool notify)
    {
        if (!FilterActive)
        {
            FilteredMacs = null;
            if (notify) Notify();
            return;
        }
        try
        {
            string url = $"/api/stream/devices?q={Uri.EscapeDataString(SearchText)}" +
                         $"&category={Uri.EscapeDataString(CategoryFilter)}" +
                         $"&state={Uri.EscapeDataString(StateFilter)}&log={(log ? "true" : "false")}";
            var devices = await _http.GetFromJsonAsync<List<LiveDevice>>(url);
            FilteredMacs = devices?.Select(d => d.MacAddress).ToList() ?? new();
        }
        catch
        {
            // keep the previous result if the API blips
        }
        if (notify) Notify();
    }

    /// <summary>Devices to render: the live snapshot, narrowed to the server's search results.</summary>
    public IReadOnlyList<LiveDevice> VisibleDevices()
    {
        if (Snapshot is null) return Array.Empty<LiveDevice>();
        if (!FilterActive || FilteredMacs is null) return Snapshot.Devices;
        var byMac = Snapshot.Devices.ToDictionary(d => d.MacAddress, StringComparer.OrdinalIgnoreCase);
        return FilteredMacs.Where(byMac.ContainsKey).Select(m => byMac[m]).ToList();
    }

    // =====================================================================================
    //  Selected device timeline (SortedDictionary on the server)
    // =====================================================================================

    public async Task SelectDeviceAsync(string? mac)
    {
        SelectedMac = mac;
        Timeline = null;
        Notify();
        if (mac is not null) await LoadTimelineAsync(notify: true);
    }

    public async Task SetTimelineMinutesAsync(int minutes)
    {
        TimelineMinutes = minutes;
        await LoadTimelineAsync(notify: true);
    }

    private async Task LoadTimelineAsync(bool notify)
    {
        if (SelectedMac is null) return;
        try
        {
            Timeline = await _http.GetFromJsonAsync<List<TimelinePoint>>(
                $"/api/stream/devices/{Uri.EscapeDataString(SelectedMac)}/timeline?minutes={TimelineMinutes}");
        }
        catch { /* keep last chart */ }
        if (notify) Notify();
    }

    // =====================================================================================
    //  Commands
    // =====================================================================================

    public Task<CommandResult> SendCommandAsync(string mac, string action, string source = "operator") =>
        PostCommandAsync("/api/stream/commands", new DeviceCommandRequest(mac, action, source));

    public Task<CommandResult> SendBulkAsync(string action, string? nameContains, string label) =>
        PostCommandAsync("/api/stream/commands/bulk", new BulkCommandRequest(action, nameContains, label));

    public Task<CommandResult> UndoAsync() => PostCommandAsync("/api/stream/commands/undo", null);
    public Task<CommandResult> RedoAsync() => PostCommandAsync("/api/stream/commands/redo", null);

    private async Task<CommandResult> PostCommandAsync(string url, object? body)
    {
        CommandResult result;
        try
        {
            using var response = body is null
                ? await _http.PostAsync(url, null)
                : await _http.PostAsJsonAsync(url, body);
            result = await response.Content.ReadFromJsonAsync<CommandResult>()
                     ?? new CommandResult(false, $"Gateway returned {(int)response.StatusCode}.");
        }
        catch (Exception ex)
        {
            result = new CommandResult(false, $"Gateway unreachable: {ex.Message}");
        }
        Log((result.Success ? "✔ " : "✖ ") + result.Message);
        await RefreshAsync();
        return result;
    }

    public async Task<BurstResult?> SimulateBurstAsync(string? criticalMac = null)
    {
        try
        {
            using var response = await _http.PostAsJsonAsync("/api/stream/simulate/burst", new BurstRequest(600, true, criticalMac));
            var result = await response.Content.ReadFromJsonAsync<BurstResult>();
            if (result is not null)
                Log($"⚡ Burst: {result.StandardEnqueued} routine packets queued, then 1 critical packet for {result.CriticalDevice} " +
                    $"(queues: std {result.StandardQueueDepth}, priority {result.PriorityQueueDepth}).");
            await RefreshAsync();
            return result;
        }
        catch (Exception ex)
        {
            Log($"✖ Burst failed: {ex.Message}");
            return null;
        }
    }

    public async Task InjectCriticalAsync(string mac)
    {
        try
        {
            using var response = await _http.PostAsync($"/api/stream/simulate/critical/{Uri.EscapeDataString(mac)}", null);
            var ack = response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<IngestAck>() : null;
            Log(ack is null ? "✖ Could not inject a critical reading for that device."
                            : $"🚨 Critical reading injected → {ack.Lane} lane (priority depth {ack.PriorityQueueDepth}).");
        }
        catch (Exception ex)
        {
            Log($"✖ Inject failed: {ex.Message}");
        }
        await RefreshAsync();
    }

    public async Task LoadPatternsAsync()
    {
        try { LearnedPatterns = await _http.GetFromJsonAsync<List<LearnedPattern>>("/api/stream/patterns"); }
        catch { LearnedPatterns ??= new(); }
        Notify();
    }

    private void Log(string message)
    {
        ActivityLog.Insert(0, $"{DateTime.Now:HH:mm:ss}  {message}");
        if (ActivityLog.Count > 12) ActivityLog.RemoveAt(ActivityLog.Count - 1);
        Notify();
    }

    public async ValueTask DisposeAsync()
    {
        _fallbackCts?.Cancel();
        if (_hub is not null) await _hub.DisposeAsync();
    }
}
