using System.Reflection;
using FluentAssertions;
using ProcuLink.Core.Constants;

namespace ProcuLink.Infrastructure.Tests.Constants;

/// <summary>
/// An event type that is EMITTED but not SUBSCRIBABLE is silence that reads like a working feature.
///
/// <para>
/// <c>IntegrationTriggerService.EnqueueAsync</c> matches subscriptions with exact string equality
/// (<c>s.EventType == eventType</c>) — no wildcards, no prefixes — and
/// <c>IntegrationController.Create</c>'s allow-list is the only path that inserts a subscription
/// row. So if an event is missing from the allow-list, no subscription for it can exist,
/// <c>subs.Count == 0</c>, and <c>EnqueueAsync</c> returns silently. The emit site looks correct in
/// review and the event reaches nobody, forever.
/// </para>
///
/// <para>
/// That is not hypothetical: <c>order.rejected</c> shipped in exactly that state. It was emitted by
/// <c>DeliveryService</c> while the controller's hand-typed <c>validEvents</c> array listed only
/// three events, so every supplier rejection fanned out to zero subscribers. This test is the guard
/// that makes the same mistake impossible for the next event added.
/// </para>
/// </summary>
public class IntegrationEventTypesAreSubscribableTests
{
    private static IReadOnlyList<(string Name, string Value)> DeclaredConstants() =>
        typeof(IntegrationEventTypes)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(string))
            .Select(f => (f.Name, (string)f.GetRawConstantValue()!))
            .ToList();

    [Fact]
    public void EveryDeclaredEventTypeIsSubscribable()
    {
        var declared = DeclaredConstants();

        // ANTI-VACUITY. A reflection walk that finds nothing passes every "all of them are fine"
        // assertion below. This floor fails if the walk stops seeing the constants — a rename to a
        // static readonly field, a visibility change, or a move to another class would all silently
        // empty the set otherwise.
        declared.Should().HaveCountGreaterThanOrEqualTo(5,
            "the walk must actually be finding the event constants for the assertion below to mean anything");
        declared.Select(d => d.Value).Should().Contain(
            "order.dead_lettered",
            "the walk must see the specific constant this guard was written for");

        foreach (var (name, value) in declared)
        {
            IntegrationEventTypes.Subscribable.Should().Contain(value,
                $"IntegrationEventTypes.{name} (\"{value}\") is emitted but would fan out to zero "
              + "subscribers — IntegrationController.Create refuses to create a subscription for an "
              + "event outside the allow-list");
        }
    }

    [Fact]
    public void SubscribableListsNothingThatIsNotADeclaredEventType()
    {
        // The reverse direction. An allow-list entry with no constant behind it lets a customer
        // create a subscription that nothing will ever fire — a webhook that stays silent forever
        // and looks configured.
        var declaredValues = DeclaredConstants().Select(d => d.Value).ToHashSet();

        IntegrationEventTypes.Subscribable.Should().OnlyContain(
            e => declaredValues.Contains(e),
            "every subscribable event must have an emit-side constant behind it");
    }

    [Fact]
    public void SubscribableHasNoDuplicates()
    {
        IntegrationEventTypes.Subscribable.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void DeadLetterEventIsDistinctFromThePerAttemptFailureEvent()
    {
        // These say different things and must never be collapsed. order.failed fires per failed
        // ATTEMPT and is compatible with the order still arriving on the next retry; the dead-letter
        // event is terminal. A subscriber cannot derive the second from the first, because the
        // attempt cap is server-side configuration it cannot see.
        IntegrationEventTypes.OrderDeadLettered.Should().NotBe(IntegrationEventTypes.OrderFailed);
    }
}
