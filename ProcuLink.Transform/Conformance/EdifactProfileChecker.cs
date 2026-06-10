namespace ProcuLink.Transform.Conformance;

/// <summary>
/// Validates a UN/EDIFACT ORDERS D.96A message (the output of
/// <c>EdifactParsedOrderTransform</c>) against the named EDIFACT ORDERS D.96A
/// profile. Pragmatic segment-structure + mandatory-segment + element-presence +
/// cardinality checks per ISO 9735 + the ORDERS D.96A message guideline — not a
/// full EDIFACT directory validator.
///
/// <para>
/// Default service delimiters (ISO 9735) are assumed unless a leading <c>UNA</c>
/// segment redefines them: component <c>:</c>, element <c>+</c>, release <c>?</c>,
/// segment <c>'</c>. The release character is honoured so an escaped delimiter
/// inside free text does not split a segment / element.
/// </para>
/// </summary>
internal sealed class EdifactProfileChecker : IProfileChecker
{
    public StandardsProfile Profile => StandardsProfile.EdifactOrdersD96A;

    public ConformanceReport Check(string documentText)
    {
        var b = new ConformanceCheckBuilder(Profile, "UN/EDIFACT ORDERS", "D.96A");

        var text = (documentText ?? string.Empty).Trim();

        // ── Delimiter discovery (UNA optional; otherwise ISO 9735 defaults) ────
        var component = ':';
        var element   = '+';
        var release   = '?';
        var segmentT  = '\'';

        if (text.StartsWith("UNA", StringComparison.Ordinal) && text.Length >= 9)
        {
            component = text[3];
            element   = text[4];
            release   = text[6];
            segmentT  = text[8];
            text = text[9..]; // strip the UNA so it is not treated as a body segment
        }

        // ── Split into segments honouring the release character ────────────────
        var segments = SplitTopLevel(text, segmentT, release)
            .Select(s => s.Trim('\r', '\n', ' '))
            .Where(s => s.Length > 0)
            .ToList();

        var anySegments = segments.Count > 0;
        b.Require("edifact.structure", anySegments, "ISO 9735 segment structure",
            $"{segments.Count} segment(s) parsed.", "No EDIFACT segments could be parsed.");

        if (!anySegments)
        {
            FailRemaining(b);
            return b.Build();
        }

        string Tag(string seg)
        {
            var firstElement = SplitTopLevel(seg, element, release).FirstOrDefault() ?? seg;
            return firstElement.Length >= 3 ? firstElement[..3] : firstElement;
        }

        List<string> SegmentsWithTag(string tag) =>
            segments.Where(s => string.Equals(Tag(s), tag, StringComparison.Ordinal)).ToList();

        string[] ElementsOf(string seg) => SplitTopLevel(seg, element, release).ToArray();

        // ── UNB interchange header ─────────────────────────────────────────────
        b.Require("edifact.unb", SegmentsWithTag("UNB").Count > 0, "UNB (interchange header)",
            "UNB interchange header present.", "Mandatory UNB interchange header is missing.");

        // ── UNH message header + ORDERS:D:96A:UN message identifier ────────────
        var unh = SegmentsWithTag("UNH").FirstOrDefault();
        b.Require("edifact.unh", unh is not null, "UNH (message header)",
            "UNH message header present.", "Mandatory UNH message header is missing.");

        var msgId = unh is not null && ElementsOf(unh).Length > 2 ? ElementsOf(unh)[2] : null;
        var msgComponents = msgId is null ? Array.Empty<string>() : msgId.Split(component);
        var isOrdersD96A =
            msgComponents.Length >= 3
            && string.Equals(msgComponents[0], "ORDERS", StringComparison.Ordinal)
            && string.Equals(msgComponents[1], "D", StringComparison.Ordinal)
            && string.Equals(msgComponents[2], "96A", StringComparison.Ordinal);
        b.Require("edifact.unh.messageType", isOrdersD96A, "UNH S009 (ORDERS:D:96A:UN)",
            "UNH message identifier is ORDERS:D:96A.",
            $"UNH message identifier must be ORDERS:D:96A but was '{msgId ?? "(missing)"}'.");

        // ── BGM beginning of message + 1004 document number ────────────────────
        var bgm = SegmentsWithTag("BGM").FirstOrDefault();
        b.Require("edifact.bgm", bgm is not null, "BGM (beginning of message)",
            "BGM segment present.", "Mandatory BGM segment is missing.");
        var bgmElements = bgm is null ? Array.Empty<string>() : ElementsOf(bgm);
        var docNumber = bgmElements.Length > 2 ? bgmElements[2] : null; // BGM C106/1004
        b.Require("edifact.bgm.documentNumber", !string.IsNullOrWhiteSpace(docNumber), "BGM C106/1004 (document number)",
            "BGM document number (1004) present.", "Mandatory BGM document number (C106/1004) is missing or empty.");

        // ── DTM date/time ──────────────────────────────────────────────────────
        b.Require("edifact.dtm", SegmentsWithTag("DTM").Count > 0, "DTM (date/time/period)",
            "DTM segment present.", "Mandatory DTM segment is missing.");

        // ── LIN line items: ≥1, each carrying an item number in C212 ───────────
        var lins = SegmentsWithTag("LIN");
        b.Require("edifact.lin.cardinality", lins.Count >= 1, "LIN (line item, 1..n)",
            $"{lins.Count} LIN line item(s) present.", "At least one LIN line item is required.");

        var allLinHaveItem = lins.Count > 0 && lins.All(lin =>
        {
            var els = ElementsOf(lin);
            // LIN  1082(line no) 1229(action) C212(item number composite)
            if (els.Length <= 3) return false;
            var c212 = els[3].Split(component);
            return c212.Length > 0 && !string.IsNullOrWhiteSpace(c212[0]);
        });
        b.Require("edifact.lin.itemNumber", allLinHaveItem, "LIN C212/7140 (item number)",
            "Every LIN carries an item number (C212/7140).",
            "A LIN line is missing the mandatory item number (C212/7140).");

        // ── UNT message trailer + UNZ interchange trailer ──────────────────────
        b.Require("edifact.unt", SegmentsWithTag("UNT").Count > 0, "UNT (message trailer)",
            "UNT message trailer present.", "Mandatory UNT message trailer is missing.");
        b.Require("edifact.unz", SegmentsWithTag("UNZ").Count > 0, "UNZ (interchange trailer)",
            "UNZ interchange trailer present.", "Mandatory UNZ interchange trailer is missing.");

        // ── UNT segment count must match the actual UNH..UNT segment count ─────
        var unt = SegmentsWithTag("UNT").FirstOrDefault();
        var untCountStr = unt is not null && ElementsOf(unt).Length > 1 ? ElementsOf(unt)[1] : null;
        var bodyCount = CountSegmentsBetween(segments, "UNH", "UNT", Tag); // inclusive
        var untMatches = int.TryParse(untCountStr, out var declared) && declared == bodyCount;
        b.Require("edifact.unt.count", untMatches, "UNT 0074 (number of segments in the message)",
            $"UNT segment count ({untCountStr}) matches the UNH..UNT count ({bodyCount}).",
            $"UNT 0074 ('{untCountStr ?? "(missing)"}') must equal the UNH..UNT segment count ({bodyCount}).",
            ConformanceSeverity.Warning);

        return b.Build();
    }

    /// <summary>Inclusive count of segments from the first <paramref name="startTag"/> to the first <paramref name="endTag"/>.</summary>
    private static int CountSegmentsBetween(List<string> segments, string startTag, string endTag, Func<string, string> tag)
    {
        var start = segments.FindIndex(s => string.Equals(tag(s), startTag, StringComparison.Ordinal));
        if (start < 0) return -1;
        var end = segments.FindIndex(start, s => string.Equals(tag(s), endTag, StringComparison.Ordinal));
        if (end < 0) return -1;
        return end - start + 1;
    }

    /// <summary>
    /// Splits <paramref name="value"/> on <paramref name="separator"/> while honouring the
    /// EDIFACT <paramref name="release"/> (escape) character — a separator immediately
    /// preceded by an unescaped release is treated as data, not a split point.
    /// </summary>
    private static IEnumerable<string> SplitTopLevel(string value, char separator, char release)
    {
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        var escaped = false;

        foreach (var c in value)
        {
            if (escaped)
            {
                current.Append(c);
                escaped = false;
                continue;
            }

            if (c == release)
            {
                current.Append(c);
                escaped = true;
                continue;
            }

            if (c == separator)
            {
                parts.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0)
            parts.Add(current.ToString());

        return parts;
    }

    private static void FailRemaining(ConformanceCheckBuilder b)
    {
        foreach (var (code, @ref) in new[]
                 {
                     ("edifact.unb", "UNB (interchange header)"),
                     ("edifact.unh", "UNH (message header)"),
                     ("edifact.unh.messageType", "UNH S009 (ORDERS:D:96A:UN)"),
                     ("edifact.bgm", "BGM (beginning of message)"),
                     ("edifact.bgm.documentNumber", "BGM C106/1004"),
                     ("edifact.dtm", "DTM (date/time/period)"),
                     ("edifact.lin.cardinality", "LIN (1..n)"),
                     ("edifact.lin.itemNumber", "LIN C212/7140"),
                     ("edifact.unt", "UNT (message trailer)"),
                     ("edifact.unz", "UNZ (interchange trailer)"),
                 })
            b.Add(code, false, @ref, "Not checked — no parseable segments.");
        b.Add("edifact.unt.count", false, "UNT 0074", "Not checked — no parseable segments.",
            ConformanceSeverity.Warning);
    }
}
