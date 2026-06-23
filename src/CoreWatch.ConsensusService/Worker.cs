using CoreWatch.Shared;
using Microsoft.EntityFrameworkCore;

namespace CoreWatch.ConsensusService;

public class Worker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<Worker> _logger;

    public Worker(IServiceScopeFactory scopeFactory, ILogger<Worker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                await CalculateConsensusAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error while calculating consensus.");
            }
        }
    }

    private async Task CalculateConsensusAsync(CancellationToken stoppingToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CoreWatchDbContext>();
        await db.Database.EnsureCreatedAsync(stoppingToken);

        DateTime end = DateTime.UtcNow;
        DateTime start = end.AddMinutes(-1);
        DateTime minute = new(start.Year, start.Month, start.Day, start.Hour, start.Minute, 0, DateTimeKind.Utc);

        if (await db.ConsensusValues.AnyAsync(x => x.MinuteUtc == minute, stoppingToken))
            return;

        var values = await db.Measurements
            .Where(x => !x.IsConsensus &&
                x.Quality == DataQuality.GOOD &&
                x.TimestampUtc >= start &&
                x.TimestampUtc < end)
            .ToListAsync(stoppingToken);

        if (values.Count == 0)
        {
            _logger.LogInformation("No GOOD measurements available for consensus in the last minute.");
            return;
        }

        var sensors = await db.Sensors.ToDictionaryAsync(x => x.Id, stoppingToken);
        var invalid = values
            .Where(x => sensors.TryGetValue(x.SensorId, out var sensor) &&
                (x.Value < sensor.MinTemperature - 100 || x.Value > sensor.MaxTemperature + 100))
            .ToList();

        foreach (var rejectedSensorId in invalid.Select(x => x.SensorId).Distinct())
        {
            if (sensors.TryGetValue(rejectedSensorId, out var sensor))
            {
                sensor.Quality = DataQuality.BAD;
                _logger.LogWarning("Sensor {SensorId} marked as BAD due to invalid data.", rejectedSensorId);
            }
        }

        var candidates = values.Except(invalid).ToList();
        if (candidates.Count == 0)
            return;

        var sorted = candidates.Select(x => x.Value).OrderBy(x => x).ToList();
        double median = sorted[sorted.Count / 2];
        double allowedDeviation = 100;
        var accepted = candidates
            .Where(x => Math.Abs(x.Value - median) <= allowedDeviation)
            .ToList();

        if (accepted.Count == 0)
            return;

        double consensus = accepted.Average(x => x.Value);
        db.ConsensusValues.Add(new ConsensusValue
        {
            MinuteUtc = minute,
            Value = consensus,
            UsedMeasurements = accepted.Count,
            CreatedUtc = DateTime.UtcNow,
            IsConsensus = true
        });

        db.Measurements.Add(new Measurement
        {
            SensorId = "CONSENSUS",
            Value = consensus,
            TimestampUtc = DateTime.UtcNow,
            AlarmPriority = 0,
            Quality = DataQuality.GOOD,
            IsConsensus = true
        });

        await db.SaveChangesAsync(stoppingToken);
        _logger.LogInformation("Consensus {Value:F2} calculated from {Count} measurements.", consensus, accepted.Count);
    }
}
