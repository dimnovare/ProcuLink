using ProcuLink.Core.Services.Delivery;

namespace ProcuLink.Core.Documents;

/// <summary>
/// Why a flow delivery dead-lettered, recorded when it does: who has to act. <see cref="Refused"/> and <see cref="Setup"/> are the
/// customer's (they see "Couldn't send" on the document); <see cref="Unreachable"/>, <see cref="Internal"/> and a null kind are ours.
/// An ambiguous case is always given a system kind, never a customer one, so it still pages.
/// </summary>
public static class FlowDeliveryFailureKind
{
    /// <summary>The receiver answered and refused this document: a business rejection, a duplicate number, a recipient not registered.</summary>
    public const string Refused = "refused";
    /// <summary>The customer's destination setup: credentials refused, folder, bucket or endpoint not found, a destination removed.</summary>
    public const string Setup = "setup";
    /// <summary>Retryable failures that used up every attempt: timeouts, 5xx, connection and DNS faults.</summary>
    public const string Unreachable = "unreachable";
    /// <summary>Our side: an exception, a missing artifact, a decrypt failure, a final failure nobody explained.</summary>
    public const string Internal = "internal";

    public static bool IsCustomerSide(string? kind) => kind is Refused or Setup;

    /// <summary>The kind of a dispatcher's failed result that dead-lettered a delivery, from the existing classification only.</summary>
    public static string For(DeliveryResult result)
    {
        var final = result.Outcome == DeliveryOutcome.NotRetryable;
        var cause = SupplierResponseClassification.Classify(result).Cause;
        // A final answer whose reason the receiver gave (B2Brouter's file and network refusals).
        if (final && result.SupplierReasonObservable) return Refused;
        // 401 / 403 / 404, or a final 4xx from the customer's own storage (S3, Drive, Sheets, OneDrive): their setup.
        if (cause is SupplierFailureCause.AuthRejected or SupplierFailureCause.PermissionDenied or SupplierFailureCause.EndpointNotFound
            || final && result.ResponseCode is >= 400 and < 500) return Setup;
        if (cause == SupplierFailureCause.BusinessRejection) return Refused;
        return final ? Internal : Unreachable;
    }
}
