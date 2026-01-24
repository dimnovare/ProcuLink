using System.Text.Json;
using System.Text.RegularExpressions;
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
        var normalizedBuyerCode = NormalizeBuyerItemCode(buyerItemCode);
        if (string.IsNullOrEmpty(normalizedBuyerCode))
            return null;

        var data = await LoadMappingFileAsync(supplierName, ct);
        if (data == null)
            return null;

        var mapping = data.Mappings.FirstOrDefault(m =>
            NormalizeBuyerItemCode(m.BuyerItemCode).Equals(normalizedBuyerCode, StringComparison.OrdinalIgnoreCase));

        return mapping?.SupplierItemCode;
    }

    public async Task<IReadOnlyList<ItemCodeMapping>> ListAsync(string supplierName, CancellationToken ct = default)
    {
        var data = await LoadMappingFileAsync(supplierName, ct);
        if (data == null)
            return Array.Empty<ItemCodeMapping>();

        return data.Mappings
            .Select(m => new ItemCodeMapping(m.BuyerItemCode, m.SupplierItemCode, m.CreatedAtUtc))
            .OrderBy(m => m.BuyerItemCode, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<ItemCodeMapping> UpsertAsync(string supplierName, string buyerItemCode, string supplierItemCode, CancellationToken ct = default)
    {
        var normalizedBuyerCode = NormalizeBuyerItemCode(buyerItemCode);
        var trimmedSupplierCode = supplierItemCode?.Trim() ?? string.Empty;

        await _lock.WaitAsync(ct);
        try
        {
            var data = await LoadMappingFileAsync(supplierName, ct) ?? new MappingFile
            {
                SupplierName = supplierName,
                Mappings = new List<MappingEntry>()
            };

            // Find existing mapping (case-insensitive, trimmed)
            var existing = data.Mappings.FirstOrDefault(m =>
                NormalizeBuyerItemCode(m.BuyerItemCode).Equals(normalizedBuyerCode, StringComparison.OrdinalIgnoreCase));

            var now = DateTime.UtcNow;

            if (existing != null)
            {
                // Update existing
                existing.SupplierItemCode = trimmedSupplierCode;
                existing.CreatedAtUtc = now;
            }
            else
            {
                // Add new with normalized buyer code
                existing = new MappingEntry
                {
                    BuyerItemCode = normalizedBuyerCode,
                    SupplierItemCode = trimmedSupplierCode,
                    CreatedAtUtc = now
                };
                data.Mappings.Add(existing);
            }

            // Sort mappings by BuyerItemCode for readability
            data.Mappings = data.Mappings
                .OrderBy(m => m.BuyerItemCode, StringComparer.OrdinalIgnoreCase)
                .ToList();

            await SaveMappingFileAsync(supplierName, data, ct);

            return new ItemCodeMapping(existing.BuyerItemCode, existing.SupplierItemCode, existing.CreatedAtUtc);
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
        // Normalize supplier name for filename: lowercase, replace spaces with dashes, remove invalid chars
        var safeName = NormalizeForFilename(supplierName);
        return Path.Combine(_dataDirectory, $"{safeName}.json");
    }

    /// <summary>
    /// Normalize supplier name for filename safety: lowercase, spaces to dashes, remove invalid chars
    /// </summary>
    private static string NormalizeForFilename(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "unknown";

        // Replace spaces with dashes
        var normalized = name.Trim().Replace(" ", "-");

        // Remove invalid filename characters
        normalized = string.Join("", normalized.Split(Path.GetInvalidFileNameChars()));

        // Lowercase
        normalized = normalized.ToLowerInvariant();

        // Remove consecutive dashes
        normalized = Regex.Replace(normalized, "-+", "-");

        return string.IsNullOrEmpty(normalized) ? "unknown" : normalized;
    }

    /// <summary>
    /// Normalize buyer item code: trim whitespace
    /// </summary>
    private static string NormalizeBuyerItemCode(string? code)
    {
        return code?.Trim() ?? string.Empty;
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
