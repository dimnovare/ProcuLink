using System.Text.Json;
using ProcuLink.Core.Canonical;

namespace ProcuLink.Infrastructure.Repositories;

public class FileSupplierProfileRepository : ISupplierProfileRepository
{
    private readonly string _dataDirectory;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public FileSupplierProfileRepository(string dataDirectory)
    {
        _dataDirectory = dataDirectory;
        Directory.CreateDirectory(_dataDirectory);
    }

    public async Task<SupplierProfile?> GetByNameAsync(string supplierName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(supplierName))
            return null;

        if (!Directory.Exists(_dataDirectory))
            return null;

        // Case-insensitive file search
        var files = Directory.GetFiles(_dataDirectory, "*.json");
        var normalizedName = NormalizeName(supplierName);

        foreach (var file in files)
        {
            var fileName = Path.GetFileNameWithoutExtension(file);
            if (NormalizeName(fileName).Equals(normalizedName, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var json = await File.ReadAllTextAsync(file, ct);
                    return JsonSerializer.Deserialize<SupplierProfile>(json, JsonOptions);
                }
                catch (JsonException)
                {
                    // Skip malformed files
                }
            }
        }

        return null;
    }

    public async Task<IReadOnlyList<SupplierProfile>> ListAsync(CancellationToken ct = default)
    {
        var profiles = new List<SupplierProfile>();

        if (!Directory.Exists(_dataDirectory))
            return profiles;

        var files = Directory.GetFiles(_dataDirectory, "*.json");
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var json = await File.ReadAllTextAsync(file, ct);
                var profile = JsonSerializer.Deserialize<SupplierProfile>(json, JsonOptions);
                if (profile != null)
                    profiles.Add(profile);
            }
            catch (JsonException)
            {
                // Skip malformed files
            }
        }

        return profiles.OrderBy(p => p.SupplierName).ToList();
    }

    private static string NormalizeName(string name)
    {
        // Remove spaces, dots, dashes for comparison
        return name.Replace(" ", "").Replace(".", "").Replace("-", "").Replace("_", "");
    }
}
