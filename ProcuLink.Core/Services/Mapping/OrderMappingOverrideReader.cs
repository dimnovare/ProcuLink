using System.Text.Json;

namespace ProcuLink.Core.Services.Mapping;

/// <summary>
/// Pure, dependency-free decoder for the per-order mapping override stored under the
/// <c>"mappingOverride"</c> key of an order's <c>canonical_json</c>. Used by BOTH the storage
/// service (Infrastructure) and the override-aware transform branch (Api / Worker) so the override
/// is read identically everywhere. Never throws on a missing / malformed key — returns null.
/// </summary>
public static class OrderMappingOverrideReader
{
    /// <summary>Canonical_json node under which the per-order override lives.</summary>
    public const string CanonicalKey = "mappingOverride";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        // OutputNode tree (Phase B): accept camelCase string enum node types ("object"/"array"/
        // "field"/"attribute") from the frontend (the converter also still reads numeric values).
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>
    /// Extracts the <see cref="OrderMappingOverride"/> from a (possibly null) canonical_json document.
    /// Returns null when the document is null, not an object, has no override key, the key is JSON null,
    /// or the sub-document is malformed.
    /// </summary>
    public static OrderMappingOverride? Read(JsonDocument? canonicalJson)
    {
        if (canonicalJson is null) return null;

        try
        {
            if (canonicalJson.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!canonicalJson.RootElement.TryGetProperty(CanonicalKey, out var node)) return null;
            if (node.ValueKind == JsonValueKind.Null) return null;

            return node.Deserialize<OrderMappingOverride>(SerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Raw JSON text of the <c>"mappingOverride"</c> node, exactly as stored in canonical_json
    /// (used for provenance config digests — hashing the stored text, not a re-serialization).
    /// Returns null when the document is null, not an object, has no override key, or the key
    /// is JSON null. Never throws.
    /// </summary>
    public static string? ReadRawJson(JsonDocument? canonicalJson)
    {
        if (canonicalJson is null) return null;

        try
        {
            if (canonicalJson.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!canonicalJson.RootElement.TryGetProperty(CanonicalKey, out var node)) return null;
            if (node.ValueKind == JsonValueKind.Null) return null;

            return node.GetRawText();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// True only when an override is present AND actually carries an output mapping with at least one
    /// header or line rule. An override that has only custom fields (no <see cref="OutputMappingConfig"/>),
    /// or an empty output config, must NOT divert the transform — the fixed transformer stays in control
    /// so required fields can never be silently dropped.
    /// </summary>
    public static bool HasUsableOutput(OrderMappingOverride? @override) =>
        @override?.Output is { } output
        && (output.Header.Count > 0 || output.Lines.Count > 0);

    /// <summary>
    /// Supplier-config equivalent of <see cref="HasUsableOutput"/> (launch batch 4A — consuming
    /// promoted mappings): true only when a supplier-level <see cref="PoMappingConfig"/> carries a
    /// promoted output mapping with at least one header or line rule. A null config, a null
    /// <see cref="PoMappingConfig.Output"/>, or an empty output config must NOT divert the
    /// transform — the fixed transformer stays in control, byte-for-byte identical to today.
    /// </summary>
    public static bool HasUsablePromotedOutput(PoMappingConfig? config) =>
        config?.Output is { } output
        && (output.Header.Count > 0 || output.Lines.Count > 0);

    /// <summary>
    /// Supplier-config wrapper over <see cref="HasUsableOutputTree"/> (WP-12). Mirrors
    /// <see cref="HasUsablePromotedOutput"/>. Within the supplier-promoted layer a usable
    /// NON-fixed-format tree OUTRANKS a usable flat output config, mirroring the per-order ladder
    /// where <see cref="OrderMappingOverride.OutputTree"/> outranks
    /// <see cref="OrderMappingOverride.Output"/>.
    /// </summary>
    public static bool HasUsablePromotedOutputTree(PoMappingConfig? config) =>
        HasUsableOutputTree(config?.OutputTree);

    /// <summary>
    /// True only when an output template can actually contribute something to a delivered document.
    /// Two different questions, because a template means two different things by format:
    ///
    /// <list type="bullet">
    ///   <item><description><b>JSON / XML / CSV</b> — the tree IS the document, so it is usable when
    ///     its root can emit: at least one child, or a root that is itself a leaf carrying a rule. An
    ///     empty root would emit an empty document, strictly worse than the fixed transformer's
    ///     complete one.</description></item>
    ///   <item><description><b>cXML / X12</b> — the tree is NEVER rendered. The emitter refuses those
    ///     formats (a generic node tree cannot carry a valid cXML Header/DOCTYPE or an X12 ISA/GS
    ///     envelope) and the dedicated fixed transformer owns the document. The only part ever
    ///     honoured is <see cref="OutputNodeTemplate.Envelope"/>, so ONLY an envelope carrying real
    ///     identity makes such a template usable. Nodes drawn under it are decoration — reporting
    ///     them as a saved layout would promise a capability that does not exist.</description></item>
    /// </list>
    ///
    /// <para>Null-safe by construction. <see cref="OutputNodeTemplate.Root"/> and
    /// <see cref="OutputNode.Children"/> are <c>init</c> properties with defaults, but
    /// System.Text.Json ASSIGNS NULL over a default for <c>"root": null</c> / <c>"children": null</c>,
    /// and both are reachable from the <c>[FromBody] PoMappingConfig</c> endpoint that writes
    /// <c>SupplierPoMapping.ConfigJson</c> verbatim. Dereferencing them unguarded threw a
    /// NullReferenceException out of the transform's "never throws" seam.</para>
    /// </summary>
    public static bool HasUsableOutputTree(OutputNodeTemplate? tree)
    {
        if (tree?.Root is not { } root) return false;

        if (IsFixedFormatTree(tree))
            return HasEnvelopeIdentity(tree.Envelope);

        return root.Children is { Count: > 0 } || root.Rule is not null;
    }

    /// <summary>
    /// True for the two formats whose documents a node tree can never render (cXML / X12) — the
    /// WS-12 exception. Such a template contributes its <see cref="OutputNodeTemplate.Envelope"/> to
    /// the dedicated fixed transformer and nothing else: it must never route to the emitter, never
    /// replace a flat output config, and never be described to the user as a file layout.
    /// </summary>
    public static bool IsFixedFormatTree(OutputNodeTemplate? tree) =>
        tree?.Format is OutputFormat.CXml or OutputFormat.X12;

    /// <summary>
    /// True when an <see cref="EnvelopeConfig"/> actually sets something the X12 / cXML transform
    /// will use. An envelope whose every member is null/blank changes not one delivered byte, so it
    /// is indistinguishable from having no envelope at all.
    /// </summary>
    public static bool HasEnvelopeIdentity(EnvelopeConfig? envelope)
    {
        if (envelope is null) return false;

        if (envelope.Cxml is { } cxml
            && (Set(cxml.FromDomain)   || Set(cxml.FromIdentity)
             || Set(cxml.ToDomain)     || Set(cxml.ToIdentity)
             || Set(cxml.SenderDomain) || Set(cxml.SenderIdentity)
             || Set(cxml.DtdSystemId)  || Set(cxml.DtdPublicId)))
            return true;

        return envelope.X12 is { } x12
            && (Set(x12.IsaSenderQualifier)   || Set(x12.IsaSenderId)
             || Set(x12.IsaReceiverQualifier) || Set(x12.IsaReceiverId)
             || Set(x12.Version)              || Set(x12.UsageIndicator)
             || x12.ElementSeparator   is not null
             || x12.SegmentSeparator   is not null
             || x12.ComponentSeparator is not null);

        static bool Set(string? value) => !string.IsNullOrWhiteSpace(value);
    }

    /// <summary>
    /// True only when an override is present AND carries a non-blank whole-document
    /// <see cref="OrderMappingOverride.OutputTemplate"/>. Template mode takes precedence over the
    /// field-by-field <see cref="OrderMappingOverride.Output"/> config: when a usable template is
    /// present, the transform renders the whole document from it. A null/blank template leaves the
    /// existing (field-by-field override, or fixed transformer) path unchanged.
    /// </summary>
    public static bool HasUsableTemplate(OrderMappingOverride? @override) =>
        !string.IsNullOrWhiteSpace(@override?.OutputTemplate);
}
