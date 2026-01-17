using System.Text.Json;
using ProcuLink.Core.Canonical;

namespace ProcuLink.Infrastructure.Repositories;

public class FileOrderRepository : IOrderRepository
{
    private readonly string _dataDirectory;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public FileOrderRepository(string dataDirectory)
    {
        _dataDirectory = dataDirectory;
        Directory.CreateDirectory(_dataDirectory);
    }

    public async Task SaveAsync(PurchaseOrder po, CancellationToken ct = default)
    {
        var filePath = GetFilePath(po.Id);
        var json = JsonSerializer.Serialize(po, JsonOptions);
        await File.WriteAllTextAsync(filePath, json, ct);
    }

    public async Task<PurchaseOrder?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var filePath = GetFilePath(id);
        if (!File.Exists(filePath))
            return null;

        var json = await File.ReadAllTextAsync(filePath, ct);
        return JsonSerializer.Deserialize<PurchaseOrder>(json, JsonOptions);
    }

    public async Task<IReadOnlyList<PurchaseOrder>> ListAsync(CancellationToken ct = default)
    {
        var orders = new List<PurchaseOrder>();

        if (!Directory.Exists(_dataDirectory))
            return orders;

        var files = Directory.GetFiles(_dataDirectory, "*.json");
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var json = await File.ReadAllTextAsync(file, ct);
                var po = JsonSerializer.Deserialize<PurchaseOrder>(json, JsonOptions);
                if (po != null)
                    orders.Add(po);
            }
            catch (JsonException)
            {
                // Skip malformed files
            }
        }

        return orders.OrderByDescending(o => o.OrderDate).ToList();
    }

    private string GetFilePath(Guid id) => Path.Combine(_dataDirectory, $"{id}.json");
}
