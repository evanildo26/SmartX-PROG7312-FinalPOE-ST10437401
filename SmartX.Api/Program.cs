using SmartX.Api.Endpoints;
using SmartX.Api.Services;
using SmartX.Shared;
using System.Collections.Concurrent;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCors(options =>
{
    // SignalR negotiates with credentials, so AllowAnyOrigin() is not allowed together
    // with AllowCredentials(); SetIsOriginAllowed keeps local dev origins flexible.
    options.AddPolicy("AllowClient", p => p
        .SetIsOriginAllowed(_ => true)
        .AllowAnyMethod()
        .AllowAnyHeader()
        .AllowCredentials());
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddOpenApi();
builder.Services.AddSignalR();

// ---- Part 2: real-time command stream engine -----------------------------------------
builder.Services.Configure<GatewayOptions>(builder.Configuration.GetSection("Gateway"));
builder.Services.AddSingleton(_ =>
{
    var engine = new CommandStreamEngine(new ActionPatternEngine());
    FleetSeeder.Seed(engine);          // heavily seeded fleet + history + operator behaviour
    return engine;
});
builder.Services.AddHostedService<StreamProcessorService>();
builder.Services.AddHostedService<SnapshotBroadcaster>();

var app = builder.Build();
app.UseCors("AllowClient");
if (app.Environment.IsDevelopment()) app.MapOpenApi();

// In-memory "registry" — thread-safe Dictionary<TKey,TValue> keyed by MAC address (Part 1)
var registry = new ConcurrentDictionary<string, SensorProfile>(StringComparer.OrdinalIgnoreCase);
var deploymentTree = DeploymentNode.BuildDemoTree();
var engine = app.Services.GetRequiredService<CommandStreamEngine>();

app.MapGet("/api/health", (CommandStreamEngine e) =>
    Results.Ok(new { status = "ok", utc = DateTime.UtcNow, devices = e.GetStats().DevicesTotal }));

// ---- Sensor registration (Part 1) -----------------------------------------------------
app.MapPost("/api/sensors", (SensorRegistrationRequest req) =>
{
    if (string.IsNullOrWhiteSpace(req.MacAddress))
        return Results.BadRequest("MAC address is required.");

    var mac = CommandStreamEngine.NormalizeMac(req.MacAddress);
    var profile = new SensorProfile
    {
        MacAddress = mac,
        Location = req.Location,
        Category = req.Category
    };

    // Seed some mock historical batches (jagged array) so the structure has data under load
    var rnd = new Random();
    profile.RawHistoricalBatches = new double[3][];
    for (int i = 0; i < 3; i++)
    {
        int batchSize = rnd.Next(3, 8); // jagged: each batch a different length
        profile.RawHistoricalBatches[i] = new double[batchSize];
        for (int j = 0; j < batchSize; j++)
            profile.RawHistoricalBatches[i][j] = Math.Round(rnd.NextDouble() * 100, 2);
    }
    profile.FlattenBatchesIntoList();

    registry[mac] = profile;

    // Part 2 integration: a sensor registered on the ingestion page joins the live
    // command-stream device dictionary too, so both pillars share one device registry.
    if (!engine.IsRegistered(mac))
    {
        engine.RegisterDevice(new LiveDevice
        {
            MacAddress = mac,
            DisplayName = $"Custom {req.Category} ({mac})",
            Category = DeviceCategories.All.Contains(req.Category) ? req.Category : DeviceCategories.Environmental,
            Location = req.Location,
            Metric = "value",
            Unit = ""
        });
    }
    return Results.Created($"/api/sensors/{mac}", profile);
});

app.MapGet("/api/sensors", () => registry.Values);

app.MapGet("/api/sensors/{mac}", (string mac) =>
    registry.TryGetValue(mac, out var p) ? Results.Ok(p) : Results.NotFound());

// ---- Telemetry ingestion (uses the generic TelemetryPacket<T>) ---------------------------
app.MapPost("/api/sensors/{mac}/telemetry", (string mac, TelemetryIngestRequest req) =>
{
    if (!registry.TryGetValue(mac, out var profile))
        return Results.NotFound($"Sensor {mac} not registered.");

    var packet = new TelemetryPacket<double>
    {
        SensorId = profile.MacAddress,
        Value = req.Value,
        Unit = req.Unit
    };

    profile.TelemetryHistory.Add(packet);
    profile.ProcessedReadings.Add(req.Value);

    // Part 2 integration: the same reading also flows through the real-time queues.
    engine.Enqueue(new StreamIngestRequest(profile.MacAddress, req.Value, req.Unit));
    return Results.Ok(packet);
});

app.MapGet("/api/sensors/{mac}/telemetry", (string mac) =>
    registry.TryGetValue(mac, out var p) ? Results.Ok(p.TelemetryHistory) : Results.NotFound());

// ---- File / log upload --------------------------------------------------------------------
app.MapPost("/api/sensors/{mac}/upload", async (string mac, IFormFile file) =>
{
    if (!registry.TryGetValue(mac, out var profile))
        return Results.NotFound($"Sensor {mac} not registered.");

    // Path.GetFileName strips any directory parts, so "../../x" cannot escape the uploads folder.
    var safeName = Path.GetFileName(file.FileName);
    var uploadsDir = Path.Combine(AppContext.BaseDirectory, "uploads", profile.MacAddress.Replace(':', '-'));
    Directory.CreateDirectory(uploadsDir);
    var savePath = Path.Combine(uploadsDir, safeName);

    await using (var stream = File.Create(savePath))
        await file.CopyToAsync(stream);

    profile.AttachedFiles.Add(safeName);
    return Results.Ok(new { FileName = safeName, file.Length });
}).DisableAntiforgery();

// ---- Recursive deployment-path validation --------------------------------------------------
app.MapPost("/api/deployment/validate", (PathValidationRequest req) =>
{
    bool isValid = deploymentTree.ValidatePath(req.Path);
    return Results.Ok(new { req.Path, IsValid = isValid });
});

// ---- Operator overloading demo endpoint (aggregate two meters) ----------------------------
app.MapGet("/api/power/aggregate", (double watts1, double watts2) =>
{
    var meter1 = new PowerReading { SensorId = "Meter1", Watts = watts1 };
    var meter2 = new PowerReading { SensorId = "Meter2", Watts = watts2 };
    var combined = meter1 + meter2; // operator overload in action
    return Results.Ok(new { meter1, meter2, combined, meter1IsGreater = meter1 > meter2 });
});

// ---- Part 2: Real-Time Command Stream ------------------------------------------------------
app.MapStreamEndpoints();
app.MapHub<TelemetryHub>("/hubs/telemetry");

app.Run();

/// <summary>Exposed so integration tests can reference the API assembly.</summary>
public partial class Program { }
