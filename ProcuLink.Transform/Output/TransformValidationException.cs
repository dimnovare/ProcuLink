namespace ProcuLink.Transform.Output;

/// <summary>
/// Thrown when a transform is attempted on an order that still has unresolved lines,
/// or whose lines violate a target format's required-field rules (empty mandatory
/// item code, missing / zero unit price, delimiter contamination in a code field).
/// The controller maps this to HTTP 422 Unprocessable Entity / a review warning,
/// so the flagged lines surface in <c>/operations/exceptions</c> instead of being
/// delivered as a structurally-invalid or financially-wrong document.
/// </summary>
public sealed class TransformValidationException : Exception
{
    /// <summary>
    /// The distinct, ascending line numbers that blocked the transform. Kept as the
    /// primary contract because the controller already maps these to a review warning.
    /// </summary>
    public IReadOnlyList<int> UnresolvedLineNumbers { get; }

    /// <summary>
    /// The detailed per-line findings (category + human-readable message). Empty when
    /// the exception was raised via the legacy line-numbers-only constructor.
    /// </summary>
    public IReadOnlyList<LineProblem> Problems { get; }

    public TransformValidationException(IReadOnlyList<int> unresolvedLineNumbers)
        : base($"Cannot transform: lines {string.Join(", ", unresolvedLineNumbers)} still need review.")
    {
        UnresolvedLineNumbers = unresolvedLineNumbers;
        Problems = Array.Empty<LineProblem>();
    }

    /// <summary>
    /// Additive constructor that carries the detailed per-line problems alongside the
    /// line numbers. The message lists each problem so an operator sees <em>why</em> a
    /// line was held, not just which lines.
    /// </summary>
    public TransformValidationException(
        IReadOnlyList<int> unresolvedLineNumbers,
        IReadOnlyList<LineProblem> problems)
        : base(BuildMessage(unresolvedLineNumbers, problems))
    {
        UnresolvedLineNumbers = unresolvedLineNumbers;
        Problems = problems;
    }

    private static string BuildMessage(
        IReadOnlyList<int> unresolvedLineNumbers,
        IReadOnlyList<LineProblem> problems)
    {
        if (problems is { Count: > 0 })
            return "Cannot transform: " + string.Join(" ", problems.Select(p => p.Message));

        return $"Cannot transform: lines {string.Join(", ", unresolvedLineNumbers)} still need review.";
    }
}
