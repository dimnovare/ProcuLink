namespace ProcuLink.Core.Services;

/// <summary>
/// The one place that decides what a purchase order's PO number IS, and whether that value is
/// comparable to another order's.
///
/// <para><b>Why this type exists.</b> Before it, five call sites each inlined
/// <c>string.IsNullOrWhiteSpace(parsed.PoNumber) ? $"PO-{now:yyyyMMddHHmmss}" : parsed.PoNumber</c>.
/// That expression is two separate defects at once:</para>
/// <list type="number">
///   <item>The placeholder is <b>collision-prone by construction</b> — it is truncated to whole
///   seconds, so any two stubs created in the same UTC second, on any channel, in any org, get the
///   byte-identical string. Nothing downstream could tell them apart.</item>
///   <item>Nothing could tell a placeholder from a real PO number, so any duplicate detector built
///   on the column would flag colliding placeholders — two genuinely different documents — as
///   duplicates of each other. That false positive is worse than no detector.</item>
/// </list>
///
/// <para><b>The fix is two fields written together.</b> <see cref="Resolve"/> returns both the
/// stored display value and its normalized comparison key, so they cannot drift. The comparison key
/// is <c>null</c> for a placeholder, which is the single, well-defined meaning of a null
/// <c>po_number_normalized</c>: <b>this order carries no PO number a human ever asserted, so it
/// takes part in no duplicate comparison.</b> Null never means "unknown" or "not yet computed" —
/// the migration that added the column backfilled every historical row.</para>
///
/// <para><b>Normalization is deliberately narrow:</b> trim + upper-case, nothing else. It exists so
/// the same PO arriving on two channels that disagree about casing or padding still compares equal.
/// It does NOT strip punctuation or leading zeros — <c>PO-1001</c> and <c>PO1001</c> are different
/// supplier-facing identifiers, and folding them would manufacture duplicates that are not
/// duplicates. The stored <c>po_number</c> is never case-folded by this: it is what gets
/// transformed into the outgoing payload, and it keeps the casing it arrived with (it is trimmed,
/// which the operator-edit path already did).</para>
/// </summary>
public static class PoNumberIdentity
{
    /// <summary>
    /// Prefix every generated placeholder carries. Also the anchor of the regex the backfill
    /// migration used to recognise the OLD (pre-suffix) placeholders in historical rows.
    /// </summary>
    public const string PlaceholderPrefix = "PO-";

    /// <summary>
    /// Mints the stand-in PO number for a document that carried none.
    ///
    /// <para>The <paramref name="orderId"/> suffix is the whole point: the timestamp alone repeats
    /// within a second, and the order id is unique by construction, so two stubs created in the same
    /// second are now visibly different in the queue instead of appearing four times as the same
    /// PO. Six hex characters is enough to separate rows a human is reading side by side; the
    /// authority for "is this a placeholder" is the null comparison key, never this string's shape,
    /// so the suffix does not have to be collision-proof to be correct.</para>
    /// </summary>
    public static string MakePlaceholder(DateTime nowUtc, Guid orderId) =>
        $"{PlaceholderPrefix}{nowUtc:yyyyMMddHHmmss}-{orderId.ToString("N")[..6].ToUpperInvariant()}";

    /// <summary>
    /// The comparison key for a PO number a document or a human actually supplied, or <c>null</c>
    /// when there is nothing to compare (blank input). Trim + invariant upper-case; see the type
    /// doc for why nothing further is folded.
    /// </summary>
    public static string? Normalize(string? poNumber) =>
        string.IsNullOrWhiteSpace(poNumber) ? null : poNumber.Trim().ToUpperInvariant();

    /// <summary>
    /// Resolves a parsed/entered PO number into the pair that must always be written together:
    /// the value stored in <c>po_number</c> and the comparison key stored in
    /// <c>po_number_normalized</c>.
    ///
    /// <para>Blank input yields a freshly minted placeholder and a <c>null</c> key — the order is
    /// visible and unique in the queue but takes part in no duplicate comparison, because a
    /// placeholder is not a claim anyone made about the document.</para>
    /// </summary>
    /// <param name="suppliedPoNumber">PO number from the parsed document or an operator edit.</param>
    /// <param name="nowUtc">Timestamp used only when a placeholder must be minted.</param>
    /// <param name="orderId">The order's id — the uniqueness in a minted placeholder.</param>
    public static (string Value, string? Normalized) Resolve(
        string? suppliedPoNumber, DateTime nowUtc, Guid orderId)
    {
        if (string.IsNullOrWhiteSpace(suppliedPoNumber))
            return (MakePlaceholder(nowUtc, orderId), null);

        var trimmed = suppliedPoNumber.Trim();
        return (trimmed, Normalize(trimmed));
    }
}
