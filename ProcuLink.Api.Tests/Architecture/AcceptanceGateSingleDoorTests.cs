using System.Text.RegularExpressions;
using ProcuLink.Api.Controllers;
using ProcuLink.Api.Jobs;
using ProcuLink.Api.Services;
using Xunit;

namespace ProcuLink.Api.Tests.Architecture;

/// <summary>
/// WP-17 — the doors an order can leave through, and the guarantee that each one is gated.
///
/// <para><b>There are TWO doors, not one.</b> The transform door
/// (<c>OrderTransformService.TransformAsync</c>) is where a document is produced; the DELIVERY door
/// is where a document that already exists is put in front of a supplier. The second one carries
/// most of the traffic: <c>Organisation.AutoDeliver</c> defaults to false, so a live order comes to
/// rest in <c>ready_to_deliver</c> and waits for a person to press Send — and the inbox's bulk
/// "Send selected" reaches the supplier through <c>POST /api/orders/{id}/redeliver</c>, never
/// through the transform. This file pins both.</para>
///
/// <para><b>What it defends.</b> The behavioural proof that a blocked order does not transform
/// (<c>AcceptanceGateBlocksTransformTests</c>, <c>AcceptanceGateEntryPathsPostgresTests</c>) can only
/// generalise to "every ingress channel" while <c>IOrderService.TransformAsync</c> has exactly one
/// caller and <c>TransformOrderJob</c> exactly one enqueuer. The day someone adds a second trigger —
/// an auto-send after parse, a scheduled sweep, an ERP poller — the behavioural tests would keep
/// passing while a real hole opened. Likewise, the delivery-side proofs
/// (<c>AcceptanceGateBlocksDeliveryTests</c>) cover the three manual-send endpoints that exist; this
/// is what notices a fourth.</para>
///
/// <para><b>Structural, by SYMBOL.</b> The failure being guarded is a call site nothing yet
/// references, which no runtime test can observe. It is found by resolving the metadata tokens in
/// compiled IL (see <see cref="TransformTriggerDetector"/>) rather than by matching source text —
/// the previous regex form saw only half of the eight plausible shapes in
/// <see cref="TransformTriggerShapes"/>, all of the misses being the scheduled and expression-built
/// enqueues that are exactly the futures this file says it guards.</para>
/// </summary>
public sealed class AcceptanceGateSingleDoorTests
{
    /// <summary>
    /// Types allowed to START a transform, and why each one is legitimate.
    ///
    /// <para>Every claim in these strings is asserted somewhere. "The only transform trigger" is
    /// this file's own <see cref="OnlyOneProductionType_canStartATransform"/>; "the workshop's Send
    /// button calls it" is <c>AcceptanceGateEntryPathsPostgresTests.BrowserSend_isRefused</c>, which
    /// drives the endpoint and the job it enqueues. What is NOT claimed — and used to be — is that
    /// the inbox bulk-send calls it. It does not: it calls <c>/redeliver</c>, which is why that
    /// endpoint is gated separately and pinned by
    /// <see cref="TheManualSendEndpoints_consultTheGate"/>.</para>
    /// </summary>
    private static readonly IReadOnlyDictionary<Type, string> KnownEnqueuers =
        new Dictionary<Type, string>
        {
            [typeof(OrdersController)] =
                "POST /api/orders/{id}/transform — the only trigger. The order workshop's Send button "
              + "calls it, and orders arriving over REST ingress or inbound email are driven through "
              + "the same job. The inbox's bulk 'Send selected' does NOT: it calls POST "
              + "/api/orders/{id}/redeliver, gated separately in OrdersController.Redeliver.",
        };

    /// <summary>Types allowed to RUN a transform once one has been started.</summary>
    private static readonly IReadOnlyDictionary<Type, string> KnownRunners =
        new Dictionary<Type, string>
        {
            [typeof(TransformOrderJob)] =
                "The Hangfire job. Calls IOrderService.TransformAsync, which delegates to the gated "
              + "OrderTransformService.",
        };

    // ── The transform door ────────────────────────────────────────────────────

    [Fact]
    public void OnlyOneProductionType_canStartATransform()
    {
        var callers = TransformTriggerDetector.TypesThatCanStartATransform()
            // The job's own static factory is the definition, not a second trigger.
            .Where(t => t != typeof(TransformOrderJob))
            .ToList();

        AssertSameSet(callers, KnownEnqueuers.Keys,
            "The set of types that can start a transform changed. A NEW trigger does not inherit the "
          + "WP-17 acceptance gate for free — confirm it goes through OrderTransformService.TransformAsync, "
          + "then record it in KnownEnqueuers with the reason.");
    }

    [Fact]
    public void OnlyOneProductionType_callsIOrderServiceTransformAsync()
    {
        var callers = TransformTriggerDetector.TypesThatRunATransform()
            // OrderService's own one-line delegation is the door itself, not a consumer of it.
            .Where(t => t != typeof(OrderService))
            .ToList();

        AssertSameSet(callers, KnownRunners.Keys,
            "The set of types that run a transform changed. Every runner passes through the gate "
          + "inside OrderTransformService; a caller that bypasses IOrderService would not.");
    }

    /// <summary>
    /// The gate must actually be consulted. A behavioural suite proves this far better — but it
    /// proves it only for the orders it seeds, and a refactor that drops the call while leaving the
    /// type wired would keep every no-profile test green.
    /// </summary>
    [Fact]
    public void TheTransformService_consultsTheGate()
    {
        // Matched by name because OrderTransformService is internal to ProcuLink.Api — the detector
        // sees it through reflection regardless, and naming it here keeps the assertion pointed at
        // the type that actually holds the call rather than at its public facade.
        Assert.Contains(
            TransformTriggerDetector.TypesThatConsultTheGate(),
            t => t.FullName == "ProcuLink.Api.Services.OrderTransformService");
    }

    // ── The delivery door ─────────────────────────────────────────────────────

    /// <summary>
    /// The endpoints that dispatch an ALREADY-STORED artifact on a human's say-so must consult the
    /// gate: <c>OrdersController</c> (redeliver — the inbox bulk-send and a parked order's "Send
    /// again" — and retry-delivery) and <c>OpsController</c> (the operator requeue escalation).
    ///
    /// <para>This is the assertion the corrected <see cref="KnownEnqueuers"/> justification owes:
    /// the reason it is now honest to say "the inbox bulk-send does not call /transform" is that the
    /// path it DOES call is gated, and here is the check that it stays that way.</para>
    /// </summary>
    [Fact]
    public void TheManualSendEndpoints_consultTheGate()
    {
        var consulting = TransformTriggerDetector.TypesThatConsultTheGate();

        Assert.Contains(typeof(OrdersController), consulting);
        Assert.Contains(typeof(OpsController), consulting);
    }

    /// <summary>
    /// Only known types may enqueue a DELIVERY of a stored artifact bypassing AutoDeliver
    /// (<c>DeliverOrderJob.EnqueueRedeliver</c>). Both of them are the manual-send endpoints above,
    /// and both are gated — so a third one appearing is a new ungated send path, which is exactly
    /// the defect this work package was reopened for.
    /// </summary>
    [Fact]
    public void OnlyTheGatedEndpoints_canForceARedelivery()
    {
        var forced = ForcedRedeliverCall.Matches(Text("ProcuLink.Api/Controllers/OrdersController.cs")).Count
                   + ForcedRedeliverCall.Matches(Text("ProcuLink.Api/Controllers/OpsController.cs")).Count;
        Assert.True(forced >= 2, "the two known forced-redelivery call sites are gone — did they move?");

        var callers = Sources()
            .Where(f => ForcedRedeliverCall.IsMatch(f.Text))
            .Select(f => f.Path)
            .Where(p => p != "ProcuLink.Api/Jobs/DeliverOrderJob.cs")   // the factory's definition
            .Distinct(StringComparer.Ordinal)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            callers.SequenceEqual(new[]
            {
                "ProcuLink.Api/Controllers/OpsController.cs",
                "ProcuLink.Api/Controllers/OrdersController.cs",
            }, StringComparer.Ordinal),
            "The set of files that can force a delivery of a stored artifact changed; found: "
          + string.Join(", ", callers)
          + ". A forced redelivery is a manual send — it must consult IAcceptanceGate before enqueuing, "
          + "or it reopens the hole where the inbox bulk-send shipped orders the supplier refuses.");
    }

    // ── The tripwire's own sensitivity ────────────────────────────────────────

    /// <summary>
    /// The tripwire must SEE every plausible way to add a trigger. A guard that only recognises the
    /// exact formatting of the one call site that exists today guards against reformatting that call
    /// site, not against a new one — and the doc comment above promises the latter.
    /// </summary>
    [Fact]
    public void TheTripwire_seesEveryPlausibleTriggerShape()
    {
        var seen = TransformTriggerDetector.TypesThatTriggerATransform(
            typeof(TransformTriggerShapes).Assembly);

        var missed = TransformTriggerShapes.All
            .Where(s => !seen.Contains(s.Type))
            .Select(s => $"  • {s.Description} ({s.Type.Name})")
            .ToList();

        Assert.True(missed.Count == 0,
            $"the tripwire does not see {missed.Count} of {TransformTriggerShapes.All.Count} plausible ways "
          + "to start a transform, so none of them would fire it:\n" + string.Join("\n", missed));
    }

    /// <summary>
    /// …and it must NOT fire on the name collision. <c>ITransformService.TransformAsync</c> shares
    /// the name and has a dozen legitimate call sites; <c>ReplayService</c> is one of them, and it
    /// starts no transform. A detector that answered "yes" to any method called
    /// <c>TransformAsync</c> would pass every test above while making the two known-set tests
    /// permanently red for unrelated reasons — and would be switched off within a week.
    /// </summary>
    [Fact]
    public void TheTripwire_doesNotFireOnTheSameNamedTransformerCall()
    {
        var seen = TransformTriggerDetector.TypesThatTriggerATransform();

        Assert.DoesNotContain(seen, t => t.FullName == "ProcuLink.Api.Services.ReplayService");
    }

    // ── Composition roots ─────────────────────────────────────────────────────

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
        var program = Text(host);

        Assert.Matches(@"AddScoped\s*<\s*IAcceptanceGate\s*,\s*AcceptanceGate\s*>", program);
        Assert.Matches(@"AddScoped\s*<\s*ISupplierAcceptanceService\s*,\s*SupplierAcceptanceService\s*>", program);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void AssertSameSet(IReadOnlyCollection<Type> actual, IEnumerable<Type> expected, string why)
    {
        var found = actual.Select(TransformTriggerDetector.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
        var want  = expected.Select(TransformTriggerDetector.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();

        Assert.True(found.SequenceEqual(want, StringComparer.Ordinal),
            $"{why}\nExpected: {string.Join(", ", want)}\nFound:    {string.Join(", ", found)}");
    }

    /// <summary><c>DeliverOrderJob.EnqueueRedeliver(…)</c> — the forced, AutoDeliver-bypassing
    /// dispatch of a stored artifact. Still textual: this one is about which FILES exist, and the
    /// call is a plain static invocation with no expression tree to resolve.</summary>
    private static readonly Regex ForcedRedeliverCall =
        new(@"DeliverOrderJob\s*\.\s*EnqueueRedeliver\s*\(", RegexOptions.Compiled);

    /// <summary>
    /// Production sources, comment-stripped by the orphan guard's scanner.
    ///
    /// <para>This file used to carry its own copy of the stripper, because
    /// <c>OrphanDetector.StripComments</c> was then two regex passes with block comments removed
    /// FIRST — and a regex cannot tell a comment opener from the same two characters inside a
    /// string. Applied to <c>ProcuLink.Api/Program.cs</c> it read the <c>/*</c> sitting inside
    /// <c>// … a future `https://*.vercel.app`</c> as an OPEN block comment, closed it at the
    /// <c>catch { /* swallow */ }</c> 681 lines below it (:421 → :1102 as the file stands today),
    /// and deleted everything between — all but two of the composition root's service
    /// registrations, which is most of what <see cref="BothHosts_registerTheGate"/> is looking
    /// at.</para>
    ///
    /// <para>#92 replaced both passes with a single left-to-right scanner that tracks literal
    /// state, so there is no longer a pass order to get wrong and no reason for a second copy: the
    /// duplicate is gone, and <c>OrphanGuardTests</c> holds the one that remains with regression
    /// tests over every literal and comment form. Reordering the passes, which is what this copy
    /// did, only moved the hole — a <c>//</c> inside a string truncates the line instead.</para>
    ///
    /// <para><see cref="OrphanDetector.LoadSources"/> rather than <c>RepoScan.Run()</c>: the corpus
    /// is all this file needs, and going through the full scan would re-run orphan detection —
    /// every DbSet against every file — to obtain it.</para>
    /// </summary>
    private static readonly Lazy<IReadOnlyList<(string Path, string Text)>> CachedSources =
        new(ReadSources, isThreadSafe: true);

    private static IReadOnlyList<(string Path, string Text)> Sources() => CachedSources.Value;

    private static IReadOnlyList<(string Path, string Text)> ReadSources()
    {
        var root = OrphanDetector.FindRepoRoot();

        // Paths arrive with the host separator — Windows dev, Linux CI — and every lookup in this
        // file is written with '/'. Normalising here is what keeps Text() from throwing on one OS.
        var files = OrphanDetector
            .LoadSources(root, OrphanDetector.ProductionProjectDirectories(root))
            .Select(f => (Path: Normalise(f.RelativePath), f.Text))
            .ToList();

        Assert.True(files.Count > 100,
            $"only {files.Count} production sources found under {root} — the scan is broken");
        return files;
    }

    private static string Text(string relativePath) =>
        Sources().Single(f => f.Path == relativePath).Text;

    private static string Normalise(string relativePath) => relativePath.Replace('\\', '/');
}
