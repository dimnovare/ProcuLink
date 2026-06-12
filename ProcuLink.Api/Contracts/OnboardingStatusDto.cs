// ProcuLink.Api/Contracts/OnboardingStatusDto.cs
namespace ProcuLink.Api.Contracts;

/// <summary>
/// Server-truth onboarding milestones for the authenticated org, consumed by the
/// setup checklist (see docs/superpowers/plans/2026-06-12-onboarding-overhaul-design.md).
///
/// All flags, ids, and counts EXCLUDE sample data (<c>IsSample</c> suppliers/orders created
/// by the sample-order onboarding path): samples teach, they never certify a milestone.
/// A sample-only org therefore intentionally reports every flag false and every count zero,
/// while <see cref="HasSampleOrder"/>/<see cref="SampleOrderId"/> still point at the sample.
/// </summary>
public record OnboardingStatusDto(
    bool  HasSupplier,
    bool  HasUpload,
    bool  HasResolvedMapping,
    bool  HasDelivery,
    bool  HasCatalog,
    bool  HasItemMappings,
    bool  HasDeliveryConfig,
    bool  HasTestFired,
    Guid? FirstSupplierId,
    Guid? FirstActionableOrderId,
    int   SupplierCount,
    int   OrderCount,
    int   DeliveredCount,
    bool  HasSampleOrder,
    Guid? SampleOrderId
);
