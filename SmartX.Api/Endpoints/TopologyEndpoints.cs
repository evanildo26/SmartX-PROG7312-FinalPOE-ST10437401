using System.Globalization;
using SmartX.Api.Services.Topology;
using SmartX.Shared;

namespace SmartX.Api.Endpoints;

/// <summary>Final PoE — Network Topology &amp; Mesh Routing REST API (all routes under /api/topology).</summary>
public static class TopologyEndpoints
{
    public static IEndpointRouteBuilder MapTopologyEndpoints(this IEndpointRouteBuilder app)
    {
        var topo = app.MapGroup("/api/topology").WithTags("Network Topology");

        // ---- Mesh graph ---------------------------------------------------------------------
        topo.MapGet("/", (MeshTopologyEngine t) => Results.Ok(t.GetSnapshot()));

        // ---- Route tracking by MAC address (MST route + Dijkstra + backup hop) -----------------
        topo.MapGet("/route/{mac}", (MeshTopologyEngine t, string mac, string? to) => Results.Ok(t.Route(mac, to)));

        // ---- Traversals: who is connected to the gateway / a subnet, and who is down ----------
        topo.MapGet("/bfs", (MeshTopologyEngine t, string? start, string? zone) => Results.Ok(t.Traverse("bfs", start, zone)));
        topo.MapGet("/dfs", (MeshTopologyEngine t, string? start, string? zone) => Results.Ok(t.Traverse("dfs", start, zone)));

        // ---- Minimum spanning tree -------------------------------------------------------------
        topo.MapGet("/mst", (MeshTopologyEngine t, string? algorithm) =>
        {
            string alg = string.IsNullOrWhiteSpace(algorithm) ? "prim" : algorithm.Trim().ToLowerInvariant();
            return alg is "prim" or "kruskal"
                ? Results.Ok(t.MinimumSpanningTree(alg))
                : Results.BadRequest("algorithm must be 'prim' or 'kruskal'.");
        });

        // ---- Simulated failures (to demonstrate self-healing) --------------------------------
        topo.MapPost("/links/toggle", (MeshTopologyEngine t, LinkToggleRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(req.A) || string.IsNullOrWhiteSpace(req.B))
                return Results.BadRequest(new TopologyActionResult(false, "Both MAC addresses are required."));
            var result = t.ToggleLink(req.A, req.B);
            return result.Success ? Results.Ok(result) : Results.BadRequest(result);
        });

        topo.MapPost("/nodes/{mac}/toggle", (MeshTopologyEngine t, string mac) =>
        {
            var result = t.ToggleNode(mac);
            return result.Success ? Results.Ok(result) : Results.BadRequest(result);
        });

        topo.MapPost("/reset", (MeshTopologyEngine t) => Results.Ok(t.ResetFailures()));

        // ---- Device uplink: neighbour table with RSSI (what each ESP32 reports) --------------
        topo.MapPost("/neighbors", (MeshTopologyEngine t, NeighborReport report) =>
        {
            if (string.IsNullOrWhiteSpace(report.MacAddress) || report.Neighbors is null)
                return Results.BadRequest("MacAddress and Neighbors are required.");
            return Results.Accepted(value: new { updated = t.ReportNeighbors(report) });
        });

        // ---- Historical telemetry (AVL tree range queries) ------------------------------------
        topo.MapGet("/history", (MeshTopologyEngine t) => Results.Ok(t.DescribeHistory()));

        // Range by ISO-8601 (from/to, e.g. 2026-10-22T14:00:00%2B02:00), Unix milliseconds (fromMs/toMs),
        // or the last N minutes (minutes). Strings without an offset are treated as UTC.
        topo.MapGet("/history/{mac}", (MeshTopologyEngine t, string mac, string? from, string? to,
                                       long? fromMs, long? toMs, int? minutes, int? maxPoints) =>
        {
            var now = DateTime.UtcNow;
            DateTime toUtc = toMs is { } tm ? DateTimeOffset.FromUnixTimeMilliseconds(tm).UtcDateTime
                           : TryParseUtc(to, out var parsedTo) ? parsedTo
                           : now;
            DateTime fromUtc = fromMs is { } fm ? DateTimeOffset.FromUnixTimeMilliseconds(fm).UtcDateTime
                             : TryParseUtc(from, out var parsedFrom) ? parsedFrom
                             : toUtc.AddMinutes(-Math.Clamp(minutes ?? 120, 1, 60 * 24 * 31));

            var result = t.QueryHistory(mac, fromUtc, toUtc, maxPoints ?? 600);
            return result is null ? Results.NotFound($"Device {mac} is not registered.") : Results.Ok(result);
        });

        // ---- Scale benchmark (AVL vs linear scan, graph algorithms on a large mesh) -----------
        topo.MapGet("/benchmark", (MeshTopologyEngine t, int? nodes) => Results.Ok(t.RunScaleBenchmark(nodes ?? 2000)));

        return app;
    }

    private static bool TryParseUtc(string? text, out DateTime utc)
    {
        utc = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)) return false;
        utc = parsed.UtcDateTime;
        return true;
    }
}
