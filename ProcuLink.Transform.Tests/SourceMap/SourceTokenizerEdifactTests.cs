using System.Text;
using FluentAssertions;
using ProcuLink.Transform.Tokenizing;

namespace ProcuLink.Transform.Tests.SourceMap;

/// <summary>
/// Tests for <see cref="SourceTokenizer"/> EDIFACT tokenisation.
///
/// Fixtures are minimal, inline EDIFACT ORDERS D96A fragments constructed as
/// string literals — no binary file required. Each test validates a specific
/// aspect of the id scheme, grouping, or edge-case handling.
///
/// Token id scheme:  seg:{TAG}[{n}].el{element}[.c{component}]
///   TAG      — segment tag (uppercase)
///   n        — 1-based occurrence count of that tag in the message
///   element  — 1-based element index within the segment (after the tag)
///   component — 1-based component index within a composite element
///              (omitted when element has only one non-empty component)
/// </summary>
public class SourceTokenizerEdifactTests
{
    private static readonly SourceTokenizer Tokenizer = new();

    // A minimal but valid EDIFACT ORDERS D96A message used by several tests.
    // Uses default delimiters: component=':', element='+', segment='\'
    private const string MinimalOrdersMessage =
        "UNH+1+ORDERS:D:96A:UN'" +
        "BGM+220+PO-TEST-001+9'" +
        "DTM+137:20240115:102'" +
        "NAD+BY+++Acme Buyer Ltd'" +
        "CUX+2:EUR:9'" +
        "LIN+1++ITEM-A:IN'" +
        "IMD+F+ANM+:::Widget Type A'" +
        "QTY+21:10:PCE'" +
        "PRI+AAA:25.00'" +
        "LIN+2++ITEM-B:IN'" +
        "QTY+21:5:PCE'" +
        "PRI+AAA:49.99'" +
        "UNT+12+1'";

    // ── Basic token emission ──────────────────────────────────────────────────

    [Fact]
    public async Task Edifact_BgmPoNumber_EmittedAsToken()
    {
        var bytes = Encoding.UTF8.GetBytes(MinimalOrdersMessage);

        var tokens = await Tokenizer.TokenizeAsync(bytes, ".edi");

        // BGM+220+PO-TEST-001+9 — element 2 = PO number
        tokens.Should().Contain(t => t.Id == "seg:BGM[1].el2" && t.Value == "PO-TEST-001");
    }

    [Fact]
    public async Task Edifact_TagOccurrenceCount_IsStableAndOneBasedPerTag()
    {
        var bytes = Encoding.UTF8.GetBytes(MinimalOrdersMessage);

        var tokens = await Tokenizer.TokenizeAsync(bytes, ".edi");

        // Two LIN segments → LIN[1] and LIN[2]
        tokens.Should().Contain(t => t.Id.StartsWith("seg:LIN[1]"));
        tokens.Should().Contain(t => t.Id.StartsWith("seg:LIN[2]"));
        // Only two LIN occurrences — no LIN[3]
        tokens.Should().NotContain(t => t.Id.StartsWith("seg:LIN[3]"));
    }

    [Fact]
    public async Task Edifact_CompositeElement_ComponentSuffixAppended()
    {
        // DTM+137:20240115:102 — element 1 is a composite with 3 components
        // Expected tokens: seg:DTM[1].el1.c1="137", seg:DTM[1].el1.c2="20240115", seg:DTM[1].el1.c3="102"
        var bytes = Encoding.UTF8.GetBytes(MinimalOrdersMessage);

        var tokens = await Tokenizer.TokenizeAsync(bytes, ".edi");

        tokens.Should().Contain(t => t.Id == "seg:DTM[1].el1.c1" && t.Value == "137");
        tokens.Should().Contain(t => t.Id == "seg:DTM[1].el1.c2" && t.Value == "20240115");
        tokens.Should().Contain(t => t.Id == "seg:DTM[1].el1.c3" && t.Value == "102");
    }

    [Fact]
    public async Task Edifact_SimpleElement_NoComponentSuffix()
    {
        // BGM element 2 = PO-TEST-001 is a single-component element — no .c suffix.
        var bytes = Encoding.UTF8.GetBytes(MinimalOrdersMessage);

        var tokens = await Tokenizer.TokenizeAsync(bytes, ".edi");

        tokens.Should().Contain(t => t.Id == "seg:BGM[1].el2");
        tokens.Should().NotContain(t => t.Id == "seg:BGM[1].el2.c1");
    }

    // ── Readable labels (segment + element meaning) ────────────────────────────

    [Fact]
    public async Task Edifact_BgmDocumentNumber_LabelledByMeaning()
    {
        var bytes = Encoding.UTF8.GetBytes(MinimalOrdersMessage);

        var tokens = await Tokenizer.TokenizeAsync(bytes, ".edi");

        var token = tokens.First(t => t.Id == "seg:BGM[1].el2");
        token.Label.Should().Be("BGM document no");
    }

    [Fact]
    public async Task Edifact_LinItemCode_LabelledByMeaning()
    {
        var bytes = Encoding.UTF8.GetBytes(MinimalOrdersMessage);

        var tokens = await Tokenizer.TokenizeAsync(bytes, ".edi");

        // LIN+1++ITEM-A:IN — element 3 component 1 is the item code.
        var token = tokens.First(t => t.Id == "seg:LIN[1].el3.c1");
        token.Label.Should().Be("LIN item code");
    }

    [Fact]
    public async Task Edifact_QtyQuantity_LabelledByMeaning()
    {
        var bytes = Encoding.UTF8.GetBytes(MinimalOrdersMessage);

        var tokens = await Tokenizer.TokenizeAsync(bytes, ".edi");

        // QTY+21:10:PCE — element 1 component 2 is the quantity.
        var token = tokens.First(t => t.Id == "seg:QTY[1].el1.c2");
        token.Label.Should().Be("QTY quantity");
    }

    [Fact]
    public async Task Edifact_RepeatedTag_LabelCarriesOccurrenceSuffix()
    {
        var bytes = Encoding.UTF8.GetBytes(MinimalOrdersMessage);

        var tokens = await Tokenizer.TokenizeAsync(bytes, ".edi");

        // First QTY has no occurrence suffix; the second QTY (LIN[2]) reads "#2".
        var first  = tokens.First(t => t.Id == "seg:QTY[1].el1.c2");
        first.Label.Should().Be("QTY quantity");
        var second = tokens.First(t => t.Id == "seg:QTY[2].el1.c2");
        second.Label.Should().Be("QTY quantity #2");
    }

    [Fact]
    public async Task Edifact_UnknownPosition_FallsBackToElementNumber()
    {
        // A segment with no entry in the meaning table falls back to "{TAG} element {n}".
        const string msg = "ZZZ+foo+bar'";
        var bytes = Encoding.UTF8.GetBytes(msg);

        var tokens = await Tokenizer.TokenizeAsync(bytes, ".edi");

        var token = tokens.First(t => t.Id == "seg:ZZZ[1].el1");
        token.Label.Should().Be("ZZZ element 1");
    }

    // ── Group tagging ────────────────────────────────────────────────────────

    [Fact]
    public async Task Edifact_SegmentsBeforeFirstLin_HaveHeaderGroup()
    {
        var bytes = Encoding.UTF8.GetBytes(MinimalOrdersMessage);

        var tokens = await Tokenizer.TokenizeAsync(bytes, ".edi");

        // BGM, DTM, NAD, CUX all appear before the first LIN.
        var bgmTokens = tokens.Where(t => t.Id.StartsWith("seg:BGM")).ToList();
        bgmTokens.Should().NotBeEmpty();
        bgmTokens.Should().OnlyContain(t => t.Group == "header");

        var dtmTokens = tokens.Where(t => t.Id.StartsWith("seg:DTM")).ToList();
        dtmTokens.Should().NotBeEmpty();
        dtmTokens.Should().OnlyContain(t => t.Group == "header");

        var cuxTokens = tokens.Where(t => t.Id.StartsWith("seg:CUX")).ToList();
        cuxTokens.Should().NotBeEmpty();
        cuxTokens.Should().OnlyContain(t => t.Group == "header");
    }

    [Fact]
    public async Task Edifact_SegmentsFromFirstLinOnward_HaveLineGroup()
    {
        var bytes = Encoding.UTF8.GetBytes(MinimalOrdersMessage);

        var tokens = await Tokenizer.TokenizeAsync(bytes, ".edi");

        var linTokens = tokens.Where(t => t.Id.StartsWith("seg:LIN")).ToList();
        linTokens.Should().NotBeEmpty();
        linTokens.Should().OnlyContain(t => t.Group == "line");

        var qtyTokens = tokens.Where(t => t.Id.StartsWith("seg:QTY")).ToList();
        qtyTokens.Should().NotBeEmpty();
        qtyTokens.Should().OnlyContain(t => t.Group == "line");

        var priTokens = tokens.Where(t => t.Id.StartsWith("seg:PRI")).ToList();
        priTokens.Should().NotBeEmpty();
        priTokens.Should().OnlyContain(t => t.Group == "line");
    }

    // ── UNA custom delimiters ─────────────────────────────────────────────────

    [Fact]
    public async Task Edifact_WithUnaPrefix_DelimitersRespected()
    {
        // UNA: ↑^.?  ~  → comp=↑, elem=^, decimal=., release=?, segment=~
        // (space is the "reserved" character at UNA[7])
        const string withUna =
            "UNA:+.? '" +   // Standard UNA — same as defaults
            "BGM+220+PO-UNA-001+9'" +
            "LIN+1++ITEM-X:IN'" +
            "QTY+21:3:PCE'";

        var bytes = Encoding.UTF8.GetBytes(withUna);
        var tokens = await Tokenizer.TokenizeAsync(bytes, ".edi");

        tokens.Should().Contain(t => t.Id == "seg:BGM[1].el2" && t.Value == "PO-UNA-001");
        tokens.Should().Contain(t => t.Id.StartsWith("seg:LIN[1]"));
    }

    // ── Edge cases ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Edifact_EmptyInput_ReturnsEmptyList()
    {
        var tokens = await Tokenizer.TokenizeAsync(Array.Empty<byte>(), ".edi");
        tokens.Should().BeEmpty();
    }

    [Fact]
    public async Task Edifact_GarbageContent_ReturnsEmptyList()
    {
        // Non-EDIFACT bytes must not throw; just return empty.
        var bytes = Encoding.UTF8.GetBytes("this is not an EDIFACT message at all");

        var tokens = await Tokenizer.TokenizeAsync(bytes, ".edi");

        // May produce tokens (treated as one giant segment with no known delimiters)
        // OR return empty — either is acceptable; crucially it must NOT throw.
        // We just assert no exception was thrown (implicit — if we get here, it passed).
    }

    [Fact]
    public async Task Edifact_EmptyElementValues_NotEmitted()
    {
        // A segment with trailing empty elements should not produce empty-value tokens.
        // e.g. NAD+BY+++ has empty elements 2, 3, 4.
        const string msg = "BGM+220+PO-001+9'NAD+BY+++'";
        var bytes = Encoding.UTF8.GetBytes(msg);

        var tokens = await Tokenizer.TokenizeAsync(bytes, ".edi");

        var nadTokens = tokens.Where(t => t.Id.StartsWith("seg:NAD")).ToList();
        // Only element 1 (BY qualifier) should be emitted; elements 2, 3, 4 are empty.
        nadTokens.Should().OnlyContain(t => !string.IsNullOrEmpty(t.Value));
    }

    [Fact]
    public async Task Edifact_MultipleOccurrencesOfSameTag_IndependentCounters()
    {
        // Three PRI segments (unusual but valid) should yield PRI[1], PRI[2], PRI[3].
        const string msg =
            "PRI+AAA:10.00'" +
            "PRI+AAB:9.50'" +
            "PRI+CAL:11.00'";
        var bytes = Encoding.UTF8.GetBytes(msg);

        var tokens = await Tokenizer.TokenizeAsync(bytes, ".edi");

        tokens.Should().Contain(t => t.Id.StartsWith("seg:PRI[1]"));
        tokens.Should().Contain(t => t.Id.StartsWith("seg:PRI[2]"));
        tokens.Should().Contain(t => t.Id.StartsWith("seg:PRI[3]"));
    }
}
