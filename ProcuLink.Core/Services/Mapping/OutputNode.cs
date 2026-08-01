namespace ProcuLink.Core.Services.Mapping;

/// <summary>
/// The kind of an <see cref="OutputNode"/> in the output template tree.
/// </summary>
/// <remarks>
/// Annotated so the values round-trip as camelCase strings ("object"/"array"/"field"/"attribute")
/// EVERYWHERE — including ASP.NET Core <c>[FromBody]</c> binding, which uses the global web JSON
/// options that lack a string-enum converter. Without this the frontend tree (<c>"nodeType":"object"</c>)
/// fails to bind and the preview/save endpoints return HTTP 400.
/// </remarks>
[System.Text.Json.Serialization.JsonConverter(typeof(CamelCaseJsonStringEnumConverter))]
public enum OutputNodeType
{
    /// <summary>A named wrapper with child nodes — a JSON object, an XML element, a UBL group.</summary>
    Object,
    /// <summary>
    /// A repeating group bound to a collection (v1: the order's lines). Its child template is
    /// rendered once per item; field rules inside resolve against the current item (line) context.
    /// </summary>
    Array,
    /// <summary>A leaf value — a JSON property value, XML element text, or a CSV cell. Carries a <see cref="OutputNode.Rule"/>.</summary>
    Field,
    /// <summary>An XML attribute (<c>name="value"</c>). Carries a <see cref="OutputNode.Rule"/>; ignored by the JSON/CSV emitters.</summary>
    Attribute,
}

/// <summary>
/// One node in a recursive OUTPUT TEMPLATE tree (Phase B — the structural cut). Unlike the flat
/// <see cref="OutputMappingConfig"/> (a Header/Lines dictionary of leaf rules), this expresses
/// ARBITRARY output structure: nesting, repeating groups (arrays), attributes vs elements, custom
/// wrapper/root names. The same tree is rendered to any format by a format-aware emitter.
///
/// It REUSES <see cref="OutputFieldRule"/> verbatim for leaf value resolution (source token / fixed
/// value / canonical field / Scriban expression / manipulator chain), so there is NO new value
/// semantics — the AST adds structure only, and the existing manipulator/Scriban machinery resolves
/// every leaf exactly as <c>MappedTransformService</c> does today.
/// </summary>
public record OutputNode
{
    /// <summary>The node name: JSON property / XML element-or-attribute / CSV column header / EDI segment id.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>The node kind. <see cref="Object"/> and <see cref="Array"/> use <see cref="Children"/>; <see cref="Field"/> and <see cref="Attribute"/> use <see cref="Rule"/>.</summary>
    public OutputNodeType NodeType { get; init; } = OutputNodeType.Object;

    /// <summary>Child nodes (<see cref="OutputNodeType.Object"/> / <see cref="OutputNodeType.Array"/> only).</summary>
    public List<OutputNode> Children { get; init; } = new();

    /// <summary>The leaf value recipe (<see cref="OutputNodeType.Field"/> / <see cref="OutputNodeType.Attribute"/> only).</summary>
    public OutputFieldRule? Rule { get; init; }

    /// <summary>
    /// For an <see cref="OutputNodeType.Array"/> node: the collection to repeat over. v1 supports
    /// <c>"lines"</c> (the order's resolved lines). Null defaults to <c>"lines"</c>.
    /// </summary>
    public string? Collection { get; init; }

    /// <summary>
    /// (B12) Optional XML namespace URI bound to this element/attribute (e.g. the UBL
    /// <c>CommonBasicComponents-2</c> URI for a <c>cbc:</c> element). Null → the legacy single-arg
    /// emit path, byte-identical to non-namespaced output. JSON/CSV ignore it.
    /// </summary>
    public string? Namespace { get; init; }

    /// <summary>
    /// (B12) Optional XML prefix (e.g. <c>"cac"</c>/<c>"cbc"</c>) bound to <see cref="Namespace"/>.
    /// Null prefix + non-null <see cref="Namespace"/> → a default namespace (no prefix). Null + null →
    /// the legacy single-arg emit. JSON/CSV ignore it.
    /// </summary>
    public string? Prefix { get; init; }

    /// <summary>
    /// (Output designer) Optional CONDITIONAL inclusion predicate — a bare Scriban boolean condition
    /// (NO <c>{{ }}</c> delimiters), evaluated against the same row scope as <see cref="OutputFieldRule.Expression"/>:
    /// <c>order.*</c> in header scope, plus <c>line.*</c> in line scope, with the numeric line fields
    /// (<c>Quantity</c>/<c>UnitPrice</c>/<c>LineTotal</c>/<c>LineNumber</c>) as real numbers. Examples:
    /// <c>line.Quantity &gt; 0</c>, <c>order.Currency == "EUR"</c>, <c>line.TaxRate != ""</c>.
    /// <para>When the predicate is falsy the emitter SKIPS this node: a field/object child is omitted,
    /// an array ITEM template skips that line (e.g. drop zero-quantity lines). Null/blank → always
    /// included (byte-identical to before — no behaviour change). A predicate that fails to evaluate
    /// FAILS OPEN (the node is kept) so an authoring mistake never silently drops the supplier's data.
    /// CSV applies it to line ITEMS only (fixed column grid); JSON/XML apply it to any node.</para>
    /// </summary>
    public string? IncludeWhen { get; init; }

    /// <summary>
    /// JSON only (WP-15): emit this leaf as a typed value rather than a string.
    ///
    /// <para><c>number</c> / <c>boolean</c> / <c>null</c>; anything else, and null itself, means
    /// <b>string</b> — which is what every leaf has always been, so an absent value is byte-identical.
    /// Ignored by CSV and XML, which have no types to express: everything there is text on the wire,
    /// and pretending otherwise would be a lie the receiver cannot act on.</para>
    ///
    /// <para><b>A value that does not fit the declared type FAILS the transform.</b> It does not fall
    /// back to a string. A receiver validating against a schema that says <c>quantity</c> is a number
    /// rejects the whole document on <c>"10 pcs"</c>, and rejects it AFTER we have said "delivered" —
    /// so failing at transform time, where an operator can still see the order, is the kinder of the
    /// two failures.</para>
    /// </summary>
    public string? ValueType { get; init; }

    /// <summary>
    /// JSON only (WP-15): what to do when this leaf resolves to nothing.
    ///
    /// <para><c>omit</c> drops the property from its object entirely — which is what a receiver that
    /// distinguishes "absent" from "empty string" needs, and which no amount of value formatting can
    /// express. Null (the default) writes the empty value, exactly as before.</para>
    /// </summary>
    public string? EmptyValue { get; init; }

    // ── Convenience factories (keep call sites + the converter readable) ──────────

    public static OutputNode Obj(string name, params OutputNode[] children) =>
        new() { Name = name, NodeType = OutputNodeType.Object, Children = children.ToList() };

    public static OutputNode Arr(string name, OutputNode itemTemplate, string? collection = null) =>
        new() { Name = name, NodeType = OutputNodeType.Array, Collection = collection, Children = new() { itemTemplate } };

    public static OutputNode FieldOf(string name, OutputFieldRule rule) =>
        new() { Name = name, NodeType = OutputNodeType.Field, Rule = rule };

    public static OutputNode Attr(string name, OutputFieldRule rule) =>
        new() { Name = name, NodeType = OutputNodeType.Attribute, Rule = rule };
}

/// <summary>
/// A complete output template: a tree (<see cref="Root"/>), the serialization format it renders to,
/// and the optional EDI/cXML envelope identity. ONE template is the single source of output truth
/// for an order/supplier/connection; format-aware emitters render it to the requested bytes.
/// </summary>
public record OutputNodeTemplate
{
    /// <summary>
    /// The serialization format. Property-level converter so the value binds from a camelCase string
    /// ("json"/"csv"/"xml"/"cXml"/…) under <c>[FromBody]</c> too — OutputFormat itself stays unannotated
    /// because it is used widely elsewhere and must not change its default (numeric) wire shape there.
    /// </summary>
    [System.Text.Json.Serialization.JsonConverter(typeof(CamelCaseJsonStringEnumConverter))]
    public OutputFormat Format { get; init; }

    public OutputNode Root { get; init; } = new();

    /// <summary>Optional XML root namespace declarations (prefix → uri). Ignored by JSON/CSV.</summary>
    public Dictionary<string, string>? Namespaces { get; init; }

    /// <summary>Per-connection EDI/cXML identity as DATA (WS-12). Null → the format's default identity.</summary>
    public EnvelopeConfig? Envelope { get; init; }

    /// <summary>
    /// Per-supplier CSV dialect (WP-15). Null — and every null member inside it — means "exactly the
    /// bytes this emitter produced before the dialect existed". Ignored by JSON/XML.
    /// </summary>
    public CsvDialect? CsvDialect { get; init; }
}

/// <summary>
/// The CSV wire details a receiving system cares about, expressed as data so one supplier can be
/// given semicolons and CRLF without a code change.
///
/// <para><b>Every member is nullable and every null means "as before".</b> That is not tidiness — the
/// promotion path carries a designed tree from one order onto a supplier and proves it by BYTE
/// PARITY, so a non-null default anywhere in here would change the bytes of every existing CSV
/// supplier the moment this shipped. Founder ruling 2026-07-31: existing layouts keep what they
/// have, and only layouts created after this default to CRLF — which the designer writes into the
/// tree, rather than the emitter assuming it.</para>
/// </summary>
public sealed record CsvDialect
{
    /// <summary>Field separator. Null → <c>,</c>. A tab is written as a literal tab character.</summary>
    public string? Delimiter { get; init; }

    /// <summary>
    /// <c>minimal</c> (null/default) quotes only values containing the delimiter, a quote or a
    /// newline — RFC 4180's rule and today's behaviour. <c>always</c> quotes every field, which some
    /// ERP importers require in order to treat every column as text.
    /// </summary>
    public string? QuotePolicy { get; init; }

    /// <summary>
    /// Row terminator: LF or CRLF, written as the literal escape (<c>"\n"</c> / <c>"\r\n"</c>).
    ///
    /// <para>Null means <c>Environment.NewLine</c>, which is what the emitter has always used — and
    /// which is <b>platform-dependent</b>: LF on the Railway container, CRLF on a Windows dev box, so
    /// the same tree has never produced identical bytes in both places. Preserved deliberately rather
    /// than fixed here, because changing it is what would break byte parity for every existing
    /// supplier. New layouts pin it explicitly and stop inheriting the ambiguity.</para>
    /// </summary>
    public string? LineEnding { get; init; }

    /// <summary>
    /// Output text encoding by name (e.g. <c>windows-1252</c>). Null → UTF-8 with no BOM. A receiver
    /// on a legacy code page reads <c>ö</c> as mojibake from UTF-8 bytes, and cannot say so.
    /// </summary>
    public string? Encoding { get; init; }

    /// <summary>Null/true writes the header row. False omits it, for feeds that expect data only.</summary>
    public bool? WriteHeaderRow { get; init; }
}

/// <summary>
/// Per-connection EDI / cXML envelope IDENTITY expressed as data (WS-12) — so a supplier's required
/// sender/receiver identity is user-set, not a compile-time constant (today X12 bakes
/// <c>SenderId="PROCULINK"</c>/<c>ReceiverId="SUPPLIER"</c>). Independently shippable.
/// </summary>
public record EnvelopeConfig
{
    public X12Envelope? X12 { get; init; }
    public CxmlEnvelope? Cxml { get; init; }
}

/// <summary>X12 ISA/GS envelope identity + delimiters. Null fields fall back to the legacy defaults.</summary>
public record X12Envelope
{
    public string? IsaSenderQualifier { get; init; }   // ISA05
    public string? IsaSenderId { get; init; }           // ISA06
    public string? IsaReceiverQualifier { get; init; }  // ISA07
    public string? IsaReceiverId { get; init; }         // ISA08
    public string? Version { get; init; }               // GS08 / ISA12 (e.g. "004010")
    public string? UsageIndicator { get; init; }        // ISA15 — "T" test / "P" production
    public char? ElementSeparator { get; init; }
    public char? SegmentSeparator { get; init; }
    public char? ComponentSeparator { get; init; }
}

/// <summary>cXML From/To/Sender party identity (mirrors <c>CxmlCredentialConfig</c>); the shared secret is a reference, never inline.</summary>
public record CxmlEnvelope
{
    public string? FromDomain { get; init; }
    public string? FromIdentity { get; init; }
    public string? ToDomain { get; init; }
    public string? ToIdentity { get; init; }
    public string? SenderDomain { get; init; }
    public string? SenderIdentity { get; init; }

    /// <summary>Optional cXML DOCTYPE SYSTEM id (DTD URI), pinned on the revision so a reproduced
    /// order emits the same DOCTYPE. Null/blank → no DOCTYPE (byte-identical).</summary>
    public string? DtdSystemId { get; init; }

    /// <summary>Optional cXML DOCTYPE PUBLIC id (PUBLIC form requires <see cref="DtdSystemId"/> too).</summary>
    public string? DtdPublicId { get; init; }
}
