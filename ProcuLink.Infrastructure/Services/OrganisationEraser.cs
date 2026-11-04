using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Logging;
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

    // Stripe statuses after which a subscription can no longer charge. Anything else next to a
    // subscription id — including a missing or unknown status — refuses.
    private static readonly string?[] EndedSubscriptionStatuses = ["canceled", "incomplete_expired"];

    /// <summary>camelCase, so a stored receipt reads exactly like the one the endpoint returned.</summary>
    public static readonly JsonSerializerOptions ReceiptJson = new(JsonSerializerDefaults.Web);

    public async Task<OrgEraseReceipt> EraseAsync(Guid orgId, string actor, string reason, CancellationToken ct)
    {
        // The walk needs the org query filters inert (or armed to this org): a context scoped to
        // another tenant would see none of the target's rows and quietly erase nothing.
        if (db.ScopedOrganisationId is { } scoped && scoped != orgId)
            throw new InvalidOperationException($"OrganisationEraser needs an unscoped DbContext; this one is scoped to {scoped}.");

        var org = await db.Organisations.AsNoTracking().FirstOrDefaultAsync(o => o.Id == orgId, ct);
        OrgEraseReceipt Receipt(OrgEraseOutcome outcome, Dictionary<string, int>? rows = null, int blobs = 0) =>
            new(outcome, orgId, org?.Slug, rows ?? [], blobs, actor, reason, DateTime.UtcNow);

        if (org is null) return Receipt(OrgEraseOutcome.NotFound);
        if (org.StripeSubscriptionId is not null && !EndedSubscriptionStatuses.Contains(org.StripeSubscriptionStatus))
            return Receipt(OrgEraseOutcome.RefusedPaidSubscription);
        if (await db.OverageBillingRecords.AnyAsync(r => r.OrgId == orgId, ct))
            return Receipt(OrgEraseOutcome.RefusedBillingEvidence);

        var tables = db.Model.GetEntityTypes()
            .Select(t => (Type: t, OrgProperty: OrgQueryFilters.FindOrgPropertyName(t)!))
            .Where(x => x.OrgProperty is not null)
            .OrderByDescending(x => DeleteDepth(x.Type, []))
            .ToList();

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // Count and collect blob keys BEFORE any delete: a cascade from one table can remove rows of
        // a later one, and those rows must still be reported and their blobs still found.
        var rows = new Dictionary<string, int>();
        var blobKeys = new List<string>();
        foreach (var (type, orgProperty) in tables)
        {
            var keyProperties = type.GetProperties()
                .Where(p => p.ClrType == typeof(string) && p.Name.EndsWith("FileKey"))
                .Select(p => p.Name).ToArray();
            var (count, keys) = await (Task<(int, List<string>)>)Generic(nameof(InspectAsync), type)
                .Invoke(this, [orgId, orgProperty, keyProperties, ct])!;
            rows[type.GetTableName()!] = count;
            blobKeys.AddRange(keys);
        }

        foreach (var (type, orgProperty) in tables)
            await (Task<int>)Generic(nameof(DeleteRowsAsync), type).Invoke(this, [orgId, orgProperty, ct])!;
        rows["organisations"] = await db.Organisations.Where(o => o.Id == orgId).ExecuteDeleteAsync(ct);

        // Best-effort, like DataErasureService: a storage failure is logged and never keeps the rows.
        var blobs = 0;
        foreach (var key in blobKeys.Distinct())
        {
            try { await storage.DeleteAsync(key, ct); blobs++; }
            catch (Exception ex) { logger.LogError(ex, "Erase org {OrgId}: failed to delete blob {Key}.", orgId, key); }
        }

        // The receipt lives in platform_audit_events, which has no org FK, so it survives the erase.
        var receipt = Receipt(OrgEraseOutcome.Erased, rows, blobs);
        db.PlatformAuditEvents.Add(new PlatformAuditEvent
        {
            Id = Guid.NewGuid(), Action = AuditAction, SubjectOrgId = orgId, Actor = actor, Reason = reason,
            Payload = JsonSerializer.Serialize(receipt, ReceiptJson), CreatedAt = receipt.At,
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        logger.LogWarning("ORG ERASE: {OrgId} ({Slug}) erased by {Actor} — {Rows} rows, {Blobs} blobs.",
            orgId, org.Slug, actor, rows.Values.Sum(), blobs);
        return receipt;
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
        typeof(OrganisationEraser).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!
            .MakeGenericMethod(type.ClrType);

    private IQueryable<T> Owned<T>(Guid orgId, string orgProperty) where T : class =>
        db.Set<T>().Where(e => EF.Property<Guid>(e, orgProperty) == orgId);

    private async Task<(int, List<string>)> InspectAsync<T>(
        Guid orgId, string orgProperty, string[] keyProperties, CancellationToken ct) where T : class
    {
        var keys = new List<string>();
        foreach (var p in keyProperties)
            keys.AddRange(await Owned<T>(orgId, orgProperty)
                .Select(e => EF.Property<string>(e, p)).Where(k => k != null && k != "").ToListAsync(ct));
        return (await Owned<T>(orgId, orgProperty).CountAsync(ct), keys);
    }

    private Task<int> DeleteRowsAsync<T>(Guid orgId, string orgProperty, CancellationToken ct) where T : class =>
        Owned<T>(orgId, orgProperty).ExecuteDeleteAsync(ct);
}
