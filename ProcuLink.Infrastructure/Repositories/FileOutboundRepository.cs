using System.Text.Json;
using ProcuLink.Core.Canonical;

namespace ProcuLink.Infrastructure.Repositories;

public class FileOutboundRepository : IOutboundRepository
{
    private readonly string _dataDirectory;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public FileOutboundRepository(string dataDirectory)
    {
        _dataDirectory = dataDirectory;
        Directory.CreateDirectory(_dataDirectory);
    }

    private string GetOrderDirectory(Guid orderId) => Path.Combine(_dataDirectory, orderId.ToString());

    public async Task SaveArtifactAsync(OutboundArtifact artifact, byte[] content, CancellationToken ct = default)
    {
        var orderDir = GetOrderDirectory(artifact.OrderId);
        Directory.CreateDirectory(orderDir);

        // Save the artifact file
        var filePath = Path.Combine(orderDir, artifact.FileName);
        await File.WriteAllBytesAsync(filePath, content, ct);

        // Save artifact metadata
        var metadataPath = Path.Combine(orderDir, "artifact.json");
        var json = JsonSerializer.Serialize(artifact, JsonOptions);
        await File.WriteAllTextAsync(metadataPath, json, ct);
    }

    public async Task<OutboundArtifact?> GetArtifactAsync(Guid orderId, CancellationToken ct = default)
    {
        var metadataPath = Path.Combine(GetOrderDirectory(orderId), "artifact.json");
        if (!File.Exists(metadataPath))
            return null;

        try
        {
            var json = await File.ReadAllTextAsync(metadataPath, ct);
            return JsonSerializer.Deserialize<OutboundArtifact>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task<byte[]?> GetArtifactContentAsync(Guid orderId, CancellationToken ct = default)
    {
        var artifact = await GetArtifactAsync(orderId, ct);
        if (artifact == null)
            return null;

        var filePath = Path.Combine(GetOrderDirectory(orderId), artifact.FileName);
        if (!File.Exists(filePath))
            return null;

        return await File.ReadAllBytesAsync(filePath, ct);
    }

    public async Task SaveDeliveryRecordAsync(DeliveryRecord record, CancellationToken ct = default)
    {
        var orderDir = GetOrderDirectory(record.OrderId);
        Directory.CreateDirectory(orderDir);

        var deliveryPath = Path.Combine(orderDir, "delivery.json");
        var json = JsonSerializer.Serialize(record, JsonOptions);
        await File.WriteAllTextAsync(deliveryPath, json, ct);
    }

    public async Task<DeliveryRecord?> GetDeliveryRecordAsync(Guid orderId, CancellationToken ct = default)
    {
        var deliveryPath = Path.Combine(GetOrderDirectory(orderId), "delivery.json");
        if (!File.Exists(deliveryPath))
            return null;

        try
        {
            var json = await File.ReadAllTextAsync(deliveryPath, ct);
            return JsonSerializer.Deserialize<DeliveryRecord>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
