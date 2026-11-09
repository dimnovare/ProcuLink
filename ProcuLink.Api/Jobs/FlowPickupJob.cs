using System.Text.Json;
using System.Text.Json.Nodes;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Api.Services.Documents;
using ProcuLink.Api.Services.Documents.Intake;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Catalog;
using ProcuLink.Core.Services.Ingress;
using ProcuLink.Core.Services.Security;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Jobs;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Infrastructure.Services.Ingress;

namespace ProcuLink.Api.Jobs;

/// <summary>
/// Hangfire job: one run of one flow intake's folder pickup. The claim-first ledger decides which files are new,
/// so a second run, an overlapping run or a retry never makes a second document. Remote files are never touched.
/// </summary>
public class FlowPickupJob(
    ProcuLinkDbContext db, IEnumerable<IPickupSource> sources, DeliveryEncryptionService encryption,
    IFlowDocumentSubmitter submitter, DocumentEngineGate gate, IBillingService billing, ILogger<FlowPickupJob> logger)
{
    public const int MaxFilesPerRun = 50;

    [Queue("polling")]
    [AutomaticRetry(Attempts = 0)]
    [PerOrgDistributedMutex(orgArgumentIndex: 0, timeoutSeconds: 300)]
    public async Task ExecuteAsync(Guid organisationId, Guid intakeId, CancellationToken ct)
    {
        if (!gate.IsEnabled(organisationId)) return;
        var intake = await db.FlowIntakes.FirstOrDefaultAsync(
            i => i.OrgId == organisationId && i.Id == intakeId && i.IsEnabled, ct);
        var source = intake is null ? null : sources.FirstOrDefault(s => s.Kind == intake.Kind);
        if (intake is null || source is null) return;

        try
        {
            var limit = await billing.CheckOrderLimitAsync(organisationId, ct);
            if (!limit.Allowed)
            {
                Finish(intake, "paused", limit.FlowPausedMessage);
                return;
            }

            var (status, message) = await PickUpAsync(intake, source, ct);
            Finish(intake, status, message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Pickup failed for intake {IntakeId}", intakeId);
            Finish(intake, "failed", ConnectionMessage(ex, intake) ?? "Couldn't pick up files. The next try is in 5 minutes.");
        }
        finally
        {
            intake.LastRunAt = DateTime.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
        }
    }

    /// <summary>The user-facing copy for a failure on the remote side or a saved credential that won't open, or null for anything else.</summary>
    private static string? ConnectionMessage(Exception ex, FlowIntake intake)
    {
        if (ex is CredentialUnbindableException) return "The saved password or key can't be read. Enter it again.";
        if (ex is not (SshHostKeyRejectedException or RemoteFailure)) return null;
        var config = JsonNode.Parse(intake.ConfigJson);
        var serviceUrl = config?["serviceUrl"]?.GetValue<string>();
        var host = config?["host"]?.GetValue<string>()
                   ?? (Uri.TryCreate(serviceUrl, UriKind.Absolute, out var uri) ? uri.Host : config?["bucket"]?.GetValue<string>());
        if (ex is SshHostKeyRejectedException)
            return $"Couldn't connect to {host}: the server's identity changed. Check with the server's owner, then clear the saved fingerprint.";
        var reason = ex.InnerException?.Message ?? "";
        return $"Couldn't connect to {host}: {(reason.Length > 200 ? reason[..200] : reason)}";
    }

    /// <summary>A manual "Test connection": opens and lists, never claims or downloads. A non-remote failure is not caught.</summary>
    public async Task<(bool Success, string Message)> TestAsync(Guid organisationId, Guid flowId, Guid intakeId, CancellationToken ct)
    {
        var intake = await db.FlowIntakes.FirstOrDefaultAsync(
            i => i.OrgId == organisationId && i.FlowId == flowId && i.Id == intakeId, ct) ?? throw new KeyNotFoundException();
        var source = sources.FirstOrDefault(s => s.Kind == intake.Kind)
                     ?? throw new IntakeException("invalid_intake", "Only folder, bucket and mailbox intakes can be tested.");
        try
        {
            using var session = await OpenAsync(intake, source, ct);
            var files = (await Remote(() => session.ListAsync(ct))).Where(IsReadable(intake.OrgId)).ToList();
            var known = (await LedgerAsync(intake, files, ct)).Count;
            return (true, $"Connected. {files.Count - known} file(s) waiting.");
        }
        catch (Exception ex) when (ConnectionMessage(ex, intake) is { } message)
        {
            return (false, message);
        }
    }

    /// <summary>This intake's ledger rows for the listed files, by stored key.</summary>
    private Task<Dictionary<string, FlowIntakeItem>> LedgerAsync(FlowIntake intake, IEnumerable<PickupFile> files, CancellationToken ct)
    {
        var keys = files.Select(f => IntakeLedger.KeyFor(f.Key)).ToList();
        return db.FlowIntakeItems.AsNoTracking()
            .Where(i => i.OrgId == intake.OrgId && i.ScopeId == intake.Id && keys.Contains(i.ItemKey))
            .ToDictionaryAsync(i => i.ItemKey, ct);
    }

    private Func<PickupFile, bool> IsReadable(Guid orgId)
    {
        var accepted = submitter.AcceptedFormats(orgId);
        return f => accepted.Contains(Path.GetExtension(f.FileName).TrimStart('.'), StringComparer.OrdinalIgnoreCase);
    }

    private async Task<IPickupSession> OpenAsync(FlowIntake intake, IPickupSource source, CancellationToken ct)
    {
        using var credentials = JsonDocument.Parse(intake.EncryptedCredentials is null
            ? "{}"
            : encryption.Decrypt(intake.EncryptedCredentials,
                new CredentialScope(intake.OrgId, CredentialPurpose.FlowIntakeCredentials, intake.Id)));
        return await Remote(() => source.OpenAsync(intake, credentials.RootElement, ct));
    }

    /// <summary>A failure on the remote side (connect, list, download, mark done); only these read as "couldn't connect".</summary>
    private sealed class RemoteFailure(Exception inner) : Exception(inner.Message, inner);

    private static async Task<T> Remote<T>(Func<Task<T>> call)
    {
        try { return await call(); }
        catch (Exception e) when (e is not (OperationCanceledException or SshHostKeyRejectedException or CatalogFileTooLargeException))
        {
            throw new RemoteFailure(e);
        }
    }

    private static void Finish(FlowIntake intake, string status, string message)
    {
        intake.LastStatus = status;
        intake.LastMessage = message;
    }

    private async Task<(string Status, string Message)> PickUpAsync(FlowIntake intake, IPickupSource source, CancellationToken ct)
    {
        using var session = await OpenAsync(intake, source, ct);

        int taken = 0, skipped = 0, claimed = 0;
        var files = (await Remote(() => session.ListAsync(ct))).Where(IsReadable(intake.OrgId)).ToList();
        var ledger = await LedgerAsync(intake, files, ct);
        foreach (var file in files)
        {
            if (claimed == MaxFilesPerRun) break;
            if (ledger.TryGetValue(IntakeLedger.KeyFor(file.Key), out var row))
            {
                // Already ingested (a document or a refusal): count it done so a mail's other files can finish it.
                if (row.DocumentId is not null)
                {
                    await Remote(async () => { await session.MarkDoneAsync(file, ct); return 0; });
                    continue;
                }

                // In flight, or a crashed claim not yet due for its retake: leave it, unmarked.
                if (row.CreatedAt >= DateTime.UtcNow - IntakeLedger.RetakeAfter) continue;
            }

            var claim = await IntakeLedger.ClaimAsync(db, intake.OrgId, intake.Id, file.Key, ct);
            if (claim is null) continue;
            claimed++;

            // A transient download or submit exception leaves the claim; it is retaken after 30 minutes.
            Guid documentId;
            try
            {
                using var content = await Remote(() => session.DownloadAsync(file, IngressLimits.MaxFileBytes, ct));
                var result = await submitter.SubmitAsync(intake.OrgId, intake.FlowId, content, file.FileName,
                    ContentTypeOf(file.FileName), intake.Kind, file.SourceRef, ct);
                if (result.Limit is { Allowed: false } refused)
                {
                    await IntakeLedger.ReleaseAsync(db, intake.OrgId, claim.Id, CancellationToken.None);
                    return ("paused", refused.FlowPausedMessage);
                }

                if (result.Error is "document_engine_not_enabled" or "flow_not_found")
                {
                    await IntakeLedger.ReleaseAsync(db, intake.OrgId, claim.Id, CancellationToken.None);
                    return ("failed", "Couldn't pick up files. The next try is in 5 minutes.");
                }

                documentId = result.Document?.Id ?? IntakeLedger.Refused;
                if (result.Document is not null) taken++;
            }
            catch (CatalogFileTooLargeException)
            {
                documentId = IntakeLedger.Refused;
                skipped++;
            }

            await db.FlowIntakeItems.Where(i => i.OrgId == intake.OrgId && i.Id == claim.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(i => i.DocumentId, documentId), CancellationToken.None);
            await Remote(async () => { await session.MarkDoneAsync(file, ct); return 0; });
        }

        var notes = await Remote(() => session.SettleAsync(ct)) ?? [];
        var message = taken > 0 ? $"Picked up {taken} new file(s)." : notes.Count > 0 ? "" : "No new files.";
        if (skipped > 0) message += $" Skipped {skipped} file(s) over {IngressLimits.MaxFileBytes / (1024 * 1024)} MB.";
        message = string.Join(" ", new[] { message }.Concat(notes).Where(m => m.Length > 0)).Trim();
        return ("ok", message);
    }

    private static string ContentTypeOf(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".pdf" => "application/pdf",
        ".csv" => "text/csv",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        _ => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
    };
}
