using System.Net.Http;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Core.Services.Catalog;
using ProcuLink.Infrastructure.Services.Catalog;
using ProcuLink.TestSupport;

namespace ProcuLink.Infrastructure.Tests.Services.Catalog;

/// <summary>
/// Opt-in LIVE feed tests (plan 2026-07-02 Phase 8). These hit real distributor endpoints with
/// real credentials and are SKIPPED unless <c>PROCULINK_LIVE_FEED_TESTS=1</c> is set — credentials
/// come from environment variables and are NEVER committed. They exist so the founder can re-run
/// end-to-end verification against the live vendors on demand (e.g. after a credential rotation),
/// without polluting the normal CI run.
///
/// Only the vendor-fetcher (Logicom) path is covered here because it is the only feed whose auth
/// is bespoke enough to warrant a live regression gate; the SFTP/FTP/HTTP feeds are exercised by
/// the standard fake-transport tests + the manual test-fetch endpoint (Phase 8 checklist).
/// </summary>
public class LiveCatalogFeedTests
{
    private static string? Env(string name) => Environment.GetEnvironmentVariable(name);

    [EnvironmentGatedFact(
        "requires live Logicom QuickConnect credentials",
        LiveTestEnvironment.FeedOptIn,
        "LOGICOM_CUSTOMER_ID", "LOGICOM_CONSUMER_KEY", "LOGICOM_CONSUMER_SECRET", "LOGICOM_ACCESS_TOKEN_KEY")]
    public async Task Logicom_FetchesFirstPage_WithRealCredentials()
    {
        var url = Env("LOGICOM_URL") ?? "https://example.invalid/redacted";
        var customerId = Env("LOGICOM_CUSTOMER_ID");
        var consumerKey = Env("LOGICOM_CONSUMER_KEY");
        var consumerSecret = Env("LOGICOM_CONSUMER_SECRET");
        var accessTokenKey = Env("LOGICOM_ACCESS_TOKEN_KEY");

        var creds = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
            System.Text.Json.JsonSerializer.Serialize(new
            {
                type = "logicom_quickconnect",
                customerId, consumerKey, consumerSecret, accessTokenKey, currency = "EUR",
            }));

        var fetcher = new LogicomQuickConnectFetcher(NullLogger<LogicomQuickConnectFetcher>.Instance);
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        var ctx = new VendorFetchContext(url, creds, client, 256L * 1024 * 1024);

        var result = await fetcher.FetchAsync(ctx, CancellationToken.None);

        result.Data.Length.Should().BeGreaterThan(2, "the flattened JSON array should carry items");
        result.ContentType.Should().Be("application/json");
    }
}
