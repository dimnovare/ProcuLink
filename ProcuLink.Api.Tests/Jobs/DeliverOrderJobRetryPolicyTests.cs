using Hangfire;
using ProcuLink.Api.Jobs;
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
}
