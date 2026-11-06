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
    ProcuLinkDbContext db, IFileStorageService storage, ILogger<OrganisationEraser> logger) : IOrganisationEraser
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

    public async Task<OrgEraseReceipt> EraseAsync(Guid orgId, string actor, string reason, CancellationToken ct)
    {
        // The walk needs the org query filters inert (or armed to this org): a context scoped to
        // another tenant would see none of the target's rows and quietly erase nothing.
        if (db.ScopedOrganisationId is { } scoped && scoped != orgId)
            throw new InvalidOperationException($"OrganisationEraser needs an unscoped DbContext; this one is scoped to {scoped}.");

        var org = await db.Organisations.AsNoTracking().FirstOrDefaultAsync(o => o.Id == orgId, ct);
        OrgEraseReceipt Receipt(OrgEraseOutcome outcome, Dictionary<string, int>? rows = null, int blobs = 0, List<string>? failed = null) =>
            new(outcome, orgId, org?.Slug, org?.ClerkOrgId, rows ?? [], blobs, failed ?? [], actor, reason, DateTime.UtcNow);

        if (org is null) return Receipt(OrgEraseOutcome.NotFound);
        if (IsPaying(org)) return Receipt(OrgEraseOutcome.RefusedPaidSubscription);
        if (await db.OverageBillingRecords.AnyAsync(r => r.OrgId == orgId, ct))
            return Receipt(OrgEraseOutcome.RefusedBillingEvidence);

        // The last point a cancellation may stop this. From the transaction on every call runs to
        // completion: a cancelled request must never leave half an erase, or a commit without cleanup.
        ct.ThrowIfCancellationRequested();
        var none = CancellationToken.None;
        var tables = Tables(db);
        await using var tx = await db.Database.BeginTransactionAsync(none);

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
        var audit = new PlatformAuditEvent
        {
            Id = Guid.NewGuid(), Action = AuditAction, SubjectOrgId = orgId, SubjectClerkOrgId = org.ClerkOrgId,
            Actor = actor, Reason = reason, CreatedAt = DateTime.UtcNow,
        };
        void Record(OrgEraseReceipt r) => audit.Payload = JsonSerializer.Serialize(r, ReceiptJson);
        Record(Receipt(OrgEraseOutcome.Erased, rows, 0, keys));
        db.PlatformAuditEvents.Add(audit);
        await db.SaveChangesAsync(none);
        await tx.CommitAsync(none);

        // Best-effort, like DataErasureService, and only after the commit: a storage failure never
        // keeps the rows, and is named in the receipt.
        var failed = new List<string>();
        foreach (var key in keys)
        {
            try { await storage.DeleteAsync(key, none); }
            catch (Exception ex)
            {
                failed.Add(key);
                logger.LogError(ex, "Erase org {OrgId}: failed to delete blob {Key}.", orgId, key);
            }
        }
        var receipt = Receipt(OrgEraseOutcome.Erased, rows, keys.Count - failed.Count, failed);
        Record(receipt);
        await db.SaveChangesAsync(none);

        logger.LogWarning("ORG ERASE: {OrgId} ({Slug}) erased by {Actor} — {Rows} rows, {Blobs} blobs, {Failed} failed.",
            orgId, org.Slug, actor, rows.Values.Sum(), receipt.BlobsDeleted, failed.Count);
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
                ? type.GetProperties().Where(p => p.ClrType == typeof(string) && (p.Name.EndsWith("FileKey") || p.Name == "ArtifactKey")).Select(p => p.Name).ToArray()
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
