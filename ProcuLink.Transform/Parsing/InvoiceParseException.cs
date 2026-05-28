namespace ProcuLink.Transform.Parsing;

public sealed class InvoiceParseException : Exception
{
    public InvoiceParseException(string message) : base(message) { }
    public InvoiceParseException(string message, Exception inner) : base(message, inner) { }
}
