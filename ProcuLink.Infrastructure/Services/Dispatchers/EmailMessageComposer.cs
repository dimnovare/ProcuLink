using ProcuLink.Core.Services.Delivery;

namespace ProcuLink.Infrastructure.Services.Dispatchers;

/// <summary>
/// What the two email channels wrap around an artifact: the subject, the body, and the name the
/// attachment travels under. <see cref="SmtpDeliveryDispatcher"/> and
/// <see cref="EmailApiDeliveryDispatcher"/> both call it.
///
/// <para>
/// It is shared because the wording was duplicated, and the duplication was the defect's second
/// home: both dispatchers independently defaulted the subject to <c>"Purchase Order {name}"</c>, so
/// fixing the Postmark path alone would have left the SMTP path still titling a connection test as
/// a purchase order. One copy cannot half-drift.
/// </para>
///
/// <para>
/// The composition is deliberately a pure function of its arguments — no config object, no I/O —
/// so the exact subject a given call produces can be asserted directly, which is how the
/// test-fire wording is pinned.
/// </para>
/// </summary>
internal static class EmailMessageComposer
{
    /// <summary>
    /// The subject line. A test fire says it is a test; an order uses the operator's
    /// <c>subjectTemplate</c>, or the "Purchase Order …" default when they set none.
    /// </summary>
    /// <param name="isTestFire">
    /// From <c>IDeliveryDispatcher.DispatchAsync</c>. Supplied by the caller, never inferred from
    /// <paramref name="poNumber"/> — on email an order named <c>proculink-test</c> is indistinguishable
    /// from the test artifact by name alone (see the remarks on that parameter).
    /// </param>
    internal static string Subject(
        bool isTestFire, string? template, string poNumber, string attachmentName) =>
        isTestFire
            ? DeliveryTestArtifact.EmailSubject
            : Render(
                string.IsNullOrWhiteSpace(template) ? "Purchase Order " + poNumber : template,
                poNumber,
                attachmentName);

    /// <summary>
    /// The body text. Same rule as <see cref="Subject"/>: a test fire explains itself and does not
    /// render the operator's <c>bodyTemplate</c>.
    /// </summary>
    internal static string Body(
        bool isTestFire, string? template, string poNumber, string attachmentName) =>
        isTestFire
            ? DeliveryTestArtifact.EmailBody
            : Render(
                string.IsNullOrWhiteSpace(template)
                    ? $"Please find the attached purchase order ({attachmentName})."
                    : template,
                poNumber,
                attachmentName);

    /// <summary>
    /// The filename the attachment carries. A test fire uses the test artifact's own fixed name,
    /// ignoring a configured <c>attachmentFileName</c>: <see cref="DeliveryTestArtifact"/> defines
    /// that name as the one a test travels under "on every channel", and letting an operator's PO
    /// attachment name win would hand the supplier a two-line sample called, say,
    /// <c>purchase-order.csv</c> — re-dressing as an order the thing the subject just disclaimed.
    /// </summary>
    internal static string AttachmentName(bool isTestFire, string? configured, string fileName) =>
        isTestFire
            ? DeliveryTestArtifact.FileName
            : string.IsNullOrWhiteSpace(configured) ? fileName : configured;

    /// <summary>Operator template token substitution. Unchanged behaviour, moved here.</summary>
    internal static string Render(string template, string poNumber, string attachmentName) =>
        template
            .Replace("{poNumber}", poNumber)
            .Replace("{fileName}", attachmentName);
}
