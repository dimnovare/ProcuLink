using Hangfire;
using ProcuLink.Api.Jobs;
using ProcuLink.Infrastructure.Jobs;
using Xunit;

namespace ProcuLink.Api.Tests.Jobs;

/// <summary>
/// Audit batch A #2 (attribute half): DeliverOrderJob must not carry its own Hangfire
/// retry budget. DeliveryService converts every failure (including thrown storage
/// errors) into a failed DeliveryResult, and the SINGLE retry authority is the
/// RetryDeliveryJob backoff queue — a Hangfire-level retry on top of that re-dispatches
/// the same artifact (double-delivery to the supplier) and double-counts attempts past
/// the dead-letter cap.
/// </summary>
public class DeliverOrderJobRetryPolicyTests
{
    [Fact]
    public void ExecuteAsync_HasAutomaticRetryDisabled()
    {
        // Read the attribute METADATA (CustomAttributeData) instead of instantiating it:
        // AutomaticRetryAttribute's ctor touches Hangfire's LogProvider, which throws in
        // a bare test context with no Hangfire bootstrap.
        var attrData = typeof(DeliverOrderJob)
            .GetMethod(nameof(DeliverOrderJob.ExecuteAsync))!
            .CustomAttributes
            .SingleOrDefault(a => a.AttributeType == typeof(AutomaticRetryAttribute));

        Assert.NotNull(attrData);
        var attempts = attrData!.NamedArguments
            .Single(n => n.MemberName == nameof(AutomaticRetryAttribute.Attempts))
            .TypedValue.Value;
        Assert.Equal(0, attempts);
    }

    /// <summary>
    /// D-1: DeliverOrderJob must carry the per-order distributed mutex (keyed on the orderId argument,
    /// index 0) so two activations for the SAME order — a double-clicked Redeliver, or one racing a
    /// scheduled RetryDeliveryJob — can't run concurrently and double-dispatch. The attribute exists and
    /// is already applied to RetryDeliveryJob; this pins it onto the first-deliver/redeliver path too.
    /// </summary>
    [Fact]
    public void ExecuteAsync_HasPerOrderDistributedMutex_KeyedOnOrderIdArgument()
    {
        var attr = typeof(DeliverOrderJob)
            .GetMethod(nameof(DeliverOrderJob.ExecuteAsync))!
            .GetCustomAttributes(typeof(PerOrderDistributedMutexAttribute), inherit: false)
            .Cast<PerOrderDistributedMutexAttribute>()
            .SingleOrDefault();

        Assert.NotNull(attr);

        // orderId is the FIRST parameter of ExecuteAsync, so the mutex must key on argument index 0.
        var parameters = typeof(DeliverOrderJob).GetMethod(nameof(DeliverOrderJob.ExecuteAsync))!.GetParameters();
        Assert.Equal("orderId", parameters[0].Name);
    }
}
