using System.Net;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Api.Services;
using ProcuLink.Api.Tests.TestDoubles;
using ProcuLink.Infrastructure;
using Stripe;
using Xunit;

namespace ProcuLink.Api.Tests.Services;

/// <summary>
/// Erasing a paid workspace must ask Stripe live whether a subscription still runs, and every
/// way of not knowing must be null (refuse), never false (allow).
/// </summary>
public class RunningSubscriptionLookupTests
{
    /// <summary>Answers every Stripe call with the given subscriptions, or throws when given none.</summary>
    private sealed class ListingStripeClient(string[]? statuses) : IStripeClient
    {
        public int Calls { get; private set; }

        public string ApiBase => "https://api.stripe.invalid";
        public string ApiKey => "sk_test_fake";
        public string ClientId => "ca_fake";
        public string ConnectBase => "https://connect.stripe.invalid";
        public string FilesBase => "https://files.stripe.invalid";
        public string MeterEventsBase => "https://meter-events.stripe.invalid";

        public Task<T> RequestAsync<T>(HttpMethod method, string path, BaseOptions options,
            RequestOptions requestOptions, CancellationToken cancellationToken = default) where T : IStripeEntity
        {
            Calls++;
            if (statuses is null) throw new StripeException(HttpStatusCode.InternalServerError, null, "test");
            if (typeof(T) == typeof(Subscription))
                return Task.FromResult((T)(IStripeEntity)new Subscription { Id = "sub_test", Status = statuses[0] });
            var list = new StripeList<Subscription>
            {
                Data = statuses.Select(s => new Subscription { Id = "sub_test", Status = s }).ToList(),
                HasMore = false,
            };
            return Task.FromResult((T)(IStripeEntity)list);
        }

        public Task<Stream> RequestStreamingAsync(HttpMethod method, string path,
            BaseOptions options, RequestOptions requestOptions, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private static StripeBillingService Service(IStripeClient stripe, string? secretKey) =>
        new(new ProcuLinkDbContext(new DbContextOptionsBuilder<ProcuLinkDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Stripe:SecretKey"] = secretKey,
            }).Build(),
            NullLogger<StripeBillingService>.Instance, new FakeAnalyticsService(), stripe);

    [Theory]
    [InlineData(new[] { "canceled" }, false)]
    [InlineData(new[] { "canceled", "past_due" }, true)]
    [InlineData(new[] { "paused" }, true)]
    [InlineData(null, null)]
    public async Task HasRunningSubscription_IsAskedLive_AndUnknownIsNull(string[]? statuses, bool? expected)
    {
        var svc = Service(new ListingStripeClient(statuses), "sk_test_fake");

        (await svc.HasRunningSubscriptionAsync("cus_test", null)).Should().Be(expected);
        // Subscription id only (the stub returns the first status): without a customer id the
        // customer's other subscriptions cannot be seen, so a canceled one is unknown, never false.
        (await svc.HasRunningSubscriptionAsync(null, "sub_test")).Should()
            .Be(statuses is [var first, ..] && first != "canceled" ? true : null);
    }

    [Fact]
    public async Task HasRunningSubscription_WithoutStripe_IsNull_AndAsksNothing()
    {
        var stripe = new ListingStripeClient(["canceled"]);

        (await Service(stripe, null).HasRunningSubscriptionAsync("cus_test", "sub_test")).Should().BeNull();
        stripe.Calls.Should().Be(0);
    }
}
