using Microsoft.EntityFrameworkCore;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;

namespace ProcuLink.Infrastructure.Services;

/// <summary>
/// Records and evaluates supplier order confirmations against purchase orders.
///
/// Matching/classification rules (v1, manual entry/upload):
///   • Each confirmation line is matched to an ordered <see cref="PurchaseOrderLineEntity"/>
///     by PurchaseOrderLineId when supplied, otherwise by LineNumber.
///   • A line is <c>rejected</c> if the source flags it rejected.
///   • A line is <c>changed</c> if confirmed qty, unit price, or delivery date differ from ordered.
///   • Otherwise the line is <c>confirmed</c>.
///   • Overall status: any rejection (line-level or an explicit Rejected payload status) ⇒ rejected;
///     else any changed line ⇒ needs_review; else accepted. A caller-supplied explicit status is
///     honoured when valid, except that a rejection always wins (safety).
/// </summary>
public sealed class OrderConfirmationService : IOrderConfirmationService
{
    private readonly ProcuLinkDbContext   _db;
    private readonly IFileStorageService  _storage;

    public OrderConfirmationService(ProcuLinkDbContext db, IFileStorageService storage)
    {
        _db      = db;
        _storage = storage;
    }

    public async Task<OrderConfirmationEntity> RecordAsync(
        Guid orgId, Guid purchaseOrderId, ParsedConfirmationData data, CancellationToken ct)
    {
        var order = await _db.PurchaseOrders
                             .Include(o => o.Lines)
                             .Where(o => o.OrgId == orgId && o.Id == purchaseOrderId)
                             .FirstOrDefaultAsync(ct)
                    ?? throw new InvalidOperationException(
                        $"Purchase order {purchaseOrderId} not found for organisation {orgId}.");

        var now = DateTime.UtcNow;

        var confirmation = new OrderConfirmationEntity
        {
            Id              = Guid.NewGuid(),
            OrgId           = orgId,
            PurchaseOrderId = order.Id,
            SupplierReference = data.SupplierReference,
            Source          = "manual_entry",
            ReceivedAt      = (data.ReceivedAt ?? now),
            Notes           = data.Notes,
            CreatedAt       = now,
            UpdatedAt       = now,
        };

        var sourceExplicitlyRejected =
            string.Equals(data.Status, OrderConfirmationStatus.Rejected, StringComparison.OrdinalIgnoreCase);

        var anyChanged  = false;
        var anyRejected = sourceExplicitlyRejected;

        foreach (var pl in data.Lines)
        {
            var ordered = MatchOrderedLine(order.Lines, pl);

            var orderedQty   = ordered?.Quantity  ?? 0m;
            var orderedPrice = ordered?.UnitPrice ?? 0m;
            // The ordered baseline for the date is the ORDERED LINE's delivery date.
            //
            // This read used to be hardcoded `null`, under a comment claiming "purchase-order lines
            // carry no per-line delivery date today". That was false at the time it was written:
            // PurchaseOrderLineEntity.DeliveryDate exists and three of the eleven line-producing
            // parsers populate it (CxmlOrderParser.cs:211, IDocOrders05Parser.cs:202,
            // OpenAiPdfOrderExtractor.cs:703). Because the baseline was always null, the date arm of
            // IsChanged could never be reached, so a supplier who confirmed the order but MOVED THE
            // DELIVERY DATE was recorded as an unchanged "accepted" — the single change a buyer most
            // needs to see, silently dropped.
            //
            // A null here still means "we never parsed an ordered date", NOT "the date changed":
            // IsChanged only compares when BOTH sides are present, so the eight parsers that do not
            // populate the field keep their current behaviour rather than reporting every confirmed
            // date as a change.
            DateOnly? orderedDate = ordered?.DeliveryDate;

            string state;
            if (pl.IsRejected)
            {
                state       = OrderConfirmationLineState.Rejected;
                anyRejected = true;
            }
            else if (IsChanged(orderedQty, orderedPrice, orderedDate, pl))
            {
                state      = OrderConfirmationLineState.Changed;
                anyChanged = true;
            }
            else
            {
                state = OrderConfirmationLineState.Confirmed;
            }

            confirmation.Lines.Add(new OrderConfirmationLineEntity
            {
                Id                  = Guid.NewGuid(),
                OrderConfirmationId = confirmation.Id,
                OrgId               = orgId,
                PurchaseOrderLineId = ordered?.Id ?? pl.PurchaseOrderLineId,
                LineNumber          = pl.LineNumber,
                BuyerItemCode       = ordered?.BuyerItemCode,
                SupplierItemCode    = ordered?.SupplierItemCode,
                OrderedQuantity     = orderedQty,
                OrderedUnitPrice    = orderedPrice,
                OrderedDeliveryDate = orderedDate,
                ConfirmedQuantity   = pl.ConfirmedQuantity,
                ConfirmedUnitPrice  = pl.ConfirmedUnitPrice,
                ConfirmedDeliveryDate = pl.ConfirmedDeliveryDate,
                State               = state,
                Note                = pl.Note,
            });
        }

        confirmation.Status = DeriveStatus(data.Status, anyChanged, anyRejected);

        _db.Set<OrderConfirmationEntity>().Add(confirmation);
        await _db.SaveChangesAsync(ct);
        return confirmation;
    }

    public async Task<OrderConfirmationEntity> RecordUploadStubAsync(
        Guid orgId, Guid purchaseOrderId, Stream fileStream,
        string fileName, string contentType, CancellationToken ct)
    {
        var orderExists = await _db.PurchaseOrders
                                   .Where(o => o.OrgId == orgId && o.Id == purchaseOrderId)
                                   .AnyAsync(ct);
        if (!orderExists)
            throw new InvalidOperationException(
                $"Purchase order {purchaseOrderId} not found for organisation {orgId}.");

        var key = $"order-confirmations/{orgId}/{Guid.NewGuid()}_{fileName}";
        await _storage.UploadAsync(fileStream, key, contentType, ct);

        var now = DateTime.UtcNow;
        var confirmation = new OrderConfirmationEntity
        {
            Id              = Guid.NewGuid(),
            OrgId           = orgId,
            PurchaseOrderId = purchaseOrderId,
            // An uploaded document has not been parsed into lines yet, so it always needs a human
            // to review and reconcile it. Parsing the file into structured lines is a later concern.
            Status          = OrderConfirmationStatus.NeedsReview,
            Source          = "manual_upload",
            SourceFileName  = fileName,
            SourceFileKey   = key,
            ReceivedAt      = now,
            CreatedAt       = now,
            UpdatedAt       = now,
        };

        _db.Set<OrderConfirmationEntity>().Add(confirmation);
        await _db.SaveChangesAsync(ct);
        return confirmation;
    }

    public async Task<OrderConfirmationEntity?> GetAsync(Guid orgId, Guid confirmationId, CancellationToken ct)
        => await _db.Set<OrderConfirmationEntity>()
                    .Include(c => c.Lines)
                    .Where(c => c.OrgId == orgId && c.Id == confirmationId)
                    .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<OrderConfirmationEntity>> ListForOrderAsync(
        Guid orgId, Guid purchaseOrderId, CancellationToken ct)
        => await _db.Set<OrderConfirmationEntity>()
                    .Include(c => c.Lines)
                    .Where(c => c.OrgId == orgId && c.PurchaseOrderId == purchaseOrderId)
                    .OrderByDescending(c => c.ReceivedAt)
                    .ToListAsync(ct);

    public async Task<bool> IsOrderBlockedFromCompletionAsync(
        Guid orgId, Guid purchaseOrderId, CancellationToken ct)
        => await _db.Set<OrderConfirmationEntity>()
                    .Where(c => c.OrgId == orgId && c.PurchaseOrderId == purchaseOrderId)
                    .Where(c => c.Status == OrderConfirmationStatus.Rejected)
                    .AnyAsync(ct);

    // ── Matching / classification helpers ────────────────────────────────────────

    /// <summary>
    /// Match a confirmation line to its ordered line: prefer an explicit
    /// <see cref="ParsedConfirmationLineData.PurchaseOrderLineId"/>, otherwise fall back to
    /// the first ordered line with the same LineNumber.
    /// </summary>
    private static PurchaseOrderLineEntity? MatchOrderedLine(
        IReadOnlyList<PurchaseOrderLineEntity> orderedLines, ParsedConfirmationLineData pl)
    {
        if (pl.PurchaseOrderLineId is { } id)
        {
            var byId = orderedLines.FirstOrDefault(l => l.Id == id);
            if (byId is not null) return byId;
        }
        return orderedLines.FirstOrDefault(l => l.LineNumber == pl.LineNumber);
    }

    /// <summary>
    /// A line is "changed" when the supplier's confirmed quantity, unit price, or — when an
    /// ordered date baseline exists — delivery date differs from what was ordered.
    /// </summary>
    private static bool IsChanged(
        decimal orderedQty, decimal orderedPrice, DateOnly? orderedDate, ParsedConfirmationLineData pl)
    {
        if (orderedQty != pl.ConfirmedQuantity) return true;
        if (orderedPrice != pl.ConfirmedUnitPrice) return true;
        if (orderedDate is { } od && pl.ConfirmedDeliveryDate is { } cd && od != cd) return true;
        return false;
    }

    /// <summary>
    /// Derive the overall confirmation status. A rejection (line-level or explicit) always wins.
    /// A valid explicit non-rejection status from the source is honoured; otherwise the status is
    /// derived: any changed line ⇒ needs_review, else accepted.
    /// </summary>
    private static string DeriveStatus(string? explicitStatus, bool anyChanged, bool anyRejected)
    {
        if (anyRejected)
            return OrderConfirmationStatus.Rejected;

        if (OrderConfirmationStatus.IsValid(explicitStatus)
            && explicitStatus != OrderConfirmationStatus.Rejected)
            return explicitStatus!;

        return anyChanged
            ? OrderConfirmationStatus.NeedsReview
            : OrderConfirmationStatus.Accepted;
    }
}
