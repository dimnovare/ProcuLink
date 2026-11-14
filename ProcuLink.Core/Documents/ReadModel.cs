using System.Text.Json.Serialization;

namespace ProcuLink.Core.Documents;

/// <summary>Normalised 0..1 rectangle, origin top-left.</summary>
public sealed record Box(double X, double Y, double Width, double Height);

/// <summary>One word of a PDF page with its box. Page is 1-based.</summary>
public sealed record PageWord(string Text, int Page, Box Box);

/// <summary>Where a value was read. Page is 1-based; Address is <c>cell:r{row}c{col}</c> for sheets.</summary>
public sealed record SourceLocation(int? Page, Box? Box, string? Address);

public sealed record ReadField(
    string Key, string Label, string? Value, string Type, string? Meaning,
    double Confidence, [property: JsonPropertyOrder(1)] SourceLocation? Location,
    [property: JsonPropertyOrder(1)] bool NotOnPage, [property: JsonPropertyOrder(1)] bool Listed, [property: JsonPropertyOrder(1)] bool Edited)
{
    private readonly double _confidence = Confidence;

    // Declared in the body, so it would serialise last: the Order attributes above keep the original property order.
    /// <summary>An empty value carries no confidence, however the record was built, loaded or changed.</summary>
    public double Confidence { get => string.IsNullOrWhiteSpace(Value) ? 0 : _confidence; init => _confidence = value; }
}

public sealed record ReadCell(
    string? Value, double Confidence, [property: JsonPropertyOrder(1)] SourceLocation? Location,
    [property: JsonPropertyOrder(1)] bool NotOnPage, [property: JsonPropertyOrder(1)] bool Edited)
{
    private readonly double _confidence = Confidence;

    // Declared in the body, so it would serialise last: the Order attributes above keep the original property order.
    /// <summary>An empty value carries no confidence, however the record was built, loaded or changed.</summary>
    public double Confidence { get => string.IsNullOrWhiteSpace(Value) ? 0 : _confidence; init => _confidence = value; }
}

public sealed record ReadColumn(string Key, string Label, string Type, string? Meaning);

/// <summary>Cells keyed by column key.</summary>
public sealed record ReadRow(IReadOnlyDictionary<string, ReadCell> Cells);

public sealed record ReadTable(
    string Key, string Label, IReadOnlyList<ReadColumn> Columns, IReadOnlyList<ReadRow> Rows, bool Listed);

/// <summary>Missing collections in stored JSON come back empty, never null.</summary>
public sealed record ReadResult(
    string? KindLabel, IReadOnlyList<ReadField>? Fields = null, IReadOnlyList<ReadTable>? Tables = null, bool Truncated = false)
{
    public IReadOnlyList<ReadField> Fields { get; } = Fields ?? [];
    public IReadOnlyList<ReadTable> Tables { get; } = Tables ?? [];

    public static ReadResult Empty { get; } = new(KindLabel: null);
}
