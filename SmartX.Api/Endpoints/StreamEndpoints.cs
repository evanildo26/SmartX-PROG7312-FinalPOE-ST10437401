using SmartX.Api.Services;
using SmartX.Shared;

namespace SmartX.Api.Endpoints;

/// <summary>Part 2 — Real-Time Command Stream REST API (all routes under /api/stream).</summary>
public static class StreamEndpoints
{
    public static IEndpointRouteBuilder MapStreamEndpoints(this IEndpointRouteBuilder app)
    {
        var stream = app.MapGroup("/api/stream").WithTags("Command Stream");

        // ---- Dashboard ------------------------------------------------------------------
        stream.MapGet("/snapshot", (CommandStreamEngine e) => Results.Ok(e.GetSnapshot()));
        stream.MapGet("/stats", (CommandStreamEngine e) => Results.Ok(e.GetStats()));
        stream.MapGet("/alerts", (CommandStreamEngine e) => Results.Ok(e.GetSnapshot().ActiveAlerts));
        stream.MapGet("/alerts/history", (CommandStreamEngine e) => Results.Ok(e.AlertHistory()));
        stream.MapGet("/suggestions", (CommandStreamEngine e) => Results.Ok(e.GetSuggestions()));
        stream.MapGet("/patterns", (CommandStreamEngine e) => Results.Ok(e.GetLearnedPatterns()));

        // ---- Devices: search / filter / timeline ----------------------------------------
        stream.MapGet("/devices", (CommandStreamEngine e, string? q, string? category, string? state, bool? log) =>
            Results.Ok(e.Search(q, category, state, log ?? false)));

        stream.MapGet("/devices/{mac}", (CommandStreamEngine e, string mac) =>
            e.GetDevice(mac) is { } d ? Results.Ok(d) : Results.NotFound());

        stream.MapGet("/devices/{mac}/timeline", (CommandStreamEngine e, string mac, int? minutes) =>
        {
            var points = e.GetTimeline(mac, TimeSpan.FromMinutes(Math.Clamp(minutes ?? 10, 1, 60)));
            return points is null ? Results.NotFound() : Results.Ok(points);
        });

        // ---- Device uplink (what an ESP32 calls) -----------------------------------------
        stream.MapPost("/ingest", (CommandStreamEngine e, StreamIngestRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(req.MacAddress))
                return Results.BadRequest("MacAddress is required.");
            if (double.IsNaN(req.Value) || double.IsInfinity(req.Value))
                return Results.BadRequest("Value must be a finite number.");

            var ack = e.Enqueue(req);
            return ack is null
                ? Results.NotFound($"Device {req.MacAddress} is not registered with the gateway.")
                : Results.Accepted(value: ack);   // 202: queued, processed asynchronously
        });

        // ---- Device downlink (device polls its command mailbox) ---------------------------
        stream.MapGet("/outbox/{mac}", (CommandStreamEngine e, string mac) => Results.Ok(e.DrainOutbox(mac)));

        // ---- Operator commands (Stack-based undo/redo) ------------------------------------
        stream.MapPost("/commands", (CommandStreamEngine e, DeviceCommandRequest req) => ToResult(e.IssueCommand(req)));
        stream.MapPost("/commands/bulk", (CommandStreamEngine e, BulkCommandRequest req) => ToResult(e.IssueBulkCommand(req)));
        stream.MapPost("/commands/undo", (CommandStreamEngine e) => ToResult(e.Undo()));
        stream.MapPost("/commands/redo", (CommandStreamEngine e) => ToResult(e.Redo()));
        stream.MapGet("/commands", (CommandStreamEngine e) => Results.Ok(e.GetSnapshot().CommandHistory));

        // ---- Test helpers ---------------------------------------------------------------
        stream.MapPost("/simulate/burst", (CommandStreamEngine e, BurstRequest? req) =>
            Results.Ok(e.SimulateBurst(req ?? new BurstRequest())));

        stream.MapPost("/simulate/critical/{mac}", (CommandStreamEngine e, string mac) =>
            e.InjectCritical(mac) is { } ack ? Results.Accepted(value: ack) : Results.NotFound());

        return app;
    }

    private static IResult ToResult(CommandResult result) =>
        result.Success ? Results.Ok(result) : Results.BadRequest(result);
}
