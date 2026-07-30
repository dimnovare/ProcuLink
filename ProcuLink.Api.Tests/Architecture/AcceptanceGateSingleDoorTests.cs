using System.Text.RegularExpressions;
using Xunit;

namespace ProcuLink.Api.Tests.Architecture;

/// <summary>
/// WP-17 — the acceptance gate protects EVERY entry path because there is only ONE door.
///
/// <para><b>The claim this file defends.</b> The gate lives in
/// <c>OrderTransformService.TransformAsync</c>. That is sufficient for browser send, inbox
/// bulk-send, REST ingress and inbound email only while those really are the same code path:
/// <c>IOrderService.TransformAsync</c> is called by <c>TransformOrderJob</c> alone, and
/// <c>TransformOrderJob</c> is enqueued by <c>OrdersController.Transform</c> alone. The behavioural
/// proof lives in <c>AcceptanceGateEntryPathsPostgresTests</c>; it can only generalise to "every
/// path" while this stays true, and the day someone adds a second trigger — an auto-send after
/// parse, a scheduled sweep, an ERP poller — the behavioural tests would keep passing while a real
/// hole opened. This is the tripwire for that.</para>
///
/// <para>Textual, and deliberately so: the failure being guarded is a NEW call site that nothing
/// yet references, which no runtime test can observe. Comments are stripped before matching, so a
/// doc-comment mention of <c>TransformOrderJob</c> (there are several) never counts as a call.</para>
/// </summary>
public sealed class AcceptanceGateSingleDoorTests
{
    /// <summary>Files allowed to START a transform, and why each one is legitimate.</summary>
    private static readonly IReadOnlyDictionary<string, string> KnownEnqueuers =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ProcuLink.Api/Controllers/OrdersController.cs"] =
                "POST /api/orders/{id}/transform — the only trigger. Browser send and inbox bulk-send "
              + "both call it; REST-ingress and email-ingested orders are sent through it too.",
        };

    /// <summary>Files allowed to RUN a transform once one has been started.</summary>
    private static readonly IReadOnlyDictionary<string, string> KnownRunners =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ProcuLink.Api/Jobs/TransformOrderJob.cs"] =
                "The Hangfire job. Calls IOrderService.TransformAsync, which delegates to the gated "
              + "OrderTransformService.",
        };

    private static readonly Regex EnqueueCall =
        new(@"TransformOrderJob\s*\.\s*Enqueue\s*\(", RegexOptions.Compiled);

    private static readonly Regex JobEnqueue =
        new(@"Enqueue\s*<\s*TransformOrderJob\s*>", RegexOptions.Compiled);

    /// <summary>
    /// <c>_orderService.TransformAsync(orgId, orderId, format, ct)</c> — four arguments, the
    /// <see cref="ProcuLink.Core.Services.IOrderService"/> shape. Deliberately NOT a bare
    /// <c>.TransformAsync(</c>: <c>ITransformService.TransformAsync</c> shares the name and has a
    /// dozen legitimate call sites, and a check that fires on those would be turned off within a week.
    /// </summary>
    private static readonly Regex OrderServiceTransformCall =
        new(@"\.\s*TransformAsync\s*\(\s*[A-Za-z_][A-Za-z0-9_]*\s*,\s*[A-Za-z_][A-Za-z0-9_]*\s*,\s*"
          + @"(outputFormat|format)\s*,\s*ct\s*\)", RegexOptions.Compiled);

    [Fact]
    public void OnlyOneProductionFile_canEnqueueATransform()
    {
        var scan = RepoScan.Run();

        var callers = scan.Sources
            .Where(f => EnqueueCall.IsMatch(f.Text) || JobEnqueue.IsMatch(f.Text))
            .Select(f => Normalise(f.RelativePath))
            // The job's own static factory method is the definition, not a second trigger.
            .Where(p => p != "ProcuLink.Api/Jobs/TransformOrderJob.cs")
            .Distinct(StringComparer.Ordinal)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            callers.SequenceEqual(KnownEnqueuers.Keys.OrderBy(k => k, StringComparer.Ordinal), StringComparer.Ordinal),
            "The set of files that can start a transform changed. Expected: "
          + string.Join(", ", KnownEnqueuers.Keys.OrderBy(k => k, StringComparer.Ordinal))
          + "; found: " + string.Join(", ", callers)
          + ". A NEW trigger does not inherit the WP-17 acceptance gate for free — confirm it goes "
          + "through OrderTransformService.TransformAsync, then record it in KnownEnqueuers with the reason.");
    }

    [Fact]
    public void OnlyOneProductionFile_callsIOrderServiceTransformAsync()
    {
        var scan = RepoScan.Run();

        var callers = scan.Sources
            .Where(f => OrderServiceTransformCall.IsMatch(f.Text))
            .Select(f => Normalise(f.RelativePath))
            // OrderService's own one-line delegation and the sub-service that implements it are the
            // door itself, not consumers of it.
            .Where(p => p is not "ProcuLink.Api/Services/OrderService.cs"
                            and not "ProcuLink.Api/Services/Orders/OrderTransformService.cs")
            .Distinct(StringComparer.Ordinal)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            callers.SequenceEqual(KnownRunners.Keys.OrderBy(k => k, StringComparer.Ordinal), StringComparer.Ordinal),
            "The set of files that run a transform changed. Expected: "
          + string.Join(", ", KnownRunners.Keys.OrderBy(k => k, StringComparer.Ordinal))
          + "; found: " + string.Join(", ", callers)
          + ". Every runner passes through the gate inside OrderTransformService; a caller that "
          + "bypasses IOrderService would not.");
    }

    /// <summary>
    /// The gate must actually be consulted. A behavioural suite proves this far better — but it
    /// proves it only for the orders it seeds, and a refactor that drops the call while leaving the
    /// type wired would keep every no-profile test green. One line of source is the cheap tripwire.
    /// </summary>
    [Fact]
    public void TheTransformService_consultsTheGate()
    {
        var scan = RepoScan.Run();

        var transform = scan.Sources.Single(f =>
            Normalise(f.RelativePath) == "ProcuLink.Api/Services/Orders/OrderTransformService.cs");

        Assert.Matches(@"_acceptanceGate\s*\.\s*EvaluateAsync\s*\(", transform.Text);
    }

    /// <summary>
    /// BOTH hosts must register the gate. The Worker is where <c>TransformOrderJob</c> runs, and
    /// before WP-17 it did not register <c>ISupplierAcceptanceService</c> at all — an enforcement
    /// service registered only in the API process would have been enforcement in the one process
    /// that never transforms anything.
    /// </summary>
    [Theory]
    [InlineData("ProcuLink.Api/Program.cs")]
    [InlineData("ProcuLink.Worker/Program.cs")]
    public void BothHosts_registerTheGate(string host)
    {
        var scan = RepoScan.Run();
        var program = scan.Sources.Single(f => Normalise(f.RelativePath) == host);

        Assert.Matches(@"AddScoped\s*<\s*IAcceptanceGate\s*,\s*AcceptanceGate\s*>", program.Text);
        Assert.Matches(@"AddScoped\s*<\s*ISupplierAcceptanceService\s*,\s*SupplierAcceptanceService\s*>", program.Text);
    }

    private static string Normalise(string relativePath) => relativePath.Replace('\\', '/');
}
