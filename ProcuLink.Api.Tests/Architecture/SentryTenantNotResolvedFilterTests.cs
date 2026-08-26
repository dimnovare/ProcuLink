using System.Reflection;
using ProcuLink.Api.Telemetry;
using ProcuLink.Core.Services;
using Sentry;
using Sentry.Extensibility;
using Xunit;

namespace ProcuLink.Api.Tests.Architecture;

/// <summary>
/// An expected refusal must not be reported as an error.
///
/// <para><b>The defect this closes.</b> <see cref="TenantNotResolvedException"/> is caught and
/// answered <c>503 + Retry-After</c> by <c>TenantNotResolvedExceptionHandler</c> — nothing is
/// broken, the request simply arrived before Clerk attached the organisation claim to the session
/// token. But Sentry's middleware is installed on the WebHost, ahead of
/// <c>UseExceptionHandler</c>, so it sees every throw whether or not something handled it. Measured
/// in production on 2026-08-26: after the 503 fix deployed, the 500s stopped and
/// <c>TenantNotResolvedException</c> events took their place — three issues in the first hour, on a
/// path that fires for EVERY new organisation. That is exactly the volume of expected-condition
/// noise that trains an operator to stop reading the dashboard.</para>
///
/// <para><b>What is asserted, and why the negative half matters more.</b> A filter that excluded
/// too much would be worse than the noise: it could silence a real fault and nothing would say so.
/// So every test below is paired — the type is filtered, AND its sibling and an unrelated
/// exception are not.</para>
/// </summary>
public class SentryTenantNotResolvedFilterTests
{
    /// <summary>The options as Program.cs builds them, minus the DSN (which no test needs).</summary>
    private static SentryOptions ConfiguredOptions()
    {
        var options = new SentryOptions();
        options.UseProcuLinkScrubbing();
        options.AddExceptionFilterForType<TenantNotResolvedException>();
        return options;
    }

    /// <summary>
    /// Reads the SDK's own filter list rather than re-implementing the decision, so this guard
    /// tests the mechanism that actually runs. If the SDK renames it, the assertion says so
    /// instead of quietly passing on an empty collection.
    /// </summary>
    private static IReadOnlyList<IExceptionFilter> Filters(SentryOptions options)
    {
        var property = typeof(SentryOptions).GetProperty(
            "ExceptionFilters", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.True(property is not null,
            "SentryOptions.ExceptionFilters no longer exists — the Sentry SDK changed and this guard is now blind.");

        var value = property!.GetValue(options) as IEnumerable<IExceptionFilter>;
        Assert.True(value is not null,
            "SentryOptions.ExceptionFilters is null — AddExceptionFilterForType did not register anything.");
        return value!.ToList();
    }

    private static bool IsFiltered(SentryOptions options, Exception exception) =>
        Filters(options).Any(f => f.Filter(exception));

    [Fact]
    public void TheTenantNotResolvedRefusal_isFilteredOut()
    {
        Assert.True(
            IsFiltered(ConfiguredOptions(), new TenantNotResolvedException()),
            "a handled 503 refusal is still being reported to Sentry as an error");
    }

    [Fact]
    public void ItsSiblingOnTheSameService_stillReachesSentry()
    {
        // ICurrentTenantService.ClerkUserId throws this for "no sub claim found — user not
        // authenticated", which means there is no authenticated user AT ALL. Different condition,
        // not retryable, and it must keep being reported. This is the assertion that stops the
        // filter widening into a blanket "tenant problems are fine".
        Assert.False(
            IsFiltered(ConfiguredOptions(), new UnauthorizedAccessException("No sub claim found — user not authenticated.")),
            "the filter has widened onto UnauthorizedAccessException and is now hiding unauthenticated requests");
    }

    [Fact]
    public void AnUnrelatedFailure_stillReachesSentry()
    {
        var options = ConfiguredOptions();
        Assert.False(IsFiltered(options, new InvalidOperationException("a real bug")),
            "the filter is swallowing unrelated exceptions");
        Assert.False(IsFiltered(options, new Exception("a real bug")),
            "the filter is swallowing unrelated exceptions");
    }

    [Fact]
    public void WithoutTheFilter_theRefusalWouldReachSentry()
    {
        // The anti-vacuity half. Every assertion above would also pass against an SDK whose
        // Filter() returned false for everything, or a reflection read that found an empty list —
        // this is the one that proves the list is doing work at all.
        var unfiltered = new SentryOptions();
        unfiltered.UseProcuLinkScrubbing();

        var filters = typeof(SentryOptions)
            .GetProperty("ExceptionFilters", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!
            .GetValue(unfiltered) as IEnumerable<IExceptionFilter>;

        Assert.True(
            filters is null || !filters.Any(f => f.Filter(new TenantNotResolvedException())),
            "scrubbing alone already filters this type, so the AddExceptionFilterForType call proves nothing");
    }

    [Fact]
    public void TheFilter_doesNotDisturbTheSecretScrubbing()
    {
        // Sentry's SetBeforeSend REPLACES rather than chains, so the obvious alternative
        // implementation of this fix — a second SetBeforeSend returning null for this type — would
        // have silently switched off secret redaction for the whole process. It is filtered by TYPE
        // precisely to avoid that. This pins the two coexisting.
        var options = ConfiguredOptions();

        foreach (var callback in new[] { "BeforeSendInternal", "BeforeBreadcrumbInternal", "BeforeSendTransactionInternal" })
        {
            var property = typeof(SentryOptions).GetProperty(
                callback, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.True(property is not null, $"SentryOptions.{callback} no longer exists — this guard is blind.");
            Assert.True(property!.GetValue(options) is Delegate,
                $"{callback} is not attached — the exception filter has displaced the scrubbing.");
        }
    }
}
