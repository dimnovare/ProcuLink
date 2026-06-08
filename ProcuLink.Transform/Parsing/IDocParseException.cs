namespace ProcuLink.Transform.Parsing;

/// <summary>
/// Thrown by <see cref="IDocOrders05Parser"/> when a required SAP IDoc ORDERS05
/// field is absent or malformed. The controller should map this to HTTP 422
/// Unprocessable Entity.
/// </summary>
public sealed class IDocParseException : Exception
{
    public IDocParseException(string message) : base(message) { }
    public IDocParseException(string message, Exception inner) : base(message, inner) { }
}
