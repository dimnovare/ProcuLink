using System.IO;
using System.Text.RegularExpressions;
using ProcuLink.Core.Services;
using Xunit;

namespace ProcuLink.Api.Tests.Architecture;

/// <summary>
/// WP-22 — the push-ingress creation seam must be RESOLVABLE, in both hosts.
///
/// <para><b>The hole this closes.</b> <c>IngressController.ReceiveOrder</c> and
/// <c>InboundEmailRouter</c> both take <see cref="IClaimedOrderCreator"/>, the controller through
/// <c>[FromServices]</c>. Every test in the suite hands them a mock or a hand-rolled double, so a
/// missing DI registration is invisible to all of them — and would surface only as a 500 on every
/// REST-ingress POST and a dead Postmark webhook, in production. Both dedupe channels would be
/// unreachable while their tests stayed green.</para>
///
/// <para><b>Why source text and not a built provider.</b> This is the same idiom
/// <c>AcceptanceGateSingleDoorTests.BothHosts_registerTheGate</c> uses, for the same reason: the
/// composition-root test (<c>OrderServiceCompositionRootTests</c>) MIRRORS the registrations rather
/// than reading <c>Program.cs</c>, so it proves the object graph constructs — not that either host
/// actually registers this seam. Only reading the host files can prove that.</para>
///
/// <para><b>The Worker matters as much as the API.</b> The Worker resolves <c>IOrderService</c> for
/// its jobs and shares this composition root; registering the seam in one host only is the exact
/// shape of the WP-17 gap, where enforcement lived in the API process and nowhere the work ran.</para>
/// </summary>
public sealed class PushIngressSeamRegistrationTests
{
    [Theory]
    [InlineData("ProcuLink.Api/Program.cs")]
    [InlineData("ProcuLink.Worker/Program.cs")]
    public void BothHosts_registerTheClaimedOrderCreator(string host)
    {
        var program = HostSource(host);

        // The registration forwards to the SAME scoped OrderService instance rather than
        // constructing a second one — an independent instance would give the push channels a
        // different DbContext-scoped unit of work from the one the request already holds.
        var registration = new Regex(
            @"AddScoped\s*<\s*IClaimedOrderCreator\s*>\s*\(\s*sp\s*=>\s*\(\s*OrderService\s*\)\s*sp\.GetRequiredService\s*<\s*IOrderService\s*>\s*\(\s*\)\s*\)");

        var match = registration.Match(program);
        Assert.True(match.Success, $"{host} must register IClaimedOrderCreator off the scoped OrderService.");

        // POSITION MATTERS, and a bare Assert.Matches misses it. `builder.Build()` snapshots the
        // service collection: a registration written after that line still compiles, still reads
        // correctly, and is simply never seen by the provider — so every REST-ingress POST 500s on
        // `[FromServices] IClaimedOrderCreator` and the Postmark webhook goes dead, while a
        // whole-file regex stays perfectly green. That is the exact failure this file exists to
        // prevent, so the guard has to assert the ORDER, not just the presence.
        var build = Regex.Match(program, @"\bbuilder\s*\.\s*Build\s*\(\s*\)");
        Assert.True(build.Success, $"{host} must call builder.Build().");
        Assert.True(match.Index < build.Index,
            $"{host} registers IClaimedOrderCreator at offset {match.Index}, AFTER builder.Build() at " +
            $"{build.Index}. Registrations added after Build() are invisible to the built provider.");
    }

    /// <summary>
    /// The seam is only worth registering if <c>OrderService</c> still implements it. An explicit
    /// interface implementation is invisible at the call site, so dropping it from the declaration
    /// list compiles everywhere except the registration cast — which is a runtime
    /// <c>InvalidCastException</c>, not a build error.
    /// </summary>
    [Fact]
    public void OrderService_implements_theClaimedCreatorSeam()
    {
        Assert.True(
            typeof(IClaimedOrderCreator).IsAssignableFrom(typeof(ProcuLink.Api.Services.OrderService)),
            "OrderService must implement IClaimedOrderCreator — both Program.cs files cast the " +
            "resolved IOrderService to OrderService and hand it back as this seam.");
    }

    /// <summary>
    /// Reads a host file with comments stripped, so a registration that has been COMMENTED OUT
    /// reads as absent rather than as present. Uses <c>OrphanDetector.StripComments</c> — the one
    /// stripper #92/#95 consolidated the repo onto — rather than a second copy: the last duplicate
    /// of that logic had a pass-order bug that silently deleted most of <c>Program.cs</c> before the
    /// assertions ran, which is precisely how a registration guard turns vacuous.
    /// </summary>
    private static string HostSource(string relativePath) =>
        OrphanDetector.StripComments(
            File.ReadAllText(Path.Combine(OrphanDetector.FindRepoRoot(), relativePath)));
}
