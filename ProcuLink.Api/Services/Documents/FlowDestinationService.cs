using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Security;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Core.Services.Microsoft;
using ProcuLink.Core.Services.Security;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Api.Services.Google;
using ProcuLink.Infrastructure.Services.Security;
using ProcuLink.Transform.Documents;

namespace ProcuLink.Api.Services.Documents;

public sealed record DestinationRequest(string Name, string Protocol, JsonElement Config, JsonElement? Credentials);

public sealed record DestinationUse(Guid FlowId, string FlowName, Guid OutputId, string OutputName);
/// <param name="Kind"><c>test</c> or <c>document</c>.</param>
public sealed record DestinationLastSend(string Kind, DateTime At, bool Ok);

/// <summary><c>Code</c> is <c>unsupported_destination</c> or <c>invalid_destination</c> (whose message is the rule's).</summary>
public sealed class DestinationException(string code, string? detail = null) : Exception(detail ?? code)
{
    public string Code { get; } = code;
}

/// <summary>Org-level reusable destinations. Delivery itself is the existing dispatchers'; this only stores, validates and test-sends.</summary>
public sealed class FlowDestinationService(
    ProcuLinkDbContext db, DeliveryEncryptionService encryption, IEnumerable<IDeliveryDispatcher> dispatchers,
    LinkedAccountAvailability availability, IMicrosoftDriveLinks links)
{
    public const string TestFileName = "TEST_proculink-test.txt";
    public const string TestFileContent = "ProcuLink test send. This file is a test and contains no document data.\n";
    private const string Purpose = CredentialPurpose.FlowDestinationCredentials;

    private static readonly string[] Allowed =
        [DeliveryProtocolConstants.Http, DeliveryProtocolConstants.Sftp, DeliveryProtocolConstants.Ftps, DeliveryProtocolConstants.S3, DeliveryProtocolConstants.Email,
         DeliveryProtocolConstants.GoogleDrive, DeliveryProtocolConstants.GoogleSheets, DeliveryProtocolConstants.MicrosoftOneDrive, DeliveryProtocolConstants.Ksef,
         DeliveryProtocolConstants.B2Brouter];

    /// <param name="userId">The person saving: recorded when a KSeF destination is switched to Production.</param>
    public async Task<FlowDestination> SaveAsync(Guid orgId, Guid? id, DestinationRequest req, CancellationToken ct, string? userId = null)
    {
        var protocol = req.Protocol?.Trim().ToLowerInvariant() ?? "";
        if (!Allowed.Contains(protocol)) throw new DestinationException("unsupported_destination");
        if (protocol == DeliveryProtocolConstants.Ksef && !availability.KsefOn(orgId)) throw new DestinationException("unsupported_destination");
        if (protocol == DeliveryProtocolConstants.B2Brouter && !availability.PeppolOn(orgId)) throw new DestinationException("unsupported_destination");
        var provider = LinkedAccountProviders.ByProtocol.GetValueOrDefault(protocol);
        var isLinked = provider is not null;
        // The flag gates creation (R2), not running: an existing destination keeps delivering once it is off.
        if (isLinked && !availability.On(orgId, provider!)) throw new DestinationException("unsupported_destination");
        if (string.IsNullOrWhiteSpace(req.Name) || req.Config.ValueKind != JsonValueKind.Object)
            throw new DestinationException("invalid_destination", "A name and a config object are required.");

        var existing = id is { } existingId
            ? await db.Set<FlowDestination>().FirstOrDefaultAsync(d => d.OrgId == orgId && d.Id == existingId, ct)
            : null;
        if (id is not null && existing is null) throw new KeyNotFoundException();

        var configJson = req.Config.GetRawText();
        var credentials = !isLinked && req.Credentials is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } c ? c.GetRawText() : null;
        if (protocol == DeliveryProtocolConstants.Ksef) configJson = KsefConfig(configJson, existing, credentials, userId);
        if (protocol == DeliveryProtocolConstants.B2Brouter) configJson = B2BrouterConfig(configJson, existing, credentials);
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

        if (provider == LinkedAccountProviders.Google) await ValidateGoogleAsync(orgId, protocol, req.Config, ct);
        // Resolved before the entity is touched: the token store may save on this context.
        if (provider == LinkedAccountProviders.Microsoft) configJson = await ResolveOneDriveAsync(orgId, existing, configJson, ct);

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

    /// <summary>
    /// A KSeF destination (plan 33): a known environment and a valid NIP, never defaulted. It starts on Test or Demo; Production is
    /// an explicit edit with the issuer acceptance ticked, and the server records who switched and when. Switching the NIP of a
    /// Production destination needs the acceptance again. The token is a credential, never config (config goes back to the browser).
    /// </summary>
    private static string KsefConfig(string configJson, FlowDestination? existing, string? credentials, string? userId)
    {
        var json = (JsonObject)JsonNode.Parse(configJson)!;
        // A destination changed to KSeF from another protocol is new to KSeF: it starts on Test or Demo and needs its own token.
        var before = existing?.Protocol == DeliveryProtocolConstants.Ksef ? JsonNode.Parse(existing.ConfigJson) as JsonObject : null;
        var environment = Text(json, "environment");
        if (environment is not ("test" or "demo" or "production"))
            throw new DestinationException("invalid_destination", "Choose the KSeF environment: Test, Demo or Production.");
        if (!PolishNip.IsValid(Text(json, "nip"))) throw new DestinationException("invalid_destination", "Enter a valid Polish NIP (10 digits).");
        json["nip"] = PolishNip.Normalize(Text(json, "nip"));
        if (before is null && credentials is null) throw new DestinationException("invalid_destination", "Paste a KSeF token.");
        var accepted = json["acceptedIssuerTerms"] is JsonValue a && a.TryGetValue<bool>(out var yes) && yes;
        foreach (var key in new[] { "acceptedIssuerTerms", "checkOnly", "productionConfirmedBy", "productionConfirmedAt" }) json.Remove(key);
        if (environment != "production") return json.ToJsonString();

        if (before is null)
            throw new DestinationException("invalid_destination", "A new KSeF destination starts on Test or Demo. Switch it to Production once you have checked a test invoice.");
        if (Text(before, "environment") == "production" && Text(before, "nip") == json["nip"]!.GetValue<string>() && !accepted)
        {
            json["productionConfirmedBy"] = before["productionConfirmedBy"]?.DeepClone();
            json["productionConfirmedAt"] = before["productionConfirmedAt"]?.DeepClone();
            return json.ToJsonString();
        }
        if (!accepted)
            throw new DestinationException("invalid_destination", "To switch to Production, confirm that your company is the issuer of every invoice sent from here.");
        json["productionConfirmedBy"] = userId;
        json["productionConfirmedAt"] = DateTime.UtcNow.ToString("O");
        return json.ToJsonString();
    }

    /// <summary>
    /// A B2Brouter destination (plan 35): an account ID and an API key (a credential, never config). Sandbox and live keys both
    /// save; <c>sandbox</c> is the server's, never the browser's: <c>true</c> when a saved key starts with <c>test_</c>,
    /// kept as stored when the key is not re-sent.
    /// </summary>
    private static string B2BrouterConfig(string configJson, FlowDestination? existing, string? credentials)
    {
        var json = (JsonObject)JsonNode.Parse(configJson)!;
        var account = Text(json, "account")?.Trim();
        if (account is null || account.Length > 64) throw new DestinationException("invalid_destination", "Enter your B2Brouter account ID.");
        string? key = null;
        try { if (credentials is not null) key = Text(JsonNode.Parse(credentials) as JsonObject ?? [], "apiKey"); } catch (System.Text.Json.JsonException) { }
        if (key is null && (credentials is not null || existing?.Protocol != DeliveryProtocolConstants.B2Brouter))
            throw new DestinationException("invalid_destination", "Enter your B2Brouter API key.");
        foreach (var k in new[] { "checkOnly", "sandbox" }) json.Remove(k);
        json["account"] = account;
        json["sandbox"] = key is not null ? key.StartsWith("test_", StringComparison.Ordinal) : JsonNode.Parse(existing!.ConfigJson)?["sandbox"]?.DeepClone();
        return json.ToJsonString();
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

    /// <summary>
    /// The account must be this org's Microsoft files account. A pasted <c>link</c> is resolved into the folder's ids and
    /// dropped; without one, the stored folder is kept only for the same account on a OneDrive destination. Sent ids are ignored.
    /// </summary>
    private async Task<string> ResolveOneDriveAsync(Guid orgId, FlowDestination? existing, string configJson, CancellationToken ct)
    {
        var json = (JsonObject)JsonNode.Parse(configJson)!;
        if (!Guid.TryParse(Text(json, "accountId"), out var accountId)
            || !await db.Set<LinkedAccount>().AnyAsync(a => a.OrgId == orgId && a.Id == accountId && a.Provider == LinkedAccountProviders.Microsoft, ct))
            throw new DestinationException("invalid_destination", "Choose a connected Microsoft account.");

        var link = Text(json, "link");
        json.Remove("link"); // a sharing link is a capability URL: never stored
        DriveItemRef target;
        if (link is not null)
            try { target = await links.ResolveAsync(orgId, accountId, link, ct); }
            catch (DriveLinkException ex) { throw ex.Unavailable ? new DestinationException("microsoft_unavailable") : new DestinationException("invalid_destination", ex.Message); }
        else if (existing?.Protocol == DeliveryProtocolConstants.MicrosoftOneDrive && JsonNode.Parse(existing.ConfigJson) is JsonObject old
                 && Guid.TryParse(Text(old, "accountId"), out var oldAccount) && oldAccount == accountId
                 && Text(old, "driveId") is { } driveId && Text(old, "itemId") is { } itemId)
            target = new DriveItemRef(driveId, itemId, Text(old, "itemName") ?? "");
        else
            throw new DestinationException("invalid_destination", "Paste the folder's link.");

        json["driveId"] = target.DriveId;
        json["itemId"] = target.ItemId;
        json["itemName"] = target.Name;
        return json.ToJsonString();
    }

    /// <summary>Null when not this org's; false while an output still sends to it.</summary>
    public async Task<bool?> DeleteAsync(Guid orgId, Guid id, CancellationToken ct)
    {
        var d = await db.Set<FlowDestination>().FirstOrDefaultAsync(x => x.OrgId == orgId && x.Id == id, ct);
        if (d is null) return null;
        if ((await UsesAsync(orgId, ct)).Contains(id)) return false;
        db.Set<FlowDestination>().Remove(d);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            return false; // an output saved after the check: the foreign key refused the delete
        }
        return true;
    }

    /// <summary>Every output that sends to one of this organisation's destinations: the editable outputs and the ones each flow's live version sends. One row per output.</summary>
    public async Task<ILookup<Guid, DestinationUse>> UsesAsync(Guid orgId, CancellationToken ct)
    {
        var editable = await db.Set<FlowOutput>().AsNoTracking()
            .Where(o => o.OrgId == orgId && o.DestinationId != null)
            .Select(o => new { o.FlowId, o.Id, o.Name, Destination = o.DestinationId!.Value }).ToListAsync(ct);
        var live = (await FlowVersions.LiveVersionOutputsByFlowAsync(db, orgId, ct))
            .Where(x => x.Output.DestinationId is not null)
            .Select(x => new { x.FlowId, x.Output.Id, x.Output.Name, Destination = x.Output.DestinationId!.Value });
        // Editable first, so an output that is both keeps its current name.
        var uses = editable.Concat(live).DistinctBy(u => (u.Destination, u.Id))
            .OrderBy(u => u.Name, StringComparer.Ordinal).ThenBy(u => u.Id).ToList();
        var flowIds = uses.Select(u => u.FlowId).Distinct().ToList();
        var names = flowIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await db.Flows.PersonFlows(orgId).AsNoTracking().Where(f => flowIds.Contains(f.Id))
                .Select(f => new { f.Id, f.Name }).ToDictionaryAsync(f => f.Id, f => f.Name, ct);
        return uses.ToLookup(u => u.Destination, u => new DestinationUse(u.FlowId, names.GetValueOrDefault(u.FlowId, ""), u.Id, u.Name));
    }

    /// <summary>For each destination: its newest finished delivery (delivered, or stopped for good) and, apart, its newest test.
    /// Kept apart so a test never hides how the last real send went.</summary>
    public async Task<IReadOnlyDictionary<Guid, (DestinationLastSend? Sent, DestinationLastSend? Tested)>> LastSendsAsync(Guid orgId, CancellationToken ct)
    {
        var rows = await db.Set<FlowDestination>().AsNoTracking().Where(d => d.OrgId == orgId).Select(d => new
        {
            d.Id,
            Sent = db.Set<FlowDelivery>()
                .Where(x => x.OrgId == orgId && x.DestinationId == d.Id && (x.Status == FlowDeliveryStatus.Delivered || x.Status == FlowDeliveryStatus.DeadLetter))
                .OrderByDescending(x => x.UpdatedAt).ThenByDescending(x => x.Id).Select(x => new { At = x.UpdatedAt, Ok = x.Status == FlowDeliveryStatus.Delivered }).FirstOrDefault(),
            Tested = db.AuditEvents
                .Where(e => e.OrgId == orgId && e.EntityType == "FlowDestination" && e.EntityId == d.Id && e.Action == "destination.tested")
                .OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.Id).Select(e => new { At = e.CreatedAt, e.Payload }).FirstOrDefault(),
        }).ToListAsync(ct);

        var last = new Dictionary<Guid, (DestinationLastSend?, DestinationLastSend?)>();
        foreach (var r in rows)
        {
            var sent = r.Sent is { } s ? new DestinationLastSend("document", s.At, s.Ok) : null;
            var tested = r.Tested is { } t
                ? new DestinationLastSend("test", t.At, t.Payload?.RootElement is { ValueKind: JsonValueKind.Object } p && p.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.True)
                : null;
            last[r.Id] = (sent, tested);
        }
        return last;
    }

    /// <summary>A KSeF destination's environment as saved ("test", "demo", "production"), or null.</summary>
    public static string? KsefEnvironment(FlowDestination d)
    {
        try { return JsonNode.Parse(d.ConfigJson)?["environment"] is JsonValue v && v.TryGetValue<string>(out var e) ? e : null; }
        catch (System.Text.Json.JsonException) { return null; }
    }

    /// <param name="documentLanguage">The document's language (<see cref="DocumentLanguage.Of"/>): an email's default message is in it,
    /// unless the destination names a <c>language</c>. Null (a test send): the destination's language, else English.</param>
    public static SupplierDeliveryConfig ToDeliveryConfig(FlowDestination d, string flowName, bool test, string? documentLanguage = null)
    {
        var json = JsonNode.Parse(d.ConfigJson) as JsonObject ?? new JsonObject();
        if (d.Protocol == DeliveryProtocolConstants.Email)
        {
            // The user's subject and message win; a blank or absent one falls back to a neutral default.
            var language = DocumentLanguage.Known(Text(json, "language")) ?? documentLanguage ?? DocumentLanguage.English;
            json["subjectTemplate"] = (test ? "[TEST] " : "") + (Text(json, "subjectTemplate") ?? $"{flowName} — {{fileName}}");
            json["bodyTemplate"] = Text(json, "bodyTemplate") ?? DocumentLanguage.Text(language, "Attached: {fileName}. Sent by ProcuLink.");
            json.Remove("attachmentFileName"); // the delivery's own file name (or the TEST_ name) wins
        }
        else if (d.Protocol is DeliveryProtocolConstants.GoogleSheets or DeliveryProtocolConstants.Ksef or DeliveryProtocolConstants.B2Brouter)
            json["checkOnly"] = test; // a test send never appends rows (R11) or issues an invoice (plans 33, 35), whatever the stored config says
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
            // The reason and the destination are kept so History can say which test failed and why, as the test itself did.
            Payload = JsonSerializer.SerializeToDocument(new { success = result.Success, responseCode = result.ResponseCode, error = result.Success ? null : result.ErrorMessage,
                destinationId = d.Id, destination = d.Name, protocol = d.Protocol }),
        });
        await db.SaveChangesAsync(ct);
        return result;
    }
}

public sealed record TestPayload(byte[] Content, string FileName, string ContentType);
