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
/// Only the vendor-fetcher (<c>aes2fa</c>) path is covered here because it is the only feed whose
/// auth is bespoke enough to warrant a live regression gate; the SFTP/FTP/HTTP feeds are exercised
/// by the standard fake-transport tests + the manual test-fetch endpoint (Phase 8 checklist).
///
/// <para><b>The endpoint is supplied by the environment and has no default.</b> It used to fall
/// back to the vendor's live hostname, hard-coded here — a real third party's production host,
/// committed to a public repository. It is now required alongside the credentials, so the host
/// travels with the secrets it belongs to instead of sitting in the tree. A missing
/// <c>CATALOG_VENDOR_URL</c> skips the test rather than silently probing something else.</para>
/// </summary>
public class LiveCatalogFeedTests
{
    private static string? Env(string name) => Environment.GetEnvironmentVariable(name);

    [EnvironmentGatedFact(
        "requires the live AES-2FA vendor endpoint and credentials",
        LiveTestEnvironment.FeedOptIn,
        "CATALOG_VENDOR_URL",
        "CATALOG_VENDOR_CUSTOMER_ID", "CATALOG_VENDOR_CONSUMER_KEY", "CATALOG_VENDOR_CONSUMER_SECRET", "CATALOG_VENDOR_ACCESS_TOKEN_KEY")]
    public async Task Aes2faVendor_FetchesFirstPage_WithRealCredentials()
    {
        var url = Env("CATALOG_VENDOR_URL");
        var customerId = Env("CATALOG_VENDOR_CUSTOMER_ID");
        var consumerKey = Env("CATALOG_VENDOR_CONSUMER_KEY");
        var consumerSecret = Env("CATALOG_VENDOR_CONSUMER_SECRET");
        var accessTokenKey = Env("CATALOG_VENDOR_ACCESS_TOKEN_KEY");

        var creds = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
            System.Text.Json.JsonSerializer.Serialize(new
            {
                type = "aes2fa_signed",
                customerId, consumerKey, consumerSecret, accessTokenKey, currency = "EUR",
            }));

        var fetcher = new Aes2faSignedCatalogFetcher(NullLogger<Aes2faSignedCatalogFetcher>.Instance);
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        var ctx = new VendorFetchContext(url, creds, client, 256L * 1024 * 1024);

        var result = await fetcher.FetchAsync(ctx, CancellationToken.None);

        result.Data.Length.Should().BeGreaterThan(2, "the flattened JSON array should carry items");
        result.ContentType.Should().Be("application/json");
    }
}
