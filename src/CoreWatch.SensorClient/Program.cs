using System.Net.Http.Json;
using System.Security.Cryptography;
using CoreWatch.Shared;

string serverBaseUrl = args.Length > 0 ? args[0].TrimEnd('/') : "http://localhost:5080";
string aesKey = Environment.GetEnvironmentVariable("COREWATCH_AES_KEY")
    ?? "core-watch-student-demo-key";

using var http = new HttpClient();
using var cts = new CancellationTokenSource();

var sensors = Enumerable.Range(1, 8)
    .Select(i => new SimulatedSensor(
        id: $"sensor-{i}",
        minTemperature: 250,
        maxTemperature: 850,
        quality: i == 7 ? DataQuality.UNCERTAIN : DataQuality.GOOD,
        alarmLimit1: 550,
        alarmLimit2: 650,
        alarmLimit3: 750,
        isActive: i <= 5,
        isMalicious: i == 5,
        isUncertain: i == 7))
    .ToList();

Console.WriteLine("CoreWatch SensorClient");
Console.WriteLine($"Server: {serverBaseUrl}");
Console.WriteLine("Commands: /block sensor-1, /flood sensor-1, /status, /exit");

await LoadServerMessageIds();

var tasks = sensors
    .Select(sensor => Task.Run(() => SensorLoop(sensor, cts.Token)))
    .ToList();

tasks.Add(Task.Run(() => ActivationLoop(cts.Token)));

while (true)
{
    string? input = Console.ReadLine();
    if (input is null)
        continue;

    if (input.Equals("/exit", StringComparison.OrdinalIgnoreCase))
        break;

    if (input.Equals("/status", StringComparison.OrdinalIgnoreCase))
    {
        PrintStatus();
        continue;
    }

    if (input.StartsWith("/block ", StringComparison.OrdinalIgnoreCase))
    {
        string sensorId = input["/block ".Length..].Trim();
        await BlockSensor(sensorId);
        continue;
    }

    if (input.StartsWith("/flood ", StringComparison.OrdinalIgnoreCase))
    {
        string sensorId = input["/flood ".Length..].Trim();
        var sensor = sensors.FirstOrDefault(x => x.Id.Equals(sensorId, StringComparison.OrdinalIgnoreCase));
        if (sensor is null)
            Console.WriteLine("Unknown sensor.");
        else
            await FloodSensor(sensor);
    }
}

cts.Cancel();
await Task.WhenAll(tasks.Select(x => x.ContinueWith(_ => { })));

async Task SensorLoop(SimulatedSensor sensor, CancellationToken ct)
{
    while (!ct.IsCancellationRequested)
    {
        try
        {
            if (!sensor.IsActive || sensor.BlockedUntilUtc > DateTime.UtcNow)
            {
                await Task.Delay(500, ct);
                continue;
            }

            await SendMeasurement(sensor);
            await Task.Delay(Random.Shared.Next(1_000, 10_001), ct);
        }
        catch (OperationCanceledException)
        {
            break;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{sensor.Id}] error: {ex.Message}");
            await Task.Delay(1_000, ct);
        }
    }
}

async Task ActivationLoop(CancellationToken ct)
{
    while (!ct.IsCancellationRequested)
    {
        try
        {
            await Task.Delay(1_000, ct);
            int activeCount = sensors.Count(x => x.IsActive && x.BlockedUntilUtc <= DateTime.UtcNow);
            foreach (var sensor in sensors
                .Where(x => !x.IsActive && x.BlockedUntilUtc <= DateTime.UtcNow)
                .OrderBy(x => x.Id)
                .Take(5 - activeCount))
            {
                sensor.IsActive = true;
                Console.WriteLine($"{sensor.Id} was activated to keep exactly 5 active sensors.");
            }
        }
        catch (OperationCanceledException)
        {
            break;
        }
    }
}

async Task SendMeasurement(SimulatedSensor sensor)
{
    double value = sensor.NextTemperature();
    int priority = GetAlarmPriority(sensor, value);
    var payload = new SensorMeasurementPayload(
        sensor.Id,
        value,
        DateTime.UtcNow,
        sensor.CurrentQuality(),
        priority);

    long messageId = Interlocked.Increment(ref sensor.MessageId);
    var envelope = CryptoService.CreateEnvelope(
        payload,
        messageId,
        sensor.PrivateKey,
        sensor.PublicKeyPem,
        aesKey);

    var response = await http.PostAsJsonAsync($"{serverBaseUrl}/api/ingest", envelope);
    WriteMeasurement(sensor.Id, value, priority, response.IsSuccessStatusCode);
}

async Task LoadServerMessageIds()
{
    try
    {
        var serverSensors = await http.GetFromJsonAsync<List<SensorConfig>>($"{serverBaseUrl}/api/sensors");
        if (serverSensors is null)
            return;

        foreach (var serverSensor in serverSensors)
        {
            var localSensor = sensors.FirstOrDefault(x => x.Id == serverSensor.Id);
            if (localSensor is not null)
                localSensor.MessageId = serverSensor.LastMessageId;
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Could not read the latest MessageId values from the server: {ex.Message}");
    }
}

async Task BlockSensor(string sensorId)
{
    var sensor = sensors.FirstOrDefault(x => x.Id.Equals(sensorId, StringComparison.OrdinalIgnoreCase));
    if (sensor is null)
    {
        Console.WriteLine("Unknown sensor.");
        return;
    }

    sensor.IsActive = false;
    sensor.BlockedUntilUtc = DateTime.UtcNow.AddSeconds(30);
    try
    {
        await http.PostAsync($"{serverBaseUrl}/api/sensors/{sensor.Id}/block", null);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Server-side blocking failed: {ex.Message}");
    }

    Console.WriteLine($"{sensor.Id} was locally blocked for 30 seconds.");
}

async Task FloodSensor(SimulatedSensor sensor)
{
    Console.WriteLine($"{sensor.Id} is sending 12 rapid messages for the DoS/rate-limit test.");
    for (int i = 0; i < 12; i++)
        await SendMeasurement(sensor);
}

void PrintStatus()
{
    foreach (var sensor in sensors.OrderBy(x => x.Id))
    {
        string state = sensor.BlockedUntilUtc > DateTime.UtcNow
            ? "BLOCKED"
            : sensor.IsActive ? "ACTIVE" : "STANDBY";
        Console.WriteLine($"{sensor.Id}: {state}");
    }
}

static int GetAlarmPriority(SimulatedSensor sensor, double value)
{
    if (value >= sensor.AlarmLimit3) return 3;
    if (value >= sensor.AlarmLimit2) return 2;
    if (value >= sensor.AlarmLimit1) return 1;
    return 0;
}

static void WriteMeasurement(string sensorId, double value, int priority, bool accepted)
{
    string alarmLabel = priority switch
    {
        1 => "yellow",
        2 => "orange",
        3 => "red",
        _ => "none"
    };

    string message =
        $"[{DateTime.Now:HH:mm:ss}] {sensorId}: {value:F2} C, alarm={priority} ({alarmLabel}), server={(accepted ? "OK" : "REJECTED")}";

    AlarmConsole.WriteLine(priority, message);
}

public class SimulatedSensor
{
    public SimulatedSensor(
        string id,
        double minTemperature,
        double maxTemperature,
        DataQuality quality,
        double alarmLimit1,
        double alarmLimit2,
        double alarmLimit3,
        bool isActive,
        bool isMalicious,
        bool isUncertain)
    {
        Id = id;
        MinTemperature = minTemperature;
        MaxTemperature = maxTemperature;
        Quality = quality;
        AlarmLimit1 = alarmLimit1;
        AlarmLimit2 = alarmLimit2;
        AlarmLimit3 = alarmLimit3;
        IsActive = isActive;
        IsMalicious = isMalicious;
        IsUncertain = isUncertain;
        PrivateKey = RSA.Create(2048);
        PublicKeyPem = PrivateKey.ExportRSAPublicKeyPem();
    }

    public string Id { get; }
    public double MinTemperature { get; }
    public double MaxTemperature { get; }
    public DataQuality Quality { get; }
    public double AlarmLimit1 { get; }
    public double AlarmLimit2 { get; }
    public double AlarmLimit3 { get; }
    public bool IsMalicious { get; }
    public bool IsUncertain { get; }
    public bool IsActive { get; set; }
    public DateTime BlockedUntilUtc { get; set; }
    public long MessageId;
    public RSA PrivateKey { get; }
    public string PublicKeyPem { get; }

    public DataQuality CurrentQuality()
    {
        if (IsUncertain && Random.Shared.Next(0, 3) == 0)
            return DataQuality.UNCERTAIN;

        return Quality;
    }

    public double NextTemperature()
    {
        if (IsMalicious && Random.Shared.Next(0, 5) == 0)
            return Random.Shared.NextDouble() * 400 + 1_000;

        return MinTemperature + Random.Shared.NextDouble() * (MaxTemperature - MinTemperature);
    }
}
