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
    private static readonly string[] Extensions = [".pdf", ".csv", ".xlsx"];

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
            var host = JsonNode.Parse(intake.ConfigJson)?["host"]?.GetValue<string>();
            var reason = ex.InnerException?.Message ?? "";
            Finish(intake, "failed", ex switch
            {
                SshHostKeyRejectedException =>
                    $"Couldn't connect to {host}: the server's identity changed. Check with the server's owner, then clear the saved fingerprint.",
                RemoteFailure => $"Couldn't connect to {host}: {(reason.Length > 200 ? reason[..200] : reason)}",
                _ => "Couldn't pick up files. The next try is in 5 minutes.",
            });
        }
        finally
        {
            intake.LastRunAt = DateTime.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
        }
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
        using var credentials = JsonDocument.Parse(intake.EncryptedCredentials is null
            ? "{}"
            : encryption.Decrypt(intake.EncryptedCredentials,
                new CredentialScope(intake.OrgId, CredentialPurpose.FlowIntakeCredentials, intake.Id)));
        using var session = await Remote(() => source.OpenAsync(intake, credentials.RootElement, ct));

        int taken = 0, skipped = 0, claimed = 0;
        foreach (var file in (await Remote(() => session.ListAsync(ct))).Where(f => Extensions.Contains(Path.GetExtension(f.FileName), StringComparer.OrdinalIgnoreCase)))
        {
            if (claimed == MaxFilesPerRun) break;
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
                    await IntakeLedger.ReleaseAsync(db, intake.OrgId, claim.Id, ct);
                    return ("paused", refused.FlowPausedMessage);
                }

                if (result.Error is "document_engine_not_enabled" or "flow_not_found")
                {
                    await IntakeLedger.ReleaseAsync(db, intake.OrgId, claim.Id, ct);
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
                .ExecuteUpdateAsync(s => s.SetProperty(i => i.DocumentId, documentId), ct);
            await Remote(async () => { await session.MarkDoneAsync(file, ct); return 0; });
        }

        var message = taken == 0 ? "No new files." : $"Picked up {taken} new file(s).";
        if (skipped > 0) message += $" Skipped {skipped} file(s) over {IngressLimits.MaxFileBytes / (1024 * 1024)} MB.";
        return ("ok", message);
    }

    private static string ContentTypeOf(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".pdf" => "application/pdf",
        ".csv" => "text/csv",
        _ => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
    };
}
