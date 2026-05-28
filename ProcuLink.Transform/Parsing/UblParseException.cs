namespace ProcuLink.Transform.Parsing;

/// <summary>
/// Thrown by <see cref="UblOrderParser"/> when a required UBL 2.1 / Peppol BIS Order field
/// is absent or malformed, or when the document is not a UBL Order at all.
/// The controller should map this to HTTP 422 Unprocessable Entity.
/// </summary>
public sealed class UblParseException : Exception
{
    public UblParseException(string message) : base(message) { }
    public UblParseException(string message, Exception inner) : base(message, inner) { }
}
