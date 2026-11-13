using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Security;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Core.Services.Security;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Infrastructure.Services.Google;
using ProcuLink.Infrastructure.Services.Security;

namespace ProcuLink.Api.Services.Documents;

public sealed record DestinationRequest(string Name, string Protocol, JsonElement Config, JsonElement? Credentials);

/// <summary><c>Code</c> is <c>unsupported_destination</c> or <c>invalid_destination</c> (whose message is the rule's).</summary>
public sealed class DestinationException(string code, string? detail = null) : Exception(detail ?? code)
{
    public string Code { get; } = code;
}

/// <summary>Org-level reusable destinations. Delivery itself is the existing dispatchers'; this only stores, validates and test-sends.</summary>
public sealed class FlowDestinationService(
    ProcuLinkDbContext db, DeliveryEncryptionService encryption, IEnumerable<IDeliveryDispatcher> dispatchers,
    DocumentEngineGate gate, IOptions<GoogleOptions> google)
{
    public const string TestFileName = "TEST_proculink-test.txt";
    public const string TestFileContent = "ProcuLink test send. This file is a test and contains no document data.\n";
    private const string Purpose = CredentialPurpose.FlowDestinationCredentials;

    private static readonly string[] Allowed =
        [DeliveryProtocolConstants.Http, DeliveryProtocolConstants.Sftp, DeliveryProtocolConstants.Ftps, DeliveryProtocolConstants.S3, DeliveryProtocolConstants.Email,
         DeliveryProtocolConstants.GoogleDrive, DeliveryProtocolConstants.GoogleSheets];

    public async Task<FlowDestination> SaveAsync(Guid orgId, Guid? id, DestinationRequest req, CancellationToken ct)
    {
        var protocol = req.Protocol?.Trim().ToLowerInvariant() ?? "";
        if (!Allowed.Contains(protocol)) throw new DestinationException("unsupported_destination");
        var isGoogle = protocol is DeliveryProtocolConstants.GoogleDrive or DeliveryProtocolConstants.GoogleSheets;
        // The flag gates creation (R2), not running: an existing destination keeps delivering once it is off.
        if (isGoogle && !(gate.NativeConnectorsOn(orgId) && google.Value.IsConfigured)) throw new DestinationException("unsupported_destination");
        if (string.IsNullOrWhiteSpace(req.Name) || req.Config.ValueKind != JsonValueKind.Object)
            throw new DestinationException("invalid_destination", "A name and a config object are required.");

        var existing = id is { } existingId
            ? await db.Set<FlowDestination>().FirstOrDefaultAsync(d => d.OrgId == orgId && d.Id == existingId, ct)
            : null;
        if (id is not null && existing is null) throw new KeyNotFoundException();

        var configJson = req.Config.GetRawText();
        var credentials = !isGoogle && req.Credentials is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } c ? c.GetRawText() : null;
        try
        {
            DeliveryConfigRules.ValidateConfigJson(configJson);
            DeliveryConfigRules.ValidateTransportSecurity(protocol, configJson);
            DeliveryConfigRules.ValidateCredentialTokenUrl(credentials);
            DeliveryConfigRules.ValidateCredentialHeaders(configJson, existing?.ConfigJson);
        }
        catch (ArgumentException ex)
        {
            throw new DestinationException("invalid_destination", ex.Message);
        }

        if (isGoogle) await ValidateGoogleAsync(orgId, protocol, req.Config, ct);

        // An S3 endpoint is a tenant-supplied URL outside the url-bearing protocols, so it is policed here (and again at dispatch).
        if (protocol == DeliveryProtocolConstants.S3 && req.Config.TryGetProperty("serviceUrl", out var endpoint)
            && endpoint.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(endpoint.GetString()))
        {
            var verdict = OutboundUrlPolicy.Inspect(endpoint.GetString(), "Endpoint URL");
            if (!verdict.Allowed) throw new DestinationException(verdict.ErrorCode!, verdict.Message);
        }

        var now = DateTime.UtcNow;
        if (existing is null)
        {
            existing = new FlowDestination { Id = Guid.NewGuid(), OrgId = orgId, CreatedAt = now };
            db.Set<FlowDestination>().Add(existing);
        }

        existing.Name = req.Name.Trim();
        existing.Protocol = protocol;
        existing.ConfigJson = DeliveryHostKeyConfig.PreserveRecordedFingerprints(configJson, existing.ConfigJson);
        existing.UpdatedAt = now;
        if (credentials is not null)
            existing.EncryptedCredentials = encryption.Encrypt(credentials, new CredentialScope(orgId, Purpose, existing.Id));

        await db.SaveChangesAsync(ct);
        return existing;
    }

    /// <summary>The account must be this org's Google account, and the picked folder or spreadsheet must be named.</summary>
    private async Task ValidateGoogleAsync(Guid orgId, string protocol, JsonElement config, CancellationToken ct)
    {
        var target = protocol == DeliveryProtocolConstants.GoogleDrive ? "folderId" : "spreadsheetId";
        string? Prop(string name) => config.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString() : null;

        if (!Guid.TryParse(Prop("accountId"), out var accountId)
            || !await db.Set<LinkedAccount>().AnyAsync(a => a.OrgId == orgId && a.Id == accountId && a.Provider == LinkedAccountProviders.Google, ct))
            throw new DestinationException("invalid_destination", "Choose a connected Google account.");
        if (Prop(target) is null)
            throw new DestinationException("invalid_destination", protocol == DeliveryProtocolConstants.GoogleDrive ? "Choose a Google Drive folder." : "Choose a Google spreadsheet.");
    }

    /// <summary>Null when not this org's; false while an output still sends to it.</summary>
    public async Task<bool?> DeleteAsync(Guid orgId, Guid id, CancellationToken ct)
    {
        var d = await db.Set<FlowDestination>().FirstOrDefaultAsync(x => x.OrgId == orgId && x.Id == id, ct);
        if (d is null) return null;
        if (await db.Set<FlowOutput>().AnyAsync(o => o.OrgId == orgId && o.DestinationId == id, ct)
            || (await FlowVersions.LiveVersionOutputsAsync(db, orgId, ct)).Any(o => o.DestinationId == id)) return false;
        db.Set<FlowDestination>().Remove(d);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public static SupplierDeliveryConfig ToDeliveryConfig(FlowDestination d, string flowName, bool test)
    {
        var json = JsonNode.Parse(d.ConfigJson) as JsonObject ?? new JsonObject();
        if (d.Protocol == DeliveryProtocolConstants.Email)
        {
            // The user's subject and message win; a blank or absent one falls back to a neutral default.
            json["subjectTemplate"] = (test ? "[TEST] " : "") + (Text(json, "subjectTemplate") ?? $"{flowName} — {{fileName}}");
            json["bodyTemplate"] = Text(json, "bodyTemplate") ?? "Attached: {fileName}. Sent by ProcuLink.";
            json.Remove("attachmentFileName"); // the delivery's own file name (or the TEST_ name) wins
        }
        else if (d.Protocol == DeliveryProtocolConstants.GoogleSheets)
            json["checkOnly"] = test; // a test send never appends rows (R11), whatever the stored config says
        else if (test && d.Protocol == DeliveryProtocolConstants.Http)
        {
            var headers = json["headers"] as JsonObject ?? new JsonObject();
            headers["X-ProcuLink-Test"] = "true";
            json["headers"] = headers;
        }

        return new SupplierDeliveryConfig
        {
            Id = d.Id, SupplierId = d.Id, OrgId = d.OrgId, Protocol = d.Protocol, AutoDeliver = true,
            ConfigJson = json.ToJsonString(),
        };
    }

    private static string? Text(JsonObject json, string key) =>
        json[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;

    /// <summary>Pins the host key a dispatcher learned on first contact into the destination's config. Does not save.</summary>
    public static void RecordLearnedHostKey(FlowDestination d, DeliveryResult result)
    {
        if (result.LearnedHostKeyFingerprint is not { } fingerprint) return;
        d.ConfigJson = DeliveryHostKeyConfig.Write(d.ConfigJson, [fingerprint]);
        d.UpdatedAt = DateTime.UtcNow;
    }

    public string DecryptCredentials(FlowDestination d) =>
        string.IsNullOrEmpty(d.EncryptedCredentials)
            ? ""
            : encryption.Decrypt(d.EncryptedCredentials, new CredentialScope(d.OrgId, Purpose, d.Id));

    /// <summary>Null when the destination is not this org's. The send is real; only its content is marked as a test (a given <paramref name="payload"/> goes as <c>TEST_</c> + its file name).</summary>
    public async Task<DeliveryResult?> TestAsync(Guid orgId, Guid id, CancellationToken ct, TestPayload? payload = null)
    {
        var d = await db.Set<FlowDestination>().FirstOrDefaultAsync(x => x.OrgId == orgId && x.Id == id, ct);
        if (d is null) return null;

        DeliveryResult result;
        var dispatcher = dispatchers.FirstOrDefault(x => x.Protocol == d.Protocol);
        if (dispatcher is null)
            result = new DeliveryResult(false, "No dispatcher registered for this destination.");
        else
            try
            {
                // Sheets only takes CSV, so a payload-less test sends the shared CSV sample (check-only: nothing is appended).
                payload ??= d.Protocol == DeliveryProtocolConstants.GoogleSheets
                    ? new TestPayload(Encoding.UTF8.GetBytes(DeliveryTestArtifact.Body), DeliveryTestArtifact.FileName, DeliveryTestArtifact.ContentType)
                    : null;
                result = await dispatcher.DispatchAsync(
                    payload?.Content ?? Encoding.UTF8.GetBytes(TestFileContent),
                    payload is null ? TestFileName : "TEST_" + payload.FileName, payload?.ContentType ?? "text/plain",
                    ToDeliveryConfig(d, d.Name, test: true), DecryptCredentials(d), ct, isTestFire: false);
            }
            catch (CredentialUnbindableException)
            {
                result = new DeliveryResult(false, "Destination credentials could not be decrypted.");
            }

        RecordLearnedHostKey(d, result);

        db.AuditEvents.Add(new AuditEvent
        {
            Id = Guid.NewGuid(), OrgId = orgId, EntityType = "FlowDestination", EntityId = d.Id, Action = "destination.tested",
            CreatedAt = DateTime.UtcNow,
            Payload = JsonSerializer.SerializeToDocument(new { success = result.Success, responseCode = result.ResponseCode }),
        });
        await db.SaveChangesAsync(ct);
        return result;
    }
}

public sealed record TestPayload(byte[] Content, string FileName, string ContentType);
