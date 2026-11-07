using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Api.Controllers;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Security;
using ProcuLink.Core.Services.Email;
using ProcuLink.Core.Services.Security;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;

namespace ProcuLink.Api.Services.Documents;

public sealed record IntakeRequest(string Kind, JsonElement Config, JsonElement? Credentials, bool? Enabled);

public sealed record FlowIntakeDto(
    Guid Id, string Kind, JsonElement Config, bool HasCredentials, bool IsEnabled, string? Address,
    DateTime? LastRunAt, string? LastStatus, string? LastMessage);

/// <summary><c>Code</c> is <c>unsupported_intake</c>, <c>invalid_intake</c> (422), <c>intake_exists</c> or <c>intake_required</c> (409).</summary>
public sealed class IntakeException(string code, string? detail = null) : Exception(detail ?? code)
{
    public string Code { get; } = code;
    public string? Detail { get; } = detail;
}

/// <summary>A flow's intakes: its own email address (always there), the file API's webhook, and server or mailbox pickups.</summary>
public sealed class FlowIntakeService(
    ProcuLinkDbContext db, DeliveryEncryptionService encryption, IInboundAddressService addresses, IConfiguration config)
{
    private const string Purpose = CredentialPurpose.FlowIntakeCredentials;

    // name, type (s string, p port, b bool, a string array), default
    private static readonly Dictionary<string, (string Name, char Type, object? Default)[]> Keys = new()
    {
        [IntakeKind.Imap] = [("host", 's', ""), ("port", 'p', 993), ("useSsl", 'b', true), ("username", 's', ""),
            ("folder", 's', "INBOX"), ("fromFilter", 's', ""), ("subjectFilter", 's', "")],
        [IntakeKind.Sftp] = [("host", 's', ""), ("port", 'p', 22), ("username", 's', ""), ("directory", 's', "/"), ("hostKeyFingerprints", 'a', null)],
        [IntakeKind.Ftp] = [("host", 's', ""), ("port", 'p', 21), ("username", 's', ""), ("directory", 's', "/")],
        [IntakeKind.Ftps] = [("host", 's', ""), ("port", 'p', 21), ("username", 's', ""), ("directory", 's', "/")],
        [IntakeKind.S3] = [("bucket", 's', ""), ("prefix", 's', ""), ("region", 's', "us-east-1"), ("serviceUrl", 's', ""), ("accessKeyId", 's', "")],
        [IntakeKind.Api] = [("webhookUrl", 's', "")],
    };

    private static readonly Dictionary<string, string[]> Required = new()
    {
        [IntakeKind.Imap] = ["host", "username"], [IntakeKind.Sftp] = ["host", "username"],
        [IntakeKind.Ftp] = ["host", "username"], [IntakeKind.Ftps] = ["host", "username"],
        [IntakeKind.S3] = ["bucket", "accessKeyId"],
    };

    private IQueryable<FlowIntake> Intakes(Guid orgId, Guid flowId) =>
        db.Set<FlowIntake>().Where(i => i.OrgId == orgId && i.FlowId == flowId);

    /// <summary>Creates the email intake and its address on first use, so a flow always has an address to show.</summary>
    public async Task<List<FlowIntakeDto>> ListAsync(Guid orgId, Guid flowId, CancellationToken ct)
    {
        var rows = await Intakes(orgId, flowId).OrderBy(i => i.CreatedAt).ToListAsync(ct);
        if (!rows.Any(i => i.Kind == IntakeKind.Email))
        {
            var minted = await addresses.MintForFlowAsync(orgId, flowId, ct);
            var now = DateTime.UtcNow;
            var email = new FlowIntake
            {
                Id = Guid.NewGuid(), OrgId = orgId, FlowId = flowId, Kind = IntakeKind.Email,
                ConfigJson = new JsonObject { ["addressId"] = minted.Id }.ToJsonString(), CreatedAt = now, UpdatedAt = now,
            };
            db.Set<FlowIntake>().Add(email);
            await db.SaveChangesAsync(ct);
            rows.Insert(0, email);
        }

        var dtos = new List<FlowIntakeDto>();
        foreach (var row in rows) dtos.Add(await ToDtoAsync(row, ct));
        return dtos;
    }

    public async Task<FlowIntakeDto> ToDtoAsync(FlowIntake i, CancellationToken ct) =>
        new(i.Id, i.Kind, JsonDocument.Parse(i.ConfigJson).RootElement.Clone(), !string.IsNullOrEmpty(i.EncryptedCredentials),
            i.IsEnabled, i.Kind == IntakeKind.Email ? await AddressOfAsync(i, ct) : null, i.LastRunAt, i.LastStatus, i.LastMessage);

    private async Task<string?> AddressOfAsync(FlowIntake i, CancellationToken ct)
    {
        var raw = JsonNode.Parse(i.ConfigJson)?["addressId"]?.GetValue<string>();
        if (!Guid.TryParse(raw, out var addressId)) return null;
        var row = await db.OrgInboundAddresses.AsNoTracking()
            .FirstOrDefaultAsync(a => a.OrganisationId == i.OrgId && a.Id == addressId && a.RevokedAt == null, ct);
        if (row is null || string.IsNullOrWhiteSpace(row.EncryptedToken)) return null;
        try
        {
            var token = encryption.Decrypt(row.EncryptedToken, CredentialScope.ForSupplier(i.OrgId, CredentialPurpose.OrgInboundEmailAddress, row.Id));
            return SettingsController.ComposeAddress(token, config["Inbound:Postmark:InboundDomain"], config["Inbound:Postmark:HostSuffix"]);
        }
        catch (CredentialUnbindableException)
        {
            return null;
        }
    }

    public async Task<FlowIntake> SaveAsync(Guid orgId, Guid flowId, Guid? id, IntakeRequest req, CancellationToken ct)
    {
        var kind = req.Kind?.Trim().ToLowerInvariant() ?? "";
        var existing = id is { } existingId ? await Intakes(orgId, flowId).FirstOrDefaultAsync(i => i.Id == existingId, ct) : null;
        if (id is not null && existing is null) throw new KeyNotFoundException();

        if (existing is null)
        {
            if (!Keys.ContainsKey(kind)) throw new IntakeException("unsupported_intake");
            if (kind == IntakeKind.Api && await Intakes(orgId, flowId).AnyAsync(i => i.Kind == IntakeKind.Api, ct))
                throw new IntakeException("intake_exists");
        }
        else if (kind != existing.Kind)
        {
            throw new IntakeException("invalid_intake", "An intake's kind cannot be changed.");
        }

        var configJson = kind == IntakeKind.Email ? null : Normalize(kind, req.Config, existing?.ConfigJson ?? "{}");
        var now = DateTime.UtcNow;
        if (existing is null)
        {
            existing = new FlowIntake { Id = Guid.NewGuid(), OrgId = orgId, FlowId = flowId, Kind = kind, CreatedAt = now };
            db.Set<FlowIntake>().Add(existing);
        }

        if (configJson is not null)
        {
            existing.ConfigJson = configJson;
            if (req.Credentials is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } c)
                existing.EncryptedCredentials = encryption.Encrypt(c.GetRawText(), new CredentialScope(orgId, Purpose, existing.Id));
        }

        if (req.Enabled is { } enabled) existing.IsEnabled = enabled;
        existing.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return existing;
    }

    /// <summary>Keeps the kind's known keys, fills defaults, falls back to the saved value for a key left out.</summary>
    private static string Normalize(string kind, JsonElement config, string savedJson)
    {
        if (config.ValueKind != JsonValueKind.Object) throw new IntakeException("invalid_intake", "A config object is required.");
        var saved = JsonNode.Parse(savedJson) as JsonObject ?? new JsonObject();
        var result = new JsonObject();
        foreach (var (name, type, def) in Keys[kind])
        {
            JsonNode? value = def is null ? new JsonArray() : JsonValue.Create(def);
            if (config.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null)
            {
                value = type switch
                {
                    's' when v.ValueKind == JsonValueKind.String => JsonValue.Create(v.GetString()!.Trim()),
                    'p' when v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var p) && p is >= 1 and <= 65535 => JsonValue.Create(p),
                    'b' when v.ValueKind is JsonValueKind.True or JsonValueKind.False => JsonValue.Create(v.GetBoolean()),
                    'a' when v.ValueKind == JsonValueKind.Array && v.EnumerateArray().All(e => e.ValueKind == JsonValueKind.String) =>
                        JsonNode.Parse(v.GetRawText()),
                    _ => throw new IntakeException("invalid_intake", type == 'p' ? $"{name} must be a port from 1 to 65535." : $"{name} is not valid."),
                };
            }
            else if (saved[name] is { } keep)
            {
                value = keep.DeepClone();
            }

            result[name] = value;
        }

        foreach (var key in Required.GetValueOrDefault(kind, []))
            if (string.IsNullOrWhiteSpace(result[key]?.GetValue<string>()))
                throw new IntakeException("invalid_intake", $"{key} is required.");

        if (kind == IntakeKind.Api && result["webhookUrl"]!.GetValue<string>() is { Length: > 0 } url)
        {
            var verdict = OutboundUrlPolicy.Inspect(url, "Webhook URL");
            if (!verdict.Allowed) throw new IntakeException("invalid_intake", verdict.Message);
        }

        return result.ToJsonString();
    }

    /// <summary>Null when not this flow's; the email intake cannot be deleted, only switched off.</summary>
    public async Task<bool?> DeleteAsync(Guid orgId, Guid flowId, Guid id, CancellationToken ct)
    {
        var row = await Intakes(orgId, flowId).FirstOrDefaultAsync(i => i.Id == id, ct);
        if (row is null) return null;
        if (row.Kind == IntakeKind.Email) throw new IntakeException("intake_required");
        db.Set<FlowIntake>().Remove(row);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Mints a new address and revokes the old one at once. Email intake only.</summary>
    public async Task<FlowIntake> NewAddressAsync(Guid orgId, Guid flowId, Guid id, CancellationToken ct)
    {
        var row = await Intakes(orgId, flowId).FirstOrDefaultAsync(i => i.Id == id, ct);
        if (row is null) throw new KeyNotFoundException();
        if (row.Kind != IntakeKind.Email) throw new IntakeException("invalid_intake", "Only the email intake has an address.");

        var old = JsonNode.Parse(row.ConfigJson)?["addressId"]?.GetValue<string>();
        var minted = await addresses.MintForFlowAsync(orgId, flowId, ct);
        if (Guid.TryParse(old, out var oldId)) await addresses.RevokeAsync(orgId, oldId, ct);
        row.ConfigJson = new JsonObject { ["addressId"] = minted.Id }.ToJsonString();
        row.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return row;
    }
}
