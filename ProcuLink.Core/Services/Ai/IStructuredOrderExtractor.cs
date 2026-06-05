namespace ProcuLink.Core.Services.Ai;

/// <summary>
/// Result of attempting to extract a structured purchase order from a document
/// (currently a text-based PDF) using an LLM.
///
/// All failure paths (no API key, wrong provider, over the per-org token cap,
/// no text layer, low confidence, zero lines) return <see cref="Success"/> =
/// <c>false</c> without throwing, so the caller can fall back to deterministic
/// parsing.
/// </summary>
/// <param name="Success">
/// True when a purchase order was extracted with at least one line at or above
/// the confidence threshold. False on every failure path.
/// </param>
/// <param name="Confidence">
/// Model-reported extraction confidence in the range 0.0–1.0. Always 0.0 when
/// <see cref="Success"/> is false.
/// </param>
/// <param name="Order">
/// The extracted purchase order. Null when <see cref="Success"/> is false.
/// </param>
/// <param name="FailureReason">
/// Human-readable explanation of why extraction failed. Null when
/// <see cref="Success"/> is true.
/// </param>
/// <param name="ReviewLineNumbers">
/// Line numbers (matching <see cref="ExtractedOrderLine.LineNumber"/> on the
/// returned <see cref="Order"/>) that the anti-hallucination validation flagged
/// as uncertain — e.g. an emitted number that did not appear verbatim in the
/// source text, or a quantity × unit price that did not reconcile with the
/// stated line amount. The caller marks the corresponding persisted lines as
/// "needs review" so they surface for a human in the exceptions dashboard
/// rather than being silently delivered. Empty when nothing needs review.
/// </param>
public sealed record StructuredExtractionResult(
    bool Success,
    double Confidence,
    ExtractedOrder? Order,
    string? FailureReason,
    IReadOnlyCollection<int> ReviewLineNumbers)
{
    /// <summary>A failure result carrying a reason and no order.</summary>
    public static StructuredExtractionResult Fail(string reason) =>
        new(false, 0.0, null, reason, Array.Empty<int>());
}

/// <summary>
/// Extracts a structured purchase order from a document using an LLM
/// (text → strict-JSON structured extraction). This is the primary PDF parse
/// path; when it is unavailable or returns a failure the orchestrator falls
/// back to deterministic parsing.
///
/// Implementations enforce the per-org monthly token cap via
/// <see cref="IAiUsageTracker"/>. The organisation id is supplied per call (not
/// read from <c>ICurrentTenantService</c>) so the same singleton instance works
/// in both the API and the background Worker host.
/// </summary>
public interface IStructuredOrderExtractor
{
    /// <summary>
    /// True when an LLM provider is configured (API key present and provider is
    /// "openai"). When false the orchestrator should not call
    /// <see cref="ExtractAsync"/> and should use deterministic parsing instead.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Attempts to extract a structured purchase order from the supplied
    /// document. Never throws — returns <see cref="StructuredExtractionResult.Success"/>
    /// = false on every failure path.
    /// </summary>
    /// <param name="document">Readable stream containing the document bytes.</param>
    /// <param name="contentType">MIME type of the document (e.g. <c>application/pdf</c>).</param>
    /// <param name="organisationId">Tenant whose monthly token cap applies.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<StructuredExtractionResult> ExtractAsync(
        Stream document,
        string contentType,
        Guid organisationId,
        CancellationToken ct);
}
