namespace ProcuLink.Core.Services.Mapping;

/// <summary>
/// THE single source of truth for what an <see cref="OutputNodeTemplate"/> can and cannot do, by
/// format. Everything that asks "will this designed layout actually produce a document?" — promote,
/// the transform's adoption ladder, replay, the preview — asks HERE, and
/// <c>OutputTemplateEmitter.Emit</c> DISPATCHES on the same answer.
///
/// <para>Why this file exists: the two questions used to be asked in two places with two different
/// spellings. The predicate asked "is this NOT cXML/X12?"; the emitter asked "is this JSON, XML or
/// CSV?". <see cref="OutputFormat.Ubl"/>, <see cref="OutputFormat.UblOrder"/>,
/// <see cref="OutputFormat.X12_850"/> and <see cref="OutputFormat.EdifactOrders"/> fell in the gap:
/// promote stored the tree and told the operator it had saved their file layout, the transform
/// adopted it, the emitter threw, and EVERY future order for that supplier ended in a terminal
/// <c>transform_failed</c>. One click bricked the supplier and reported success.</para>
///
/// <para>Both members below are exhaustive <c>switch</c>es over <see cref="OutputFormat"/> — no
/// <c>_ =&gt;</c> arm, no "everything except" — so adding an enum member is a decision, not an
/// accident. <c>OutputTreeRenderabilityTests</c> EXECUTES the emitter for every enum value and fails
/// if the two ever disagree.</para>
/// </summary>
public static class OutputTreeFormats
{
    /// <summary>
    /// True only for the formats <c>OutputTemplateEmitter</c> actually renders a document for.
    ///
    /// <list type="bullet">
    ///   <item><description><b>JSON / XML / CSV</b> — a generic node tree IS the document.</description></item>
    ///   <item><description><b>cXML / UBL / Peppol / X12 / EDIFACT</b> — the document is defined by the
    ///     standard's own envelope (cXML's DOCTYPE + From/To/Sender Header, Peppol's mandatory
    ///     UBLVersionID/CustomizationID/ProfileID, X12's ISA/GS, EDIFACT's UNB/UNH). A node tree
    ///     carries none of that, so emitting one would deliver a well-formed document the receiver
    ///     REJECTS. Those formats deliver through their dedicated fixed transform
    ///     (offer ⇔ works).</description></item>
    /// </list>
    /// </summary>
    public static bool IsRenderable(OutputFormat format) => format switch
    {
        OutputFormat.Json => true,
        OutputFormat.Xml  => true,
        OutputFormat.Csv  => true,

        OutputFormat.CXml          => false,
        OutputFormat.Ubl           => false,
        OutputFormat.X12           => false,
        OutputFormat.UblOrder      => false,
        OutputFormat.X12_850       => false,
        OutputFormat.EdifactOrders => false,
    };

    /// <summary>
    /// True only for the formats whose fixed transform actually READS an
    /// <see cref="OutputNodeTemplate.Envelope"/>. For these, a non-renderable tree still has a real
    /// job — it carries the connection's sender/receiver identity — and for every other format an
    /// envelope changes not one delivered byte.
    ///
    /// <para>Deliberately NOT the complement of <see cref="IsRenderable"/>: <c>Ubl</c>,
    /// <c>UblOrder</c>, <c>X12_850</c> and <c>EdifactOrders</c> are neither renderable nor
    /// envelope-reading, so a tree in one of those formats can contribute nothing at all. Treating
    /// "not renderable" as "envelope-bearing" would re-open the same brick through the envelope
    /// door.</para>
    /// </summary>
    public static bool ReadsEnvelopeIdentity(OutputFormat format) => format switch
    {
        OutputFormat.CXml => true,   // CxmlTransformService reads envelope.Cxml
        OutputFormat.X12  => true,   // X12TransformService  reads envelope.X12

        OutputFormat.Json          => false,
        OutputFormat.Xml           => false,
        OutputFormat.Csv           => false,
        OutputFormat.Ubl           => false,
        OutputFormat.UblOrder      => false,
        OutputFormat.X12_850       => false,
        OutputFormat.EdifactOrders => false,
    };
}
