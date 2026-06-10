namespace ProcuLink.Transform.Tokenizing;

/// <summary>
/// A single addressable value from a source file, produced by <see cref="ISourceTokenizer"/>.
/// </summary>
/// <param name="Id">
///   Stable, format-specific address for the token — used as the lookup key in
///   <c>SourceFieldRule.SourceToken</c>. Must be deterministic for a given file so the
///   user's saved rule still resolves after a re-parse of the same file type.
///   <list type="bullet">
///     <item>CSV: <c>"cell:r{row}c{col}"</c> (1-based row and column indices, e.g. <c>"cell:r2c3"</c>).</item>
///     <item>XML: the element or attribute XPath relative to the document root,
///           e.g. <c>"/Order/Header/PoNumber"</c> or <c>"/Order/Line[2]/@qty"</c>.</item>
///   </list>
/// </param>
/// <param name="Label">
///   Human-readable display name shown in the mapping editor UI. Designed so a user can
///   identify the exact source field at a glance (the Id is the machine address, the Label
///   is the human one):
///   <list type="bullet">
///     <item>CSV / XLSX data cell: <c>"{ColumnHeader} · row {n}"</c> (e.g. <c>"Unit Price · row 9"</c>),
///           falling back to <c>"Col {Letter} · row {n}"</c> (e.g. <c>"Col C · row 9"</c>) when the
///           column has no header. Header-row cells are labelled by the column name itself.</item>
///     <item>XML / cXML / UBL / IDoc: the element local-name (namespace prefix preserved, e.g.
///           <c>"cbc:ID"</c>) with a short parent context (<c>"Order › PoNumber"</c>); attributes
///           read <c>"{ownerElement}@{attrName}"</c> (e.g. <c>"OrderRequestHeader@orderID"</c>).</item>
///     <item>EDIFACT / X12: the segment tag plus the element meaning where known
///           (e.g. <c>"BGM document no"</c>, <c>"LIN item code"</c>, <c>"QTY quantity"</c>),
///           falling back to <c>"{TAG} element {n}"</c> for unmapped positions. A
///           <c>" #{n}"</c> suffix disambiguates repeated segments.</item>
///   </list>
/// </param>
/// <param name="Value">The raw text value extracted from the source file.</param>
/// <param name="Group">
///   Optional grouping hint: <c>"header"</c> for order-level tokens, <c>"line"</c> for
///   per-line tokens, or <c>null</c> when the format does not have a meaningful distinction.
/// </param>
public record SourceToken(string Id, string Label, string Value, string? Group);
