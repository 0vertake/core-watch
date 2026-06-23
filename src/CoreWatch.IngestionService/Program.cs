using System.Collections.Concurrent;
using System.Net.Http.Json;
using CoreWatch.Shared;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

string connectionString = builder.Configuration.GetConnectionString("CoreWatch")
    ?? "Host=localhost;Port=5432;Database=corewatch;Username=postgres;Password=postgres";

builder.Services.AddDbContext<CoreWatchDbContext>(options =>
    options.UseNpgsql(connectionString));
builder.Services.AddHttpClient();
builder.Services.AddSingleton<SensorRateLimiter>();
builder.Services.AddHostedService<SensorMonitorService>();

var app = builder.Build();

await EnsureDatabaseReady(app.Services);

app.MapGet("/", () => "CoreWatch IngestionService is running. POST /api/ingest");

app.MapGet("/api/sensors", async (CoreWatchDbContext db) =>
    await db.Sensors.OrderBy(x => x.Id).ToListAsync());

app.MapGet("/api/measurements", async (
    CoreWatchDbContext db,
    int limit = 50,
    string? sensorId = null,
    bool consensusOnly = false) =>
{
    limit = Math.Clamp(limit, 1, 500);
    var query = db.Measurements.AsQueryable();

    if (!string.IsNullOrWhiteSpace(sensorId))
        query = query.Where(x => x.SensorId == sensorId);

    if (consensusOnly)
        query = query.Where(x => x.IsConsensus);

    var items = await query
        .OrderByDescending(x => x.TimestampUtc)
        .Take(limit)
        .ToListAsync();

    return Results.Ok(items);
});

app.MapGet("/api/alarms", async (CoreWatchDbContext db, int limit = 50) =>
{
    limit = Math.Clamp(limit, 1, 500);
    var items = await db.Alarms
        .OrderByDescending(x => x.TimestampUtc)
        .Take(limit)
        .ToListAsync();

    return Results.Ok(items);
});

app.MapGet("/api/reports", async (CoreWatchDbContext db) =>
{
    var latestConsensus = await db.ConsensusValues
        .OrderByDescending(x => x.CreatedUtc)
        .FirstOrDefaultAsync();

    return Results.Ok(new
    {
        Measurements = await db.Measurements.CountAsync(),
        Alarms = await db.Alarms.CountAsync(),
        ConsensusValues = await db.ConsensusValues.CountAsync(),
        ActiveSensors = await db.Sensors.CountAsync(x => x.IsActive),
        LatestConsensus = latestConsensus,
        Endpoints = new
        {
            Measurements = "/api/measurements?limit=50",
            Alarms = "/api/alarms?limit=50",
            Sensors = "/api/sensors"
        }
    });
});

app.MapPost("/api/sensors/{sensorId}/block", async (string sensorId, CoreWatchDbContext db) =>
{
    var sensor = await db.Sensors.FindAsync(sensorId);
    if (sensor is null)
        return Results.NotFound("Sensor does not exist.");

    sensor.IsActive = false;
    sensor.BlockedUntilUtc = DateTime.UtcNow.AddSeconds(30);
    await ActivateStandbySensor(db);
    await db.SaveChangesAsync();
    return Results.Ok($"{sensorId} has been blocked for 30 seconds.");
});

app.MapPost("/api/ingest", async (
    SecureEnvelope envelope,
    CoreWatchDbContext db,
    SensorRateLimiter rateLimiter,
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration) =>
{
    var now = DateTime.UtcNow;
    var sensor = await db.Sensors.FindAsync(envelope.SensorId);
    if (sensor is null)
        return Results.NotFound(new IngestResult(false, "Unknown sensor."));

    if (sensor.BlockedUntilUtc > now)
        return Results.StatusCode(StatusCodes.Status429TooManyRequests);

    if (rateLimiter.ShouldBlock(envelope.SensorId, now))
    {
        sensor.IsActive = false;
        sensor.BlockedUntilUtc = now.AddSeconds(30);
        await ActivateStandbySensor(db);
        await db.SaveChangesAsync();
        return Results.StatusCode(StatusCodes.Status429TooManyRequests);
    }

    if (!sensor.IsActive)
        return Results.BadRequest(new IngestResult(false, "Sensor is not currently active."));

    if (Math.Abs((now - envelope.TimestampUtc).TotalMinutes) > 2)
        return Results.BadRequest(new IngestResult(false, "Message timestamp is not valid."));

    if (envelope.MessageId <= sensor.LastMessageId)
        return Results.BadRequest(new IngestResult(false, "Replay message rejected."));

    string aesKey = configuration["Security:AesKey"] ?? "core-watch-student-demo-key";
    if (!CryptoService.TryReadPayload(envelope, aesKey, out var payload, out string error) || payload is null)
        return Results.BadRequest(new IngestResult(false, error));

    sensor.LastMessageId = envelope.MessageId;
    sensor.LastMessageUtc = now;
    sensor.PublicKeyPem ??= envelope.PublicKeyPem;
    sensor.Quality = payload.Quality;

    var measurement = new Measurement
    {
        SensorId = payload.SensorId,
        Value = payload.Value,
        TimestampUtc = payload.TimestampUtc,
        AlarmPriority = payload.AlarmPriority,
        Quality = payload.Quality,
        IsConsensus = false
    };

    db.Measurements.Add(measurement);

    if (payload.AlarmPriority > 0)
    {
        db.Alarms.Add(new AlarmEvent
        {
            SensorId = payload.SensorId,
            Value = payload.Value,
            Priority = payload.AlarmPriority,
            TimestampUtc = payload.TimestampUtc
        });

        WriteAlarm(payload.SensorId, payload.Value, payload.AlarmPriority);
        await NotifyAlarm(httpClientFactory, configuration, payload);
    }

    await db.SaveChangesAsync();
    return Results.Ok(new IngestResult(true, "Measurement received."));
});

app.Run();

static async Task EnsureDatabaseReady(IServiceProvider services)
{
    for (int attempt = 1; attempt <= 10; attempt++)
    {
        try
        {
            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CoreWatchDbContext>();
            await db.Database.EnsureCreatedAsync();
            SeedSensors(db);
            return;
        }
        catch (Exception ex) when (attempt < 10)
        {
            Console.WriteLine($"Database is not ready, attempt {attempt}/10: {ex.Message}");
            await Task.Delay(2_000);
        }
    }
}

static void SeedSensors(CoreWatchDbContext db)
{
    if (db.Sensors.Any())
        return;

    var now = DateTime.UtcNow;
    for (int i = 1; i <= 8; i++)
    {
        db.Sensors.Add(new SensorConfig
        {
            Id = $"sensor-{i}",
            MinTemperature = 250,
            MaxTemperature = 850,
            Quality = i == 7 ? DataQuality.UNCERTAIN : DataQuality.GOOD,
            AlarmLimit1 = 550,
            AlarmLimit2 = 650,
            AlarmLimit3 = 750,
            IsActive = i <= 5,
            LastMessageUtc = i <= 5 ? now : null
        });
    }

    db.SaveChanges();
}

static async Task ActivateStandbySensor(CoreWatchDbContext db)
{
    int activeCount = await db.Sensors.CountAsync(x => x.IsActive);
    if (activeCount >= 5)
        return;

    var now = DateTime.UtcNow;
    var candidates = await db.Sensors
        .Where(x => !x.IsActive && (x.BlockedUntilUtc == null || x.BlockedUntilUtc <= now))
        .OrderBy(x => x.Id)
        .Take(5 - activeCount)
        .ToListAsync();

    foreach (var candidate in candidates)
    {
        candidate.IsActive = true;
        candidate.LastMessageUtc = now;
    }
}

static void WriteAlarm(string sensorId, double value, int priority)
{
    AlarmConsole.WriteLine(priority, $"[ALARM P{priority}] {sensorId}: {value:F2} C");
}

static async Task NotifyAlarm(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    SensorMeasurementPayload payload)
{
    string url = configuration["NotificationService:Url"]
        ?? "http://localhost:5002/api/notifications/alarm";

    try
    {
        var client = httpClientFactory.CreateClient();
        await client.PostAsJsonAsync(url, new AlarmNotificationDto(
            payload.SensorId,
            payload.Value,
            payload.AlarmPriority,
            payload.TimestampUtc));
    }
    catch (Exception ex)
    {
        Console.WriteLine($"NotificationService is not available: {ex.Message}");
    }
}

public class SensorRateLimiter
{
    private readonly ConcurrentDictionary<string, Queue<DateTime>> _messages = new();
    private readonly object _lock = new();

    public bool ShouldBlock(string sensorId, DateTime now)
    {
        lock (_lock)
        {
            var queue = _messages.GetOrAdd(sensorId, _ => new Queue<DateTime>());
            while (queue.Count > 0 && (now - queue.Peek()).TotalSeconds >= 1)
                queue.Dequeue();

            queue.Enqueue(now);
            return queue.Count > 10;
        }
    }
}

public class SensorMonitorService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;

    public SensorMonitorService(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);

                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<CoreWatchDbContext>();
                var now = DateTime.UtcNow;

                var inactive = await db.Sensors
                    .Where(x => x.IsActive &&
                        x.LastMessageUtc != null &&
                        x.LastMessageUtc < now.AddSeconds(-10))
                    .ToListAsync(stoppingToken);

                foreach (var sensor in inactive)
                {
                    sensor.IsActive = false;
                    Console.WriteLine($"Sensor {sensor.Id} has been inactive for more than 10 seconds.");
                }

                await ActivateStandbySensorAsync(db, stoppingToken);
                await db.SaveChangesAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in SensorMonitorService: {ex.Message}");
            }
        }
    }

    private static async Task ActivateStandbySensorAsync(CoreWatchDbContext db, CancellationToken stoppingToken)
    {
        int activeCount = await db.Sensors.CountAsync(x => x.IsActive, stoppingToken);
        if (activeCount >= 5)
            return;

        var now = DateTime.UtcNow;
        var candidates = await db.Sensors
            .Where(x => !x.IsActive && (x.BlockedUntilUtc == null || x.BlockedUntilUtc <= now))
            .OrderBy(x => x.Id)
            .Take(5 - activeCount)
            .ToListAsync(stoppingToken);

        foreach (var candidate in candidates)
        {
            candidate.IsActive = true;
            candidate.LastMessageUtc = now;
        }
    }
}
