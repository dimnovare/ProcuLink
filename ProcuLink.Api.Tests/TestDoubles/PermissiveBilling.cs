using Moq;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Services;

namespace ProcuLink.Api.Tests.TestDoubles;

/// <summary>
/// An <see cref="IBillingService"/> that says yes to everything.
///
/// <para>For tests whose subject is NOT the billing gate. Introduced with WP-11, which
/// added gates to several previously ungated entry points: without an explicit
/// permissive double, a bare <c>Mock&lt;IBillingService&gt;</c> returns <c>false</c>/
/// <c>default</c> and every such test would fail at the new gate instead of exercising
/// what it was written to exercise.</para>
///
/// <para>Never use this in a test that is ABOUT a gate — <c>default</c> answers are exactly
/// how a gate test goes vacuously green.</para>
/// </summary>
public static class PermissiveBilling
{
    /// <summary>Allows every feature, every limit check, for every org.</summary>
    public static Mock<IBillingService> Mock()
    {
        var billing = new Mock<IBillingService>();

        billing.Setup(b => b.HasFeatureAsync(
                It.IsAny<Guid>(), It.IsAny<BillingFeature>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        billing.Setup(b => b.CanProcessOrdersAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        billing.Setup(b => b.CanAddSupplierAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        billing.Setup(b => b.CheckOrderLimitAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LimitCheckResult(
                Allowed: true, PilotExpired: false, Plan: PlanConstants.Enterprise, Limit: int.MaxValue));

        billing.Setup(b => b.CheckSupplierLimitAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LimitCheckResult(
                Allowed: true, PilotExpired: false, Plan: PlanConstants.Enterprise, Limit: int.MaxValue));

        return billing;
    }

    /// <summary>Shorthand for <c>Mock().Object</c>.</summary>
    public static IBillingService Service() => Mock().Object;
}
