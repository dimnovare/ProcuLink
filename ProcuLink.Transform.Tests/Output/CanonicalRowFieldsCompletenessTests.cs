using System.Reflection;
using FluentAssertions;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Transform.Output;

namespace ProcuLink.Transform.Tests.Output;

/// <summary>
/// WP-14 REGRESSION GUARD — the most important artefact in the packet.
///
/// <para>The gap this closes: the set of canonical names a custom output could bind lived only
/// inside two dictionary literals in <see cref="MappedTransformService"/>. Columns were added to
/// <see cref="PurchaseOrderEntity"/> and <see cref="PurchaseOrderLineEntity"/> for years without
/// anyone widening those literals, so ShipTo*, BillTo*, Contact*, Incoterms, BuyerTaxId,
/// ManufacturerPartNumber, Unspsc, DiscountPercent and TaxAmount were unbindable. Nothing failed —
/// a rule naming an absent key simply resolved to nothing. Silence is why it lasted.</para>
///
/// <para>These tests make the silence impossible. Reflection walks BOTH entity types; every
/// property must be either a row key or an entry in <see cref="CanonicalRowFields"/>'s exclusion
/// dictionaries WITH A WRITTEN REASON. Adding a column and shipping is now a test failure until
/// someone decides, in writing, whether an output may bind it.</para>
///
/// <para><b>Asserting the difference (R6).</b> The declared registry and the built row are written
/// independently, and <see cref="Registry_And_HeaderRow_AgreeExactly"/> /
/// <see cref="Registry_And_LineRow_AgreeExactly"/> compare them as SETS in both directions. Adding
/// a name to only one side fails; deleting from only one side fails. Neither can drift alone.</para>
/// </summary>
public class CanonicalRowFieldsCompletenessTests
{
    // ── Fixtures ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// A row bag built with NO override and NO source tokens, so it contains only the canonical
    /// keys — no custom-field keys and no reserved <c>src::</c> keys to filter out.
    /// </summary>
    private static IReadOnlyDictionary<string, string> HeaderRow() =>
        MappedTransformService.BuildHeaderRow(MinimalOrder(), new OrderMappingOverride());

    private static IReadOnlyDictionary<string, string> LineRow()
    {
        var order = MinimalOrder();
        return MappedTransformService.BuildLineRow(
            order, new OrderMappingOverride(), order.Lines.Single());
    }

    private static PurchaseOrderEntity MinimalOrder() => new()
    {
        Id         = Guid.NewGuid(),
        PoNumber   = "PO-1",
        OrderDate  = new DateOnly(2026, 1, 1),
        Currency   = "EUR",
        Lines      = new List<PurchaseOrderLineEntity>
        {
            new()
            {
                LineNumber       = 1,
                BuyerItemCode    = "B-1",
                SupplierItemCode = "S-1",
                Quantity         = 1m,
                UnitPrice        = 1m,
            },
        },
    };

    /// <summary>
    /// Public readable instance properties, which is exactly what an operator sees as "a field on
    /// the order". Reflection rather than a hand-written list is the point: a new column shows up
    /// here the moment it is declared, with no second place to remember to update.
    /// </summary>
    private static IReadOnlyList<string> PropertyNamesOf<T>() =>
        typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetMethod is not null)
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

    // ── 1. COMPLETENESS: every business field is bindable or explicitly excluded ──

    [Fact]
    public void EveryHeaderEntityField_IsEitherBindable_OrCarriesAWrittenExclusion()
    {
        var row = HeaderRow();

        var uncovered = PropertyNamesOf<PurchaseOrderEntity>()
            .Where(name => !row.ContainsKey(name))
            .Where(name => !CanonicalRowFields.ExcludedHeaderFields.ContainsKey(name))
            .ToList();

        uncovered.Should().BeEmpty(
            "every field on PurchaseOrderEntity must either be bindable by a custom output (a key "
            + "in the header row bag) or appear in CanonicalRowFields.ExcludedHeaderFields with a "
            + "written reason. These have neither, so no operator can emit them and nobody decided "
            + "they shouldn't: {0}",
            string.Join(", ", uncovered));
    }

    [Fact]
    public void EveryLineEntityField_IsEitherBindable_OrCarriesAWrittenExclusion()
    {
        var row = LineRow();

        var uncovered = PropertyNamesOf<PurchaseOrderLineEntity>()
            .Where(name => !row.ContainsKey(name))
            .Where(name => !CanonicalRowFields.ExcludedLineFields.ContainsKey(name))
            .ToList();

        uncovered.Should().BeEmpty(
            "every field on PurchaseOrderLineEntity must either be bindable by a custom output (a "
            + "key in the line row bag) or appear in CanonicalRowFields.ExcludedLineFields with a "
            + "written reason. These have neither: {0}",
            string.Join(", ", uncovered));
    }

    // ── 2. The exclusion list must stay honest ───────────────────────────────────

    [Theory]
    [MemberData(nameof(AllExclusions))]
    public void EveryExclusion_CarriesASubstantiveWrittenReason(string scope, string field, string reason)
    {
        // A bare name would let "we forgot" masquerade as "we decided". A reason short enough to be
        // a placeholder ("internal", "n/a") is the same failure wearing a hat.
        reason.Should().NotBeNullOrWhiteSpace(
            "the {0}-scope exclusion for '{1}' must say WHY it is not bindable", scope, field);
        reason.Trim().Length.Should().BeGreaterThan(30,
            "the {0}-scope exclusion for '{1}' must give a real reason, not a placeholder — it read "
            + "'{2}'", scope, field, reason);
    }

    public static TheoryData<string, string, string> AllExclusions()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var (k, v) in CanonicalRowFields.ExcludedHeaderFields) data.Add("header", k, v);
        foreach (var (k, v) in CanonicalRowFields.ExcludedLineFields) data.Add("line", k, v);
        return data;
    }

    [Fact]
    public void EveryExclusion_NamesARealEntityProperty()
    {
        // A stale exclusion (property renamed or deleted) would silently keep covering a name that
        // no longer exists, while the NEW name goes uncovered. Fail on the stale entry too.
        var headerProps = PropertyNamesOf<PurchaseOrderEntity>().ToHashSet(StringComparer.Ordinal);
        var lineProps   = PropertyNamesOf<PurchaseOrderLineEntity>().ToHashSet(StringComparer.Ordinal);

        CanonicalRowFields.ExcludedHeaderFields.Keys
            .Where(k => !headerProps.Contains(k))
            .Should().BeEmpty("these header exclusions name properties PurchaseOrderEntity no longer has");

        CanonicalRowFields.ExcludedLineFields.Keys
            .Where(k => !lineProps.Contains(k))
            .Should().BeEmpty("these line exclusions name properties PurchaseOrderLineEntity no longer has");
    }

    [Fact]
    public void NoField_IsBothBindableAndExcluded()
    {
        var headerRow = HeaderRow();
        var lineRow   = LineRow();

        CanonicalRowFields.ExcludedHeaderFields.Keys
            .Where(headerRow.ContainsKey)
            .Should().BeEmpty("a field cannot be both emitted and documented as deliberately not emitted");

        // The line bag carries header keys too, so only compare against the line-only names.
        var lineOnly = CanonicalRowFields.Line.ToHashSet(StringComparer.Ordinal);
        CanonicalRowFields.ExcludedLineFields.Keys
            .Where(k => lineOnly.Contains(k) && lineRow.ContainsKey(k))
            .Should().BeEmpty("a line field cannot be both emitted and documented as deliberately not emitted");
    }

    // ── 3. DIFFERENCE (R6): the declared registry and the built row must agree ────

    [Fact]
    public void Registry_And_HeaderRow_AgreeExactly()
    {
        var declared = CanonicalRowFields.Header.ToHashSet(StringComparer.Ordinal);
        var emitted  = HeaderRow().Keys.ToHashSet(StringComparer.Ordinal);

        declared.Except(emitted).Should().BeEmpty(
            "CanonicalRowFields.Header declares these names but BuildHeaderRow does not emit them — "
            + "the picker would offer a name that resolves to nothing");
        emitted.Except(declared).Should().BeEmpty(
            "BuildHeaderRow emits these keys but CanonicalRowFields.Header does not declare them — "
            + "the frontend picker (mirrored from this list) would never offer them");
    }

    [Fact]
    public void Registry_And_LineRow_AgreeExactly()
    {
        // A line bag = header keys + line keys, by design (line rules may reference order-level values).
        var declared = CanonicalRowFields.Header
            .Concat(CanonicalRowFields.Line)
            .ToHashSet(StringComparer.Ordinal);
        var emitted = LineRow().Keys.ToHashSet(StringComparer.Ordinal);

        declared.Except(emitted).Should().BeEmpty(
            "CanonicalRowFields declares these names but BuildLineRow does not emit them");
        emitted.Except(declared).Should().BeEmpty(
            "BuildLineRow emits these keys but CanonicalRowFields does not declare them");
    }

    [Fact]
    public void EveryDeclaredName_IsAnEntityPropertyOrADocumentedDerivedKey()
    {
        // Ties the registry back to the data model: a declared name that matches no property and is
        // not in DerivedLineKeys is a typo that would ship a dead entry into the frontend picker.
        var headerProps = PropertyNamesOf<PurchaseOrderEntity>().ToHashSet(StringComparer.Ordinal);
        var lineProps   = PropertyNamesOf<PurchaseOrderLineEntity>().ToHashSet(StringComparer.Ordinal);

        CanonicalRowFields.Header
            .Where(n => !headerProps.Contains(n))
            .Should().BeEmpty("every declared header name must be a PurchaseOrderEntity property");

        CanonicalRowFields.Line
            .Where(n => !lineProps.Contains(n) && !CanonicalRowFields.DerivedLineKeys.Contains(n))
            .Should().BeEmpty(
                "every declared line name must be a PurchaseOrderLineEntity property or a documented "
                + "derived key (CanonicalRowFields.DerivedLineKeys)");
    }

    [Fact]
    public void DeclaredNames_AreUnique()
    {
        CanonicalRowFields.Header.Should().OnlyHaveUniqueItems();
        CanonicalRowFields.Line.Should().OnlyHaveUniqueItems();
        CanonicalRowFields.Header.Intersect(CanonicalRowFields.Line, StringComparer.Ordinal)
            .Should().BeEmpty("a name must belong to exactly one scope so the picker can group it");
    }
}
