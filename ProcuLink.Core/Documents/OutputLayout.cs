namespace ProcuLink.Core.Documents;

/// <summary>The words around a pdf or text output: a title, text at the top and text at the bottom.</summary>
public sealed record OutputLayout(string? Title, string? Header, string? Footer);
