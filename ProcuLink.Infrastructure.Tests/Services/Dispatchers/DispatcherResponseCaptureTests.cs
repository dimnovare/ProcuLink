using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Core.Services.Erp;
using ProcuLink.Infrastructure.Services.Dispatchers;
using ProcuLink.Infrastructure.Services.Security;
using ProcuLink.Infrastructure.Tests.TestDoubles;
using Xunit;

namespace ProcuLink.Infrastructure.Tests.Services.Dispatchers;

/// <summary>
/// Every production dispatcher must state, explicitly and on purpose, whether it can SEE the
/// counterparty's refusal text — because <c>SupplierResponseClassification</c> splits a 400 on
/// exactly that, and a channel that cannot see it has no business asserting the supplier said
/// nothing.
///
/// <para>The sibling of <c>DispatcherResendSafetyTests</c>, and for the same reason: three
/// dispatchers silently inherited the wrong answer for a whole packet, and a hard-coded protocol
/// list would have fixed those three while leaving the fourth — the one that does not exist yet —
/// to inherit it too. This test is the enforcement point; the interface default (<c>false</c>) is
/// the fail-safe backstop, not a substitute for the declaration.</para>
/// </summary>
public class DispatcherResponseCaptureTests
{
    public static TheoryData<string, bool> ExpectedCapture => new()
    {
        // Reads the response body on every non-2xx and passes it through verbatim.
        { "http", true },
        // Fixed by this packet: the connectors always read the body, and now carry it.
        { "erp_erply", true },
        { "erp_directo", true },
        { "email", true },
        // No HTTP status codes on the wire at all — every result has a null ResponseCode, so the
        // classification's 400 branch is unreachable and there is nothing to capture.
        { "sftp", false },
        { "ftps", false },
        { "smtp", false },
    };

    [Theory]
    [MemberData(nameof(ExpectedCapture))]
    public void Dispatcher_DeclaresWhetherItCanSeeTheSuppliersReason(string protocol, bool expected)
    {
        var dispatcher = AllProductionDispatchers()
            .Single(d => string.Equals(d.Protocol, protocol, StringComparison.OrdinalIgnoreCase));

        dispatcher.CapturesSupplierResponseBody.Should().Be(expected);
    }

    [Fact]
    public void EveryProductionDispatcher_IsCoveredByThisTest()
    {
        var covered = ExpectedCapture.Select(row => (string)row[0]!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var actual = AllProductionDispatchers().Select(d => d.Protocol).ToHashSet(StringComparer.OrdinalIgnoreCase);

        actual.Should().BeEquivalentTo(covered,
            "a new delivery channel must say whether it can read the counterparty's refusal — add " +
            "it to ExpectedCapture. Saying nothing means a bare 400 on that channel is treated as a " +
            "business rejection, which is the safe answer but almost certainly not the intended one");
    }

    /// <summary>
    /// The default itself, asserted rather than assumed. This is the value a dispatcher written
    /// tomorrow gets for free, so which direction it points IS the safety property.
    /// </summary>
    [Fact]
    public void ADispatcherThatDeclaresNothing_IsTreatedAsUnableToSeeTheReason()
    {
        IDeliveryDispatcher silent = new SilentDispatcher();

        silent.CapturesSupplierResponseBody.Should().BeFalse(
            "the fail-safe direction: an unexplained 4xx from a channel we cannot read goes to a " +
            "human, rather than being re-POSTed to a live endpoint up to the attempt cap");

        SupplierResponseClassification
            .Classify(400, supplierReason: null, silent.CapturesSupplierResponseBody)
            .IsBusinessRejection.Should().BeTrue();
    }

    // ── Retry-After, on the wire ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The supplier's own back-pressure, read off the response. Nothing in this repository looked at
    /// <c>Retry-After</c> before WP-19's follow-up, so a 429 was retried on our fixed schedule no
    /// matter what the endpoint asked for.
    /// </summary>
    [Fact]
    public async Task HttpDispatcher_RateLimitedWithRetryAfter_CarriesTheRequestedWait()
    {
        var dispatcher = new RecordingHttpDispatcher(
            HttpStatusCode.TooManyRequests, "slow down", retryAfterSeconds: 5400);

        var result = await dispatcher.DispatchAsync(
            "x"u8.ToArray(), "PO-1.xml", "application/xml",
            HttpConfig(), "{}", CancellationToken.None);

        result.ResponseCode.Should().Be(429);
        result.RetryAfter.Should().Be(TimeSpan.FromMinutes(90));
        result.SupplierReasonObservable.Should().BeTrue();
    }

    [Fact]
    public void RetryAfterHeader_ReadsBothWireForms_AndBoundsHostileValues()
    {
        var now = new DateTimeOffset(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);

        RetryAfterHeader.Read(Headers(delta: TimeSpan.FromSeconds(120)), now)
            .Should().Be(TimeSpan.FromMinutes(2), "delta-seconds is the common form");

        RetryAfterHeader.Read(Headers(date: now.AddMinutes(45)), now)
            .Should().BeCloseTo(TimeSpan.FromMinutes(45), TimeSpan.FromSeconds(1),
                "the HTTP-date form is resolved against our clock");

        RetryAfterHeader.Read(Headers(date: now.AddMinutes(-5)), now)
            .Should().BeNull("a date already in the past is not a wait, and must never be negative");

        RetryAfterHeader.Read(Headers(delta: TimeSpan.FromDays(1)), now)
            .Should().Be(RetryAfterHeader.MaxHonoured,
                "the value is third-party input; beyond the last backoff step the stranded sweep " +
                "would re-drive the order anyway, so honouring more is not honouring at all");

        RetryAfterHeader.Read(new HttpResponseMessage().Headers, now)
            .Should().BeNull("no header, no wait");
        RetryAfterHeader.Read(null, now).Should().BeNull("channels without headers ask for nothing");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────

    private static HttpResponseHeaders Headers(TimeSpan? delta = null, DateTimeOffset? date = null)
    {
        var response = new HttpResponseMessage();
        response.Headers.RetryAfter = delta is { } d
            ? new RetryConditionHeaderValue(d)
            : new RetryConditionHeaderValue(date!.Value);
        return response.Headers;
    }

    private static SupplierDeliveryConfig HttpConfig() => new()
    {
        Id = Guid.NewGuid(),
        OrgId = Guid.NewGuid(),
        SupplierId = Guid.NewGuid(),
        Protocol = "http",
        AutoDeliver = true,
        ConfigJson = JsonSerializer.Serialize(new { url = "https://supplier.example/orders" }),
        EncryptedCredentials = string.Empty,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    /// <summary>
    /// Waved through: the dispatchers built here are inspected for their declarations, and the one
    /// that DOES dispatch talks to a stubbed transport, so the guard's real SSRF verdict is not what
    /// is under test (<c>OutboundRequestGuardTests</c> owns that). Mirrors the sibling files'
    /// <c>AllowAllGuard()</c> — a bare empty configuration refuses the fictional hostname before the
    /// stub handler is ever reached, which shows up as a null ResponseCode.
    /// </summary>
    private static OutboundRequestGuard Guard() =>
        new(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Delivery:AllowPrivateNetworkTargets"] = "true",
                })
                .Build(),
            NullLogger<OutboundRequestGuard>.Instance);

    private static IReadOnlyList<IDeliveryDispatcher> AllProductionDispatchers() => new IDeliveryDispatcher[]
    {
        new SftpDeliveryDispatcher(NullLogger<SftpDeliveryDispatcher>.Instance, Guard()),
        new FtpsDeliveryDispatcher(NullLogger<FtpsDeliveryDispatcher>.Instance, Guard()),
        new HttpDeliveryDispatcher(new StubHttpClientFactory(), Guard(), NullLogger<HttpDeliveryDispatcher>.Instance),
        new EmailApiDeliveryDispatcher(new FakeEmailApiClient(), NullLogger<EmailApiDeliveryDispatcher>.Instance),
        new SmtpDeliveryDispatcher(NullLogger<SmtpDeliveryDispatcher>.Instance, Guard()),
        new ErplyDeliveryDispatcher(new IErpConnector[] { new StubErpConnector("erp_erply") }),
        new DirectoDeliveryDispatcher(new IErpConnector[] { new StubErpConnector("erp_directo") }),
    };

    /// <summary>A dispatcher that declares nothing — i.e. the one someone writes next.</summary>
    private sealed class SilentDispatcher : IDeliveryDispatcher
    {
        public string Protocol => "silent";

        public Task<DeliveryResult> DispatchAsync(
            byte[] content, string fileName, string contentType, SupplierDeliveryConfig config,
            string decryptedCredentials, CancellationToken ct, string? idempotencyKey = null)
            => Task.FromResult(new DeliveryResult(false, "HTTP 400", 400));
    }

    /// <summary>
    /// The real <see cref="HttpDeliveryDispatcher"/> with its transport swapped out, so the
    /// Retry-After assertion covers the production parse rather than a re-implementation of it.
    /// </summary>
    private sealed class RecordingHttpDispatcher : HttpDeliveryDispatcher
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        private readonly int? _retryAfterSeconds;

        public RecordingHttpDispatcher(HttpStatusCode status, string body, int? retryAfterSeconds)
            : base(new StubHttpClientFactory(), Guard(), NullLogger<HttpDeliveryDispatcher>.Instance)
        {
            _status = status;
            _body = body;
            _retryAfterSeconds = retryAfterSeconds;
        }

        internal override HttpClient CreateSendClient()
            => new(new FixedHandler(_status, _body, _retryAfterSeconds));
    }

    private sealed class FixedHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        private readonly int? _retryAfterSeconds;

        public FixedHandler(HttpStatusCode status, string body, int? retryAfterSeconds)
        {
            _status = status;
            _body = body;
            _retryAfterSeconds = retryAfterSeconds;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "text/plain"),
            };
            if (_retryAfterSeconds is { } seconds)
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
            return Task.FromResult(response);
        }
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class StubErpConnector : IErpConnector
    {
        public string Protocol { get; }

        public StubErpConnector(string protocol) => Protocol = protocol;

        public Task<ErpDeliveryResult> SendAsync(ErpDeliveryRequest request, CancellationToken ct) =>
            Task.FromResult(new ErpDeliveryResult(true, null));
    }
}
