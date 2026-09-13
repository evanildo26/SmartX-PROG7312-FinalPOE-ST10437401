using SmartX.Shared;
using System.Collections.Concurrent;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowClient", p => p
        .AllowAnyOrigin()
        .AllowAnyMethod()
        .AllowAnyHeader());
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddOpenApi();

var app = builder.Build();
app.UseCors("AllowClient");
if (app.Environment.IsDevelopment()) app.MapOpenApi();

// In-memory "registry" — thread-safe Dictionary<TKey,TValue> keyed by MAC address
var registry = new ConcurrentDictionary<string, SensorProfile>();
var deploymentTree = DeploymentNode.BuildDemoTree();

// ---- Sensor registration -------------------------------------------------
app.MapPost("/api/sensors", (SensorRegistrationRequest req) =>
{
    var profile = new SensorProfile
    {
        MacAddress = req.MacAddress,
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

    registry[req.MacAddress] = profile;
    return Results.Created($"/api/sensors/{req.MacAddress}", profile);
});

app.MapGet("/api/sensors", () => registry.Values);

app.MapGet("/api/sensors/{mac}", (string mac) =>
    registry.TryGetValue(mac, out var p) ? Results.Ok(p) : Results.NotFound());

// ---- Telemetry ingestion (uses the generic TelemetryPacket<T>) ----------
app.MapPost("/api/sensors/{mac}/telemetry", (string mac, TelemetryIngestRequest req) =>
{
    if (!registry.TryGetValue(mac, out var profile))
        return Results.NotFound($"Sensor {mac} not registered.");

    var packet = new TelemetryPacket<double>
    {
        SensorId = mac,
        Value = req.Value,
        Unit = req.Unit
    };

    profile.TelemetryHistory.Add(packet);
    profile.ProcessedReadings.Add(req.Value);
    return Results.Ok(packet);
});

app.MapGet("/api/sensors/{mac}/telemetry", (string mac) =>
    registry.TryGetValue(mac, out var p) ? Results.Ok(p.TelemetryHistory) : Results.NotFound());

// ---- File / log upload ----------------------------------------------------
app.MapPost("/api/sensors/{mac}/upload", async (string mac, IFormFile file) =>
{
    if (!registry.TryGetValue(mac, out var profile))
        return Results.NotFound($"Sensor {mac} not registered.");

    var uploadsDir = Path.Combine(AppContext.BaseDirectory, "uploads", mac);
    Directory.CreateDirectory(uploadsDir);
    var savePath = Path.Combine(uploadsDir, file.FileName);

    await using (var stream = File.Create(savePath))
        await file.CopyToAsync(stream);

    profile.AttachedFiles.Add(file.FileName);
    return Results.Ok(new { file.FileName, file.Length });
}).DisableAntiforgery();

// ---- Recursive deployment-path validation --------------------------------
app.MapPost("/api/deployment/validate", (PathValidationRequest req) =>
{
    bool isValid = deploymentTree.ValidatePath(req.Path);
    return Results.Ok(new { req.Path, IsValid = isValid });
});

// ---- Operator overloading demo endpoint (aggregate two meters) ----------
app.MapGet("/api/power/aggregate", (double watts1, double watts2) =>
{
    var meter1 = new PowerReading { SensorId = "Meter1", Watts = watts1 };
    var meter2 = new PowerReading { SensorId = "Meter2", Watts = watts2 };
    var combined = meter1 + meter2; // operator overload in action
    return Results.Ok(new { meter1, meter2, combined, meter1IsGreater = meter1 > meter2 });
});

app.Run();

// Request DTOs
//-----------------------------------------------------END----------------------------//