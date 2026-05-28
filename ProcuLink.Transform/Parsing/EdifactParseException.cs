namespace ProcuLink.Transform.Parsing;

/// <summary>
/// Thrown by <see cref="EdifactOrderParser"/> when an EDIFACT ORDERS message
/// is structurally malformed (missing UNH/BGM, truncated segments, unparseable
/// delimiters, etc.). The controller should map this to HTTP 422 Unprocessable Entity.
/// </summary>
public sealed class EdifactParseException : Exception
{
    public EdifactParseException(string message) : base(message) { }
    public EdifactParseException(string message, Exception inner) : base(message, inner) { }
}
