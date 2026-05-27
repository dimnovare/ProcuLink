namespace ProcuLink.Transform.Parsing;

/// <summary>
/// Thrown by <see cref="CxmlOrderParser"/> when a required cXML field is absent or malformed.
/// The controller should map this to HTTP 422 Unprocessable Entity.
/// </summary>
public sealed class CxmlParseException : Exception
{
    public CxmlParseException(string message) : base(message) { }
    public CxmlParseException(string message, Exception inner) : base(message, inner) { }
}
