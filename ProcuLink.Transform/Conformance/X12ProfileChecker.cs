namespace ProcuLink.Transform.Conformance;

/// <summary>
/// Validates an ANSI ASC X12 850 Purchase Order interchange (the output of
/// <c>X12TransformService</c> / <c>X12ParsedOrderTransform</c>, version 004010)
/// against the named X12 850 profile. Pragmatic segment-structure + mandatory-
/// segment + element-presence + cardinality checks — not a full X12 implementation
/// guide validator.
///
/// <para>
/// Delimiters are recovered positionally from the fixed-width ISA segment (element
/// separator at index 3, component separator at ISA16 / index 104, segment
/// terminator at index 105), mirroring <c>X12OrderParser</c>; this is why the ISA
/// well-formedness check is the gate for the rest.
/// </para>
/// </summary>
internal sealed class X12ProfileChecker : IProfileChecker
{
    public StandardsProfile Profile => StandardsProfile.X12_850;

    public ConformanceReport Check(string documentText)
    {
        var b = new ConformanceCheckBuilder(Profile, "ANSI X12 850 Purchase Order", "004010");

        var text = documentText ?? string.Empty;

        // ── ISA envelope + positional delimiter discovery ──────────────────────
        var hasIsa = text.StartsWith("ISA", StringComparison.Ordinal) && text.Length >= 106;
        if (!hasIsa)
        {
            b.Add("x12.isa", false, "ISA (interchange header)",
                "Document does not begin with a valid fixed-width ISA segment (≥106 chars).");
            FailRemaining(b);
            return b.Build();
        }

        var elementSep   = text[3];
        var segmentSep   = text[105];
        b.Add("x12.isa", true, "ISA (interchange header)",
            $"Fixed-width ISA present; element separator '{elementSep}', segment terminator '{segmentSep}'.");

        // Split into segments on the discovered terminator.
        var segments = text
            .Split(segmentSep)
            .Select(s => s.Trim('\r', '\n'))
            .Where(s => s.Length > 0)
            .ToList();

        string[] ElementsOf(string segment) => segment.Split(elementSep);

        List<string[]> SegmentsWithTag(string tag) => segments
            .Select(ElementsOf)
            .Where(e => e.Length > 0 && string.Equals(e[0], tag, StringComparison.Ordinal))
            .ToList();

        // ── GS functional group header, GS01 = PO ──────────────────────────────
        var gs = SegmentsWithTag("GS").FirstOrDefault();
        b.Require("x12.gs", gs is not null, "GS (functional group header)",
            "GS functional group header present.", "Mandatory GS segment is missing.");
        b.Require("x12.gs.po", gs is not null && gs.Length > 1 && string.Equals(gs[1], "PO", StringComparison.Ordinal),
            "GS01 (=PO)", "GS01 functional identifier is PO (purchase order).",
            "GS01 must be 'PO' for an 850 functional group.");

        // ── ST*850 transaction set header ──────────────────────────────────────
        var st = SegmentsWithTag("ST").FirstOrDefault();
        var isSt850 = st is not null && st.Length > 1 && string.Equals(st[1], "850", StringComparison.Ordinal);
        b.Require("x12.st850", isSt850, "ST01 (=850)",
            "ST*850 transaction set header present.", "Mandatory ST*850 transaction set header is missing.");

        // ── BEG beginning segment + BEG03 PO number ────────────────────────────
        var beg = SegmentsWithTag("BEG").FirstOrDefault();
        b.Require("x12.beg", beg is not null, "BEG (beginning segment for purchase order)",
            "BEG segment present.", "Mandatory BEG segment is missing.");
        var poNumber = beg is not null && beg.Length > 3 ? beg[3] : null;
        b.Require("x12.beg.poNumber", !string.IsNullOrWhiteSpace(poNumber), "BEG03 (purchase order number)",
            "BEG03 purchase order number present.", "Mandatory BEG03 purchase order number is missing or empty.");

        // ── PO1 detail lines: ≥1, each carrying a buyer item-id qualifier pair ──
        var po1s = SegmentsWithTag("PO1");
        b.Require("x12.po1.cardinality", po1s.Count >= 1, "PO1 (baseline item data, 1..n)",
            $"{po1s.Count} PO1 detail line(s) present.", "At least one PO1 detail line is required.");

        // Each PO1 must carry a buyer product/service id qualifier (BP / IN / VP) followed by a value.
        var allPo1HaveItemId = po1s.Count > 0 && po1s.All(HasProductIdQualifier);
        b.Require("x12.po1.itemId", allPo1HaveItemId, "PO106..PO1nn product/service ID qualifier + value",
            "Every PO1 carries a product/service ID qualifier (BP/IN/VP) and value.",
            "A PO1 line is missing a product/service ID qualifier (BP/IN/VP) followed by a non-empty value.");

        // ── CTT line-item count must match the PO1 count ───────────────────────
        var ctt = SegmentsWithTag("CTT").FirstOrDefault();
        var cttValue = ctt is not null && ctt.Length > 1 ? ctt[1] : null;
        var cttMatches = int.TryParse(cttValue, out var cttCount) && cttCount == po1s.Count;
        b.Require("x12.ctt", cttMatches, "CTT01 (transaction totals — number of line items)",
            $"CTT01 line count ({cttValue}) matches the PO1 count ({po1s.Count}).",
            $"CTT01 ('{cttValue ?? "(missing)"}') must equal the number of PO1 lines ({po1s.Count}).");

        // ── Envelope trailers SE / GE / IEA ────────────────────────────────────
        b.Require("x12.se", SegmentsWithTag("SE").Count > 0, "SE (transaction set trailer)",
            "SE trailer present.", "Mandatory SE transaction set trailer is missing.");
        b.Require("x12.ge", SegmentsWithTag("GE").Count > 0, "GE (functional group trailer)",
            "GE trailer present.", "Mandatory GE functional group trailer is missing.");
        b.Require("x12.iea", SegmentsWithTag("IEA").Count > 0, "IEA (interchange control trailer)",
            "IEA trailer present.", "Mandatory IEA interchange control trailer is missing.");

        return b.Build();
    }

    /// <summary>
    /// True when a PO1 segment carries a buyer/vendor product-service-id qualifier
    /// (BP buyer's part, IN buyer's item number, or VP vendor's part) followed by a
    /// non-empty value somewhere in PO106..PO1nn (qualifier/value pairs).
    /// </summary>
    private static bool HasProductIdQualifier(string[] po1)
    {
        // Element index layout (after the PO1 tag at index 0):
        //   1 PO101 line no · 2 PO102 qty · 3 PO103 uom · 4 PO104 price ·
        //   5 PO105 basis-of-unit-price code (a single element, e.g. "PE") ·
        //   then repeating qualifier/value pairs starting at index 6 (PO106/PO107…).
        for (var i = 6; i + 1 < po1.Length; i += 2)
        {
            var qualifier = po1[i];
            var value = po1[i + 1];
            if ((qualifier is "BP" or "IN" or "VP" or "VN" or "UP")
                && !string.IsNullOrWhiteSpace(value))
                return true;
        }
        return false;
    }

    private static void FailRemaining(ConformanceCheckBuilder b)
    {
        foreach (var (code, @ref) in new[]
                 {
                     ("x12.gs", "GS (functional group header)"),
                     ("x12.gs.po", "GS01 (=PO)"),
                     ("x12.st850", "ST01 (=850)"),
                     ("x12.beg", "BEG"),
                     ("x12.beg.poNumber", "BEG03 (purchase order number)"),
                     ("x12.po1.cardinality", "PO1 (1..n)"),
                     ("x12.po1.itemId", "PO1 product/service ID qualifier"),
                     ("x12.ctt", "CTT01"),
                     ("x12.se", "SE (transaction set trailer)"),
                     ("x12.ge", "GE (functional group trailer)"),
                     ("x12.iea", "IEA (interchange control trailer)"),
                 })
            b.Add(code, false, @ref, "Not checked — no valid ISA envelope.");
    }
}
