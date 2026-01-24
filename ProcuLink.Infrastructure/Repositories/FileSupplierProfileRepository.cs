using System.Text.Json;
using System.Text.RegularExpressions;
using ProcuLink.Core.Canonical;

namespace ProcuLink.Infrastructure.Repositories;

public class FileSupplierProfileRepository : ISupplierProfileRepository
{
    private readonly string _dataDirectory;
    private readonly SemaphoreSlim _lock = new(1, 1);

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

        var filePath = FindProfileFile(supplierName);
        if (filePath == null)
            return null;

        try
        {
            var json = await File.ReadAllTextAsync(filePath, ct);
            return JsonSerializer.Deserialize<SupplierProfile>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
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

    public async Task<SupplierProfile> UpsertAsync(SupplierProfile profile, CancellationToken ct = default)
    {
        if (profile == null)
            throw new ArgumentNullException(nameof(profile));

        if (string.IsNullOrWhiteSpace(profile.SupplierName))
            throw new ArgumentException("SupplierName is required.", nameof(profile));

        await _lock.WaitAsync(ct);
        try
        {
            // Delete any existing file with same name (case-insensitive)
            var existingFile = FindProfileFile(profile.SupplierName);
            if (existingFile != null)
            {
                File.Delete(existingFile);
            }

            // Write new file
            var safeName = NormalizeForFilename(profile.SupplierName);
            var filePath = Path.Combine(_dataDirectory, $"{safeName}.json");
            var json = JsonSerializer.Serialize(profile, JsonOptions);
            await File.WriteAllTextAsync(filePath, json, ct);

            return profile;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> DeleteAsync(string supplierName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(supplierName))
            return false;

        await _lock.WaitAsync(ct);
        try
        {
            var filePath = FindProfileFile(supplierName);
            if (filePath == null)
                return false;

            File.Delete(filePath);
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Find the profile file by name (case-insensitive)
    /// </summary>
    private string? FindProfileFile(string supplierName)
    {
        if (!Directory.Exists(_dataDirectory))
            return null;

        var files = Directory.GetFiles(_dataDirectory, "*.json");
        var normalizedName = NormalizeName(supplierName);

        foreach (var file in files)
        {
            var fileName = Path.GetFileNameWithoutExtension(file);
            if (NormalizeName(fileName).Equals(normalizedName, StringComparison.OrdinalIgnoreCase))
            {
                return file;
            }
        }

        return null;
    }

    /// <summary>
    /// Normalize name for comparison (remove spaces, dots, dashes, underscores)
    /// </summary>
    private static string NormalizeName(string name)
    {
        return name.Replace(" ", "").Replace(".", "").Replace("-", "").Replace("_", "");
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
}
