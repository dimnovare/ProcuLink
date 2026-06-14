namespace ProcuLink.Transform.Parsing;

/// <summary>Phase 1: a labelled value captured from the document that has no canonical slot
/// (e.g. supplier number, EDI id, contract no). Carried into the SourceCapture raw bag.</summary>
public record ParsedRawField(string Label, string Value);
