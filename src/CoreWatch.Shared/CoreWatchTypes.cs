using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace CoreWatch.Shared;

public enum DataQuality
{
    GOOD,
    BAD,
    UNCERTAIN
}

public class SensorConfig
{
    public string Id { get; set; } = "";
    public double MinTemperature { get; set; }
    public double MaxTemperature { get; set; }
    public DataQuality Quality { get; set; }
    public double AlarmLimit1 { get; set; }
    public double AlarmLimit2 { get; set; }
    public double AlarmLimit3 { get; set; }
    public bool IsActive { get; set; }
    public DateTime? LastMessageUtc { get; set; }
    public long LastMessageId { get; set; }
    public DateTime? BlockedUntilUtc { get; set; }
    public string? PublicKeyPem { get; set; }
}

public class Measurement
{
    public int Id { get; set; }
    public string SensorId { get; set; } = "";
    public double Value { get; set; }
    public DateTime TimestampUtc { get; set; }
    public int AlarmPriority { get; set; }
    public DataQuality Quality { get; set; }
    public bool IsConsensus { get; set; }
}

public class AlarmEvent
{
    public int Id { get; set; }
    public string SensorId { get; set; } = "";
    public double Value { get; set; }
    public int Priority { get; set; }
    public DateTime TimestampUtc { get; set; }
}

public class ConsensusValue
{
    public int Id { get; set; }
    public DateTime MinuteUtc { get; set; }
    public double Value { get; set; }
    public int UsedMeasurements { get; set; }
    public DateTime CreatedUtc { get; set; }
    public bool IsConsensus { get; set; } = true;
}

public class CoreWatchDbContext : DbContext
{
    public CoreWatchDbContext(DbContextOptions<CoreWatchDbContext> options) : base(options)
    {
    }

    public DbSet<SensorConfig> Sensors => Set<SensorConfig>();
    public DbSet<Measurement> Measurements => Set<Measurement>();
    public DbSet<AlarmEvent> Alarms => Set<AlarmEvent>();
    public DbSet<ConsensusValue> ConsensusValues => Set<ConsensusValue>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SensorConfig>().HasKey(x => x.Id);
        modelBuilder.Entity<Measurement>().HasIndex(x => x.TimestampUtc);
        modelBuilder.Entity<Measurement>().HasIndex(x => x.SensorId);
        modelBuilder.Entity<AlarmEvent>().HasIndex(x => x.TimestampUtc);
        modelBuilder.Entity<ConsensusValue>().HasIndex(x => x.MinuteUtc).IsUnique();
    }
}

public record SensorMeasurementPayload(
    string SensorId,
    double Value,
    DateTime TimestampUtc,
    DataQuality Quality,
    int AlarmPriority);

public record SecureEnvelope(
    string SensorId,
    long MessageId,
    DateTime TimestampUtc,
    string Iv,
    string EncryptedPayload,
    string Signature,
    string PublicKeyPem);

public record IngestResult(bool Accepted, string Message);

public record AlarmNotificationDto(string SensorId, double Value, int Priority, DateTime TimestampUtc);

public static class CryptoService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static SecureEnvelope CreateEnvelope(
        SensorMeasurementPayload payload,
        long messageId,
        RSA privateKey,
        string publicKeyPem,
        string aesKeyText)
    {
        byte[] key = BuildAesKey(aesKeyText);
        byte[] iv = RandomNumberGenerator.GetBytes(16);
        string json = JsonSerializer.Serialize(payload, JsonOptions);
        string encryptedPayload;

        using (var aes = Aes.Create())
        {
            aes.Key = key;
            aes.IV = iv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;

            using var encryptor = aes.CreateEncryptor();
            byte[] plainBytes = Encoding.UTF8.GetBytes(json);
            encryptedPayload = Convert.ToBase64String(
                encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length));
        }

        var envelope = new SecureEnvelope(
            payload.SensorId,
            messageId,
            DateTime.UtcNow,
            Convert.ToBase64String(iv),
            encryptedPayload,
            "",
            publicKeyPem);

        string signature = Convert.ToBase64String(privateKey.SignData(
            Encoding.UTF8.GetBytes(SignatureText(envelope)),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1));

        return envelope with { Signature = signature };
    }

    public static bool TryReadPayload(
        SecureEnvelope envelope,
        string aesKeyText,
        out SensorMeasurementPayload? payload,
        out string error)
    {
        payload = null;
        error = "";

        if (!VerifySignature(envelope))
        {
            error = "The digital signature is not valid.";
            return false;
        }

        try
        {
            byte[] key = BuildAesKey(aesKeyText);
            byte[] iv = Convert.FromBase64String(envelope.Iv);
            byte[] cipherBytes = Convert.FromBase64String(envelope.EncryptedPayload);

            using var aes = Aes.Create();
            aes.Key = key;
            aes.IV = iv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;

            using var decryptor = aes.CreateDecryptor();
            byte[] plainBytes = decryptor.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);
            payload = JsonSerializer.Deserialize<SensorMeasurementPayload>(
                Encoding.UTF8.GetString(plainBytes),
                JsonOptions);

            if (payload is null || payload.SensorId != envelope.SensorId)
            {
                error = "The message content does not match the sensor.";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = $"The message cannot be decrypted: {ex.Message}";
            return false;
        }
    }

    private static bool VerifySignature(SecureEnvelope envelope)
    {
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(envelope.PublicKeyPem);
            return rsa.VerifyData(
                Encoding.UTF8.GetBytes(SignatureText(envelope)),
                Convert.FromBase64String(envelope.Signature),
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
        }
        catch
        {
            return false;
        }
    }

    private static string SignatureText(SecureEnvelope envelope) =>
        $"{envelope.SensorId}|{envelope.MessageId}|{envelope.TimestampUtc:O}|{envelope.Iv}|{envelope.EncryptedPayload}";

    private static byte[] BuildAesKey(string text) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(text));
}
