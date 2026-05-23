namespace ProcuLink.Transform.Output;

/// <summary>
/// Thrown when a transform is attempted on an order that still has unresolved lines.
/// The controller maps this to HTTP 422 Unprocessable Entity.
/// </summary>
public sealed class TransformValidationException : Exception
{
    public IReadOnlyList<int> UnresolvedLineNumbers { get; }

    public TransformValidationException(IReadOnlyList<int> unresolvedLineNumbers)
        : base($"Cannot transform: lines {string.Join(", ", unresolvedLineNumbers)} still need review.")
    {
        UnresolvedLineNumbers = unresolvedLineNumbers;
    }
}
