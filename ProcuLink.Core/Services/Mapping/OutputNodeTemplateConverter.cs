namespace ProcuLink.Core.Services.Mapping;

/// <summary>
/// Bridges the existing FLAT <see cref="OutputMappingConfig"/> (today's per-order / supplier-promoted
/// overrides) into an equivalent <see cref="OutputNodeTemplate"/> tree, so existing overrides render
/// through the new <c>OutputTemplateEmitter</c> with the SAME values — and, for CSV, byte-identically.
///
/// The flat shape (header field rules + line field rules) maps to: a root Object whose direct Field
/// children are the header columns, followed by a single Array ("lines") whose item Object's Field
/// children are the per-line columns. Field <c>Name</c> = the rule's <c>OutputPath</c> (the column /
/// property name the flat builder used), so the column headers and ordering round-trip exactly.
/// </summary>
public static class OutputNodeTemplateConverter
{
    /// <summary>
    /// Lift a flat <paramref name="output"/> config into an <see cref="OutputNodeTemplate"/> for the
    /// given <paramref name="format"/>. Dictionary insertion order is preserved (the editor's column
    /// order). The resulting tree, rendered by the emitter, reproduces the flat builder's CSV
    /// byte-for-byte; for JSON it produces the same field values in a flat object + "lines" array.
    /// </summary>
    public static OutputNodeTemplate FromFlat(OutputMappingConfig output, OutputFormat format)
    {
        var headerFields = output.Header.Values
            .Select(rule => OutputNode.FieldOf(rule.OutputPath, rule));

        var lineFields = output.Lines.Values
            .Select(rule => OutputNode.FieldOf(rule.OutputPath, rule))
            .ToArray();

        var children = headerFields
            .Append(OutputNode.Arr("lines", OutputNode.Obj("line", lineFields)))
            .ToArray();

        return new OutputNodeTemplate
        {
            Format = format,
            Root = OutputNode.Obj("root", children),
        };
    }
}
