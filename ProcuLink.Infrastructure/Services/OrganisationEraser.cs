using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Logging;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure.Tenancy;

namespace ProcuLink.Infrastructure.Services;

/// <summary>
/// Implements <see cref="IOrganisationEraser"/>. The table list is not written down here: it is
/// every mapped entity with an org column, found by <see cref="OrgQueryFilters.FindOrgPropertyName"/>
/// — the same test the query filters use — so a table added tomorrow is erased without editing this
/// file. <c>OrganisationEraserPostgresTests</c> seeds every one of them and proves it.
///
/// <para>Child tables with no org column of their own (purchase_order_lines, acceptance rules,
/// revision snapshots/test cases) go by their parent's <c>ON DELETE CASCADE</c>.</para>
/// </summary>
public sealed class OrganisationEraser(
    ProcuLinkDbContext db, IFileStorageService storage, ILogger<OrganisationEraser> logger,
    IBillingService? billing = null, ISignInAccountRemover? signIn = null) : IOrganisationEraser
{
    public const string AuditAction = "admin.organisation_erased";

    /// <summary>camelCase, so a stored receipt reads exactly like the one the endpoint returned.</summary>
    public static readonly JsonSerializerOptions ReceiptJson = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The one paid-workspace rule, shared with <c>DisposableOrgSweepJob</c>: any Stripe customer or
    /// subscription, ended or not (a missed webhook leaves the status stale), or any plan but Pilot
    /// (an Enterprise contract carries no Stripe ids at all).
    /// </summary>
    public static bool IsPaying(Organisation o) =>
        o.StripeCustomerId is not null || o.StripeSubscriptionId is not null || o.Plan != PlanConstants.Pilot;

    public Task<OrgEraseReceipt> EraseAsync(Guid orgId, string actor, string reason, CancellationToken ct) =>
        EraseCoreAsync(orgId, actor, reason, onRequest: false, agreementEnded: false, ct);

    public Task<OrgEraseReceipt> EraseOnRequestAsync(Guid orgId, string actor, string reason, bool agreementEnded, CancellationToken ct) =>
        EraseCoreAsync(orgId, actor, reason, onRequest: true, agreementEnded, ct);

    /// <summary>
    /// The sweep (<paramref name="onRequest"/> false) refuses every paying workspace and skips the
    /// sign-in step. On request a paying workspace goes only when the operator confirms the agreement
    /// ended, Stripe answers live that nothing runs (asked only when the workspace has a Stripe id — a
    /// manual contract has none, so the operator is the authority), and every overage row was invoiced.
    /// Every refusal is decided before anything is deleted, so a refusal changes nothing.
    /// </summary>
    private async Task<OrgEraseReceipt> EraseCoreAsync(
        Guid orgId, string actor, string reason, bool onRequest, bool agreementEnded, CancellationToken ct)
    {
        // The walk needs the org query filters inert (or armed to this org): a context scoped to
        // another tenant would see none of the target's rows and quietly erase nothing.
        if (db.ScopedOrganisationId is { } scoped && scoped != orgId)
            throw new InvalidOperationException($"OrganisationEraser needs an unscoped DbContext; this one is scoped to {scoped}.");

        var org = await db.Organisations.AsNoTracking().FirstOrDefaultAsync(o => o.Id == orgId, ct);
        var signInResult = new SignInRemoval(SignInSteps.NotRequested, 0, []);
        OrgEraseReceipt Receipt(OrgEraseOutcome outcome, Dictionary<string, int>? rows = null, int blobs = 0, List<string>? failed = null) =>
            new(outcome, orgId, org?.Slug, org?.ClerkOrgId, rows ?? [], blobs, failed ?? [], actor, reason, DateTime.UtcNow,
                signInResult.Step, signInResult.AccountsRemoved, signInResult.Failed);

        if (org is null) return Receipt(OrgEraseOutcome.NotFound);
        var paying = IsPaying(org);
        // Asked before the transaction and again inside it, so an overage row the billing job writes in
        // between is never deleted unchecked. Billed rows go with a paying workspace; a row never put on
        // an invoice is money still owed. An unpaid workspace with any overage row is billing evidence.
        async Task<OrgEraseOutcome?> OverageRefusal(CancellationToken token) => paying
            ? await db.OverageBillingRecords.AnyAsync(r => r.OrgId == orgId && (r.StripeInvoiceItemId == null || r.StripeInvoiceItemId == ""), token)
                ? OrgEraseOutcome.RefusedUnbilledOverage : null
            : await db.OverageBillingRecords.AnyAsync(r => r.OrgId == orgId, token)
                ? OrgEraseOutcome.RefusedBillingEvidence : null;
        if (paying)
        {
            if (!onRequest || !agreementEnded) return Receipt(OrgEraseOutcome.RefusedPaidSubscription);
            if (org.StripeCustomerId is not null || org.StripeSubscriptionId is not null)
            {
                var running = billing is null ? null
                    : await billing.HasRunningSubscriptionAsync(org.StripeCustomerId, org.StripeSubscriptionId, ct);
                if (running is null) return Receipt(OrgEraseOutcome.RefusedSubscriptionUnknown);
                if (running.Value) return Receipt(OrgEraseOutcome.RefusedRunningSubscription);
            }
        }
        if (await OverageRefusal(ct) is { } refused) return Receipt(refused);

        // The last point a cancellation may stop this. From the transaction on every call runs to
        // completion: a cancelled request must never leave half an erase, or a commit without cleanup.
        ct.ThrowIfCancellationRequested();
        var none = CancellationToken.None;
        var tables = Tables(db);
        await using var tx = await db.Database.BeginTransactionAsync(none);
        if (await OverageRefusal(none) is { } late)
        {
            await tx.RollbackAsync(none);
            return Receipt(late);
        }
        // Like the blob keys, the receipt committed with the rows says the sign-in accounts are not
        // confirmed gone until the step after cleanup reports what it did.
        if (onRequest) signInResult = new SignInRemoval(SignInSteps.Failed, 0, ["members"]);

        // Counted and keys collected BEFORE any delete: a cascade from one table can remove rows of
        // a later one, and those rows must still be reported and their blobs still found.
        var (rows, blobKeys) = await InspectAsync(db, tables, orgId, withKeys: true, none);
        var memberIds = await db.Memberships.Where(m => m.OrgId == orgId).Select(m => m.UserId).ToListAsync(none);

        foreach (var (type, orgProperty) in tables)
            await (Task<int>)Generic(nameof(DeleteRowsAsync), type).Invoke(null, [db, orgId, orgProperty])!;
        // A member's identity (it carries an email) goes too, once nothing else holds it. Nothing
        // writes users or memberships today (tenancy is Clerk's); this holds once something does.
        rows["users"] = await db.Users
            .Where(u => memberIds.Contains(u.Id) && !db.Memberships.Any(m => m.UserId == u.Id)
                && !db.AuditEvents.Any(a => a.UserId == u.Id))
            .ExecuteDeleteAsync(none);
        rows["organisations"] = await db.Organisations.Where(o => o.Id == orgId).ExecuteDeleteAsync(none);

        // The receipt lives in platform_audit_events (no org FK, so it survives) and commits WITH the
        // rows. Until cleanup confirms each blob gone it lists every one as failed, so a crash between
        // commit and cleanup still leaves an honest record of what may remain in storage.
        var keys = blobKeys.Distinct().ToList();
        var documents = $"{orgId}/documents/";
        var audit = new PlatformAuditEvent
        {
            Id = Guid.NewGuid(), Action = AuditAction, SubjectOrgId = orgId, SubjectClerkOrgId = org.ClerkOrgId,
            Actor = actor, Reason = reason, CreatedAt = DateTime.UtcNow,
        };
        void Record(OrgEraseReceipt r) => audit.Payload = JsonSerializer.Serialize(r, ReceiptJson);
        Record(Receipt(OrgEraseOutcome.Erased, rows, 0, [.. keys, documents]));
        db.PlatformAuditEvents.Add(audit);
        await db.SaveChangesAsync(none);
        await tx.CommitAsync(none);

        // Best-effort, like DataErasureService, and only after the commit: a storage failure never
        // keeps the rows, and is named in the receipt. A re-sent document leaves older outputs no row
        // names, so the documents folder is listed too; a folder that cannot be listed is named instead.
        var failed = new List<string>();
        var all = new HashSet<string>(keys);
        try { all.UnionWith(await storage.ListKeysAsync(documents, none)); }
        catch (Exception ex)
        {
            failed.Add(documents);
            logger.LogError(ex, "Erase org {OrgId}: could not list {Prefix}.", orgId, documents);
        }
        var blobs = 0;
        foreach (var key in all)
        {
            try { await storage.DeleteAsync(key, none); blobs++; }
            catch (Exception ex)
            {
                failed.Add(key);
                logger.LogError(ex, "Erase org {OrgId}: failed to delete blob {Key}.", orgId, key);
            }
        }
        // Sign-in accounts last, also best-effort: the workspace is gone whatever the provider answers.
        if (onRequest)
        {
            try
            {
                signInResult = signIn is null ? new SignInRemoval(SignInSteps.SkippedNoKey, 0, [])
                    : await signIn.RemoveAsync(org.ClerkOrgId, none);
            }
            catch (Exception ex)
            {
                signInResult = new SignInRemoval(SignInSteps.Failed, 0, ["members"]);
                logger.LogError(ex, "Erase org {OrgId}: the sign-in step threw.", orgId);
            }
        }
        var receipt = Receipt(OrgEraseOutcome.Erased, rows, blobs, failed);
        Record(receipt);
        await db.SaveChangesAsync(none);

        logger.LogWarning("ORG ERASE: {OrgId} ({Slug}) erased by {Actor} — {Rows} rows, {Blobs} blobs, {Failed} failed, sign-in {SignIn}.",
            orgId, org.Slug, actor, rows.Values.Sum(), receipt.BlobsDeleted, failed.Count, receipt.SignIn);
        return receipt;
    }

    /// <summary>
    /// Rows per org-scoped table for <paramref name="orgId"/>, by the same walk the erase uses —
    /// how <c>DisposableOrgSweepJob</c> tells an untouched workspace from a used one.
    /// </summary>
    public static async Task<Dictionary<string, int>> CountRowsAsync(ProcuLinkDbContext db, Guid orgId, CancellationToken ct) =>
        (await InspectAsync(db, Tables(db), orgId, withKeys: false, ct)).Rows;

    private static List<(IEntityType Type, string OrgProperty)> Tables(ProcuLinkDbContext db) =>
        db.Model.GetEntityTypes()
            .Select(t => (Type: t, OrgProperty: OrgQueryFilters.FindOrgPropertyName(t)!))
            .Where(x => x.OrgProperty is not null)
            .OrderByDescending(x => DeleteDepth(x.Type, []))
            .ToList();

    private static async Task<(Dictionary<string, int> Rows, List<string> Keys)> InspectAsync(
        ProcuLinkDbContext db, List<(IEntityType Type, string OrgProperty)> tables, Guid orgId, bool withKeys, CancellationToken ct)
    {
        var rows = new Dictionary<string, int>();
        var keys = new List<string>();
        foreach (var (type, orgProperty) in tables)
        {
            string[] keyProperties = withKeys
                ? type.GetProperties().Where(p => p.ClrType == typeof(string) && (p.Name.EndsWith("FileKey") || p.Name is "ArtifactKey" or "ReceiptKey")).Select(p => p.Name).ToArray()
                : [];
            var (count, found) = await (Task<(int, List<string>)>)Generic(nameof(InspectTableAsync), type)
                .Invoke(null, [db, orgId, orgProperty, keyProperties, ct])!;
            rows[type.GetTableName()!] = count;
            keys.AddRange(found);
        }
        // Revision test cases hold stored files but no org column: they go by their revision's
        // cascade, so their files are found through it.
        if (withKeys)
            foreach (var c in await db.Set<ConnectionRevisionTestCase>().Where(c => c.Revision.OrgId == orgId)
                         .Select(c => new { c.SampleSourceFileKey, c.ExpectedOutputKey }).ToListAsync(ct))
                keys.AddRange(new[] { c.SampleSourceFileKey, c.ExpectedOutputKey }.OfType<string>().Where(k => k != ""));
        return (rows, keys);
    }

    /// <summary>
    /// How far a type sits below the tenant root along its foreign keys. Deleting deepest-first
    /// removes every row before anything it references, so RESTRICT / NO ACTION never fire — even
    /// through a cascade (order_confirmation_lines → purchase_order_lines → purchase_orders). The one
    /// cycle in the model (supplier_connections.active_revision_id ⇄ supplier_connection_revisions
    /// .connection_id) is broken on its CASCADE side: deleting the connection takes the revision.
    /// Any other cycle throws, so the guard test fails loudly rather than the erase guessing.
    /// </summary>
    private static int DeleteDepth(IEntityType type, HashSet<IEntityType> path)
    {
        if (!path.Add(type))
            throw new InvalidOperationException(
                $"Foreign-key cycle through {type.GetTableName()}: teach OrganisationEraser.DeleteDepth to break it.");
        var depth = type.GetForeignKeys()
            .Where(fk => fk.PrincipalEntityType != type
                && !(fk.DeleteBehavior == DeleteBehavior.Cascade
                     && fk.PrincipalEntityType.GetForeignKeys().Any(back => back.PrincipalEntityType == type)))
            .Select(fk => 1 + DeleteDepth(fk.PrincipalEntityType, path))
            .DefaultIfEmpty(0).Max();
        path.Remove(type);
        return depth;
    }

    private static MethodInfo Generic(string name, IEntityType type) =>
        typeof(OrganisationEraser).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(type.ClrType);

    private static IQueryable<T> Owned<T>(ProcuLinkDbContext db, Guid orgId, string orgProperty) where T : class =>
        db.Set<T>().Where(e => EF.Property<Guid>(e, orgProperty) == orgId);

    private static async Task<(int, List<string>)> InspectTableAsync<T>(
        ProcuLinkDbContext db, Guid orgId, string orgProperty, string[] keyProperties, CancellationToken ct) where T : class
    {
        var keys = new List<string>();
        foreach (var p in keyProperties)
            keys.AddRange(await Owned<T>(db, orgId, orgProperty)
                .Select(e => EF.Property<string>(e, p)).Where(k => k != null && k != "").ToListAsync(ct));
        return (await Owned<T>(db, orgId, orgProperty).CountAsync(ct), keys);
    }

    private static Task<int> DeleteRowsAsync<T>(ProcuLinkDbContext db, Guid orgId, string orgProperty) where T : class =>
        Owned<T>(db, orgId, orgProperty).ExecuteDeleteAsync(CancellationToken.None);
}
