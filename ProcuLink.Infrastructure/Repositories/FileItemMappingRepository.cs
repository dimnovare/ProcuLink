using System.Text.Json;
using ProcuLink.Core.Canonical;

namespace ProcuLink.Infrastructure.Repositories;

public class FileItemMappingRepository : IItemMappingRepository
{
    private readonly string _dataDirectory;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public FileItemMappingRepository(string dataDirectory)
    {
        _dataDirectory = dataDirectory;
        Directory.CreateDirectory(_dataDirectory);
    }

    public async Task<string?> TryGetSupplierItemCodeAsync(string supplierName, string buyerItemCode, CancellationToken ct = default)
    {
        var data = await LoadMappingFileAsync(supplierName, ct);
        if (data == null)
            return null;

        var mapping = data.Mappings.FirstOrDefault(m =>
            m.BuyerItemCode.Equals(buyerItemCode, StringComparison.OrdinalIgnoreCase));

        return mapping?.SupplierItemCode;
    }

    public async Task<IReadOnlyList<ItemCodeMapping>> ListAsync(string supplierName, CancellationToken ct = default)
    {
        var data = await LoadMappingFileAsync(supplierName, ct);
        if (data == null)
            return Array.Empty<ItemCodeMapping>();

        return data.Mappings
            .Select(m => new ItemCodeMapping(m.BuyerItemCode, m.SupplierItemCode, m.CreatedAtUtc))
            .ToList();
    }

    public async Task UpsertAsync(string supplierName, string buyerItemCode, string supplierItemCode, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var data = await LoadMappingFileAsync(supplierName, ct) ?? new MappingFile
            {
                SupplierName = supplierName,
                Mappings = new List<MappingEntry>()
            };

            // Find existing mapping (case-insensitive)
            var existing = data.Mappings.FirstOrDefault(m =>
                m.BuyerItemCode.Equals(buyerItemCode, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                // Update existing
                existing.SupplierItemCode = supplierItemCode;
                existing.CreatedAtUtc = DateTime.UtcNow;
            }
            else
            {
                // Add new
                data.Mappings.Add(new MappingEntry
                {
                    BuyerItemCode = buyerItemCode,
                    SupplierItemCode = supplierItemCode,
                    CreatedAtUtc = DateTime.UtcNow
                });
            }

            await SaveMappingFileAsync(supplierName, data, ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<MappingFile?> LoadMappingFileAsync(string supplierName, CancellationToken ct)
    {
        var filePath = GetFilePath(supplierName);
        if (!File.Exists(filePath))
            return null;

        try
        {
            var json = await File.ReadAllTextAsync(filePath, ct);
            return JsonSerializer.Deserialize<MappingFile>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task SaveMappingFileAsync(string supplierName, MappingFile data, CancellationToken ct)
    {
        var filePath = GetFilePath(supplierName);
        var json = JsonSerializer.Serialize(data, JsonOptions);
        await File.WriteAllTextAsync(filePath, json, ct);
    }

    private string GetFilePath(string supplierName)
    {
        // Sanitize supplier name for filename
        var safeName = string.Join("_", supplierName.Split(Path.GetInvalidFileNameChars()));
        return Path.Combine(_dataDirectory, $"{safeName}.json");
    }

    // Internal classes for JSON serialization
    private class MappingFile
    {
        public string SupplierName { get; set; } = string.Empty;
        public List<MappingEntry> Mappings { get; set; } = new();
    }

    private class MappingEntry
    {
        public string BuyerItemCode { get; set; } = string.Empty;
        public string SupplierItemCode { get; set; } = string.Empty;
        public DateTime CreatedAtUtc { get; set; }
    }
}
