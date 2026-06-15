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
}
