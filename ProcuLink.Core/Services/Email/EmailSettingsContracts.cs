using System.Text.Json;

namespace ProcuLink.Core.Services.Email;

public sealed record UpdateEmailSettingsRequest(
    bool Enabled,
    string Host,
    int Port,
    bool UseSsl,
    string Username,
    string? Password,
    string Folder,
    Guid? DefaultSupplierId);

public sealed record EmailSettingsResponse(
    bool Enabled,
    string Host,
    int Port,
    bool UseSsl,
    string Username,
    string Folder,
    Guid? DefaultSupplierId,
    bool HasPassword,
    string? PasswordDisplay,
    DateTime? LastPolledAt,
    DateTime? UpdatedAt);

public sealed record EmailPollingConfig(
    bool Enabled,
    string Host,
    int Port,
    bool UseSsl,
    string Username,
    string Folder,
    Guid? DefaultSupplierId,
    string? PasswordCiphertext,
    DateTime? LastPolledAt,
    DateTime? UpdatedAt)
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static EmailPollingConfig Empty => new(
        Enabled: false,
        Host: string.Empty,
        Port: 993,
        UseSsl: true,
        Username: string.Empty,
        Folder: "INBOX",
        DefaultSupplierId: null,
        PasswordCiphertext: null,
        LastPolledAt: null,
        UpdatedAt: null);

    public static EmailPollingConfig FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Trim() == "{}")
            return Empty;

        try
        {
            return JsonSerializer.Deserialize<EmailPollingConfig>(json, JsonOpts) ?? Empty;
        }
        catch (JsonException)
        {
            return Empty;
        }
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOpts);
}
