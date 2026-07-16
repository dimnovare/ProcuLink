using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Core.Services.Erp;
using ProcuLink.Infrastructure.Services.Dispatchers;
using ProcuLink.Infrastructure.Services.Security;
using ProcuLink.Infrastructure.Tests.TestDoubles;

namespace ProcuLink.Infrastructure.Tests.Services.Dispatchers;

/// <summary>
/// Every production dispatcher must state, explicitly and on purpose, whether re-sending the
/// same artifact after an UNKNOWN outcome (a crash-recovery re-drive) can duplicate at the
/// counterparty. DeliveryService parks instead of re-sending when the tier is Unsafe.
///
/// This test is the enforcement point: a new production dispatcher fails here until someone
/// lists it and thinks about its idempotency contract. The interface default (Unsafe) is the
/// fail-safe backstop, not a substitute for that thought.
/// </summary>
public class DispatcherResendSafetyTests
{
    public static TheoryData<string, ResendSafety> ExpectedTiers => new()
    {
        // Deterministic overwrite filename — re-sending overwrites the same file.
        { "sftp", ResendSafety.Safe },
        { "ftps", ResendSafety.Safe },
        // Sends Idempotency-Key + X-Message-Id; honouring them is the supplier's choice.
        { "http", ResendSafety.BestEffort },
        // Message-ID dedup by a receiving MTA is best-effort and rarely applied.
        { "email", ResendSafety.Unsafe },
        { "smtp", ResendSafety.Unsafe },
        // No dedupe signal reaches the ERP endpoint at all.
        { "erp_erply", ResendSafety.Unsafe },
        { "erp_directo", ResendSafety.Unsafe },
    };

    [Theory]
    [MemberData(nameof(ExpectedTiers))]
    public void Dispatcher_DeclaresExpectedResendSafety(string protocol, ResendSafety expected)
    {
        var dispatcher = AllProductionDispatchers()
            .Single(d => string.Equals(d.Protocol, protocol, StringComparison.OrdinalIgnoreCase));

        dispatcher.ResendSafety.Should().Be(expected);
    }

    [Fact]
    public void EveryProductionDispatcher_IsCoveredByThisTest()
    {
        var covered = ExpectedTiers.Select(row => (string)row[0]!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var actual = AllProductionDispatchers().Select(d => d.Protocol).ToHashSet(StringComparer.OrdinalIgnoreCase);

        actual.Should().BeEquivalentTo(covered,
            "a new delivery channel must declare its re-send safety on purpose — add it to ExpectedTiers");
    }

    // Real OutboundRequestGuard over an empty (in-memory) configuration — the dispatchers built
    // with it here are never dispatched, only inspected for Protocol/ResendSafety, so the guard's
    // actual SSRF decision is irrelevant. Mirrors the AllowAllGuard() pattern used by the sibling
    // dispatcher test files.
    private static OutboundRequestGuard Guard() =>
        new(new ConfigurationBuilder().Build(), NullLogger<OutboundRequestGuard>.Instance);

    private static IReadOnlyList<IDeliveryDispatcher> AllProductionDispatchers() => new IDeliveryDispatcher[]
    {
        new SftpDeliveryDispatcher(NullLogger<SftpDeliveryDispatcher>.Instance, Guard()),
        new FtpsDeliveryDispatcher(NullLogger<FtpsDeliveryDispatcher>.Instance, Guard()),
        new HttpDeliveryDispatcher(new FakeHttpClientFactory(), Guard(), NullLogger<HttpDeliveryDispatcher>.Instance),
        new EmailApiDeliveryDispatcher(new FakeEmailApiClient(), NullLogger<EmailApiDeliveryDispatcher>.Instance),
        new SmtpDeliveryDispatcher(NullLogger<SmtpDeliveryDispatcher>.Instance, Guard()),
        new ErplyDeliveryDispatcher(new IErpConnector[] { new FakeErpConnector("erp_erply") }),
        new DirectoDeliveryDispatcher(new IErpConnector[] { new FakeErpConnector("erp_directo") }),
    };

    // Minimal IHttpClientFactory double — HttpDeliveryDispatcher's constructor requires one but
    // this test never calls DispatchAsync, so no client configuration is needed. No reusable fake
    // existed for this interface in TestDoubles/ or sibling dispatcher tests (which use an inline
    // Moq mock instead), so this is the smallest fake that satisfies the constructor.
    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    // Minimal IErpConnector double — no reusable fake existed (ErpConnectorTests.cs exercises the
    // real ErplyConnector/DirectoDeliveryDispatcher — DirectoConnector against a fake HTTP factory,
    // not an IErpConnector double). Only Protocol is exercised here.
    private sealed class FakeErpConnector : IErpConnector
    {
        public string Protocol { get; }

        public FakeErpConnector(string protocol)
        {
            Protocol = protocol;
        }

        public Task<ErpDeliveryResult> SendAsync(ErpDeliveryRequest request, CancellationToken ct) =>
            Task.FromResult(new ErpDeliveryResult(true, null));
    }
}
