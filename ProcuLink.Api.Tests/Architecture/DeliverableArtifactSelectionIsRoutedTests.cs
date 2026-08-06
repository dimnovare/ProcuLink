using System.Reflection;
using FluentAssertions;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using static ProcuLink.Api.Tests.Architecture.OutboundArtifactSelectionIlScanner;

namespace ProcuLink.Api.Tests.Architecture;

/// <summary>
/// The build fails here when a NEW production site decides which outbound artifact an order would
/// send, and decides it by recency, without consulting <see cref="OutboundArtifactSelection"/>.
///
/// <para><b>What went wrong once.</b> Seven production paths each answered "which artifact does this
/// order send?" by taking the newest one, and every one of them was correct until re-processing
/// began appending an artifact an operator asked to SEE — an artifact that is, by construction, the
/// newest. WP-35 routed all seven through one rule. <b>Two of the seven were found only by
/// adversarial review, after the first implementation pass was believed complete.</b> That is the
/// whole argument for a guard: the failure mode is silent — no exception, no failing test, nothing
/// in a log — and the outcome is either a supplier receiving a document no human approved, or a
/// preview's SHA-256 published as the record of what was sent.</para>
///
/// <para><b>Shape of the guard.</b> Four things have to be true together, and the log has a named
/// trap for each one missing:</para>
/// <list type="number">
/// <item><description><b>It reads compiled IL, not source text.</b> A regex cannot tell code from a
/// comment; two guards in this repo went silently blind exactly that way.
/// <c>ACommentedOutRoutingCallIsNotEvidenceOfRouting</c> pins that this one does not.</description></item>
/// <item><description><b>It runs against the real assemblies, not only a fixture.</b> A guard
/// exercised only against a fixture proves its plumbing and says nothing about its coverage of the
/// repo — the log records a guard that was dead for the life of a branch under a green fixture
/// test.</description></item>
/// <item><description><b>It has an anti-vacuity floor.</b> A scanner that matches nothing reports
/// clean. <c>TheScannerFindsTheKnownSelectionSitesInTheRealTree</c> fails if the known sites stop
/// being seen.</description></item>
/// <item><description><b>It has a negative control.</b> A detector that fires on everything is
/// noise that gets suppressed. <c>LookingUpOneArtifactByIdentityIsNotASelection</c> pins real
/// production methods that touch artifacts and must stay absent.</description></item>
/// </list>
///
/// <para><b>What the guard does NOT claim.</b> Its unit is the method: it proves a method that
/// selects by recency also consults the rule. It does not prove that every selection <i>inside</i>
/// a method that already consults the rule went through it, and it does not recognise a hand-rolled
/// loop that tracks a maximum <c>CreatedAt</c> without LINQ. Both are stated here rather than left
/// for someone to discover, because a guard believed to be wider than it is, is worse than a narrow
/// one that is understood.</para>
/// </summary>
public sealed class DeliverableArtifactSelectionIsRoutedTests
{
    /// <summary>
    /// The one production site that orders artifacts by recency and deliberately does not narrow to
    /// the deliverable one — with the reason, not an impression of one.
    ///
    /// <para>The order-detail DTO lists EVERY artifact the order holds, newest first, because being
    /// able to inspect a re-processed output is the point of producing it. WP-35 is explicit that
    /// the record keeps showing every row and that only the "what would this order send?" question
    /// is narrowed; narrowing this list would hide the preview from the operator who asked for
    /// it.</para>
    ///
    /// <para>An entry here is not free: <c>TheAllowlistCarriesNoStaleEntries</c> fails if a listed
    /// method stops being a selection site at all, so an entry cannot outlive the code it excuses
    /// and cannot be added "just in case".</para>
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> DeliberatelyUnrouted =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ProcuLink.Api.Controllers.OrdersController.MapToDto"] =
                "Lists every artifact for the order-detail view, newest first — including re-processed "
                + "previews, which is the point of producing them. It answers 'what does this order "
                + "hold?', never 'what would this order send?'.",
        };

    /// <summary>
    /// Sites the scanner is known to find today. A floor, not a census: new selection sites are
    /// allowed to appear (the guard above is what judges them), but these disappearing means the
    /// scanner has stopped seeing the real tree — the failure mode where a guard reports clean
    /// because it matches nothing.
    /// </summary>
    private static readonly string[] KnownSelectionSites =
    [
        "ProcuLink.Api.Controllers.OrdersController.MapToDto",
        "ProcuLink.Api.Services.OrderTransformService.TransformAsync",
        "ProcuLink.Api.Services.PassportService.GetAsync",
        "ProcuLink.Api.Services.ReplayService.ReprocessAsync",
        "ProcuLink.Core.Services.OutboundArtifactSelection.NewestDeliverable",
        "ProcuLink.Infrastructure.Services.DeliveryService.RetryDeliveryAsync",
        "ProcuLink.Infrastructure.Services.StrandedReadyOrderDetectionService.RunAsync",
    ];

    /// <summary>
    /// Real production methods that handle outbound artifacts and must NOT be reported. Each is a
    /// lookup by identity or a bulk sweep — the caller already knows which artifact it means, so
    /// there is no recency question to get wrong. Their existence is asserted separately, because a
    /// negative control that names a method which no longer exists proves nothing.
    /// </summary>
    private static readonly (Type Type, string Method)[] NotSelections =
    [
        (typeof(ProcuLink.Infrastructure.Services.DeliveryService), "DispatchArtifactAsync"),
        (typeof(ProcuLink.Infrastructure.Services.DataErasureService), "EraseOrderAsync"),
        (typeof(ProcuLink.Infrastructure.Services.BlobRetentionService), "RunAsync"),
    ];

    private static IReadOnlyList<SelectionSite> RealTree() => Scan(ProductionAssemblies());

    // ── The guard ─────────────────────────────────────────────────────────────

    [Fact]
    public void EveryProductionRecencySelectionOverAnArtifactConsultsTheRule()
    {
        var violations = RealTree()
            .Where(s => !s.IsRouted)
            .Where(s => !DeliberatelyUnrouted.ContainsKey(s.Key))
            .ToList();

        violations.Should().BeEmpty(
            "picking an outbound artifact by recency is the decision WP-35 exists to make once. "
            + "Each site below orders or aggregates OutboundArtifact by a key and never consults "
            + $"{nameof(OutboundArtifactSelection)}, so a re-processed preview — which is the newest "
            + "artifact by construction — is what it would choose.\n\n"
            + string.Join("\n", violations.Select(v =>
                $"  {v.Display}  —  {v.PickCount} pick(s), {v.EvidenceCount} consultation(s) of the rule"
                + $"\n      [{string.Join(", ", v.Operators)}]"))
            + "\n\nFix it by routing the selection through the rule:\n"
            + $"  in memory : {nameof(OutboundArtifactSelection)}.{nameof(OutboundArtifactSelection.NewestDeliverable)}(order.OutboundArtifacts)\n"
            + $"  on a query: .{nameof(OutboundArtifactSelection.Deliverable)}() before the OrderByDescending\n\n"
            + "If the site genuinely wants every artifact — a listing, an audit, an erasure sweep — add "
            + $"it to {nameof(DeliberatelyUnrouted)} in this file with the reason, so the exemption is "
            + "visible in a diff rather than inferred from silence.");
    }

    // ── Anti-vacuity: the scanner still sees the real tree ────────────────────

    [Fact]
    public void TheScannerFindsTheKnownSelectionSitesInTheRealTree()
    {
        var found = RealTree().Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
        var missing = KnownSelectionSites.Where(k => !found.Contains(k)).ToList();

        missing.Should().BeEmpty(
            "these are the artifact selections WP-35 left in the tree. If the scanner stops seeing "
            + "them it will report a clean build for the same reason a switched-off smoke alarm "
            + "reports no fire — check the operator names, the OutboundArtifact type reference, and "
            + "the async state-machine unwrap before assuming the code changed.");
    }

    [Fact]
    public void TheKnownRoutedSitesReallyShowTheirRouting()
    {
        var byKey = RealTree().ToDictionary(s => s.Key, StringComparer.Ordinal);

        var unroutedKnownSites = KnownSelectionSites
            .Where(k => !DeliberatelyUnrouted.ContainsKey(k))
            .Where(k => !byKey.TryGetValue(k, out var site) || !site.IsRouted)
            .ToList();

        unroutedKnownSites.Should().BeEmpty(
            "every WP-35 site is supposed to consult the rule, and the guard above can only be "
            + "trusted if the evidence half of the scanner works as well as the detection half. A "
            + "scanner that never recognises routing would make this list the whole tree; one that "
            + "recognises it too eagerly would make it empty for the wrong reason, which is what "
            + "TheDefectWithRoutingOnlyInCommentsAndStrings exists to rule out.");
    }

    // ── Negative controls: the detector is not simply "touches artifacts" ─────

    [Fact]
    public void LookingUpOneArtifactByIdentityIsNotASelection()
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
                                 | BindingFlags.Instance | BindingFlags.Static;

        foreach (var (type, method) in NotSelections)
        {
            // Any overload will do — the guard's unit is the method NAME, and asking for a specific
            // signature would make this control fail on an unrelated parameter change.
            type.GetMethods(flags).Any(m => m.Name == method).Should().BeTrue(
                "a negative control that names a method which no longer exists asserts nothing. "
                + $"{type.Name}.{method} was renamed or removed — repoint this control at whatever "
                + "replaced it.");
        }

        var found = RealTree().Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
        var wronglyReported = NotSelections
            .Select(x => $"{x.Type.FullName}.{x.Method}")
            .Where(found.Contains)
            .ToList();

        wronglyReported.Should().BeEmpty(
            "these methods handle outbound artifacts by identity or in bulk — no recency question is "
            + "being answered, so nothing about the deliverable rule applies to them. Reporting them "
            + "would make the guard noise, and a noisy guard gets an allowlist entry instead of a fix.");
    }

    [Fact]
    public void TheAllowlistCarriesNoStaleEntries()
    {
        var found = RealTree().Select(s => s.Key).ToHashSet(StringComparer.Ordinal);

        var stale = DeliberatelyUnrouted.Keys.Where(k => !found.Contains(k)).ToList();

        stale.Should().BeEmpty(
            "an allowlist entry excuses a site that exists. Once the named method stops being a "
            + "selection site the entry is a standing permission for a name, which is how an "
            + "exemption written for one reason ends up covering something else that later takes "
            + "the same name.");
    }

    // ── The rule itself is still where the guard looks for it ────────────────

    [Fact]
    public void TheSelectionRuleStillExistsWhereTheGuardLooksForIt()
    {
        SelectionType.Should().Be(typeof(OutboundArtifactSelection));

        ReprocessKeyMarker.Should().Be("/reprocessed/",
            "the marker is read off the production constant rather than typed into the scanner, so "
            + "this assertion is the one place the expected VALUE is pinned. Changing the storage "
            + "segment is allowed — changing it without noticing that re-processed artifacts already "
            + "in R2 keep the old key is not.");

        RoutingMembers.Should().BeEquivalentTo(
            new[]
            {
                nameof(OutboundArtifactSelection.NewestDeliverable),
                nameof(OutboundArtifactSelection.Deliverable),
                nameof(OutboundArtifactSelection.IsReprocessed),
                nameof(OutboundArtifactSelection.IsReprocessedKey),
                nameof(OutboundArtifactSelection.ReprocessKeyMarker),
                nameof(OutboundArtifactSelection.ReprocessKeySegment),
            },
            "adding a member here widens what counts as proof that a site consulted the rule. "
            + $"{nameof(OutboundArtifactSelection.BuildReprocessKey)} is deliberately NOT here: it "
            + "writes a preview, it does not read the deliverable rule, and a method could call it "
            + "and still pick the newest artifact raw.");
    }

    [Fact]
    public void TheScannedAssembliesCoverEveryProductionAssembly()
    {
        var outputDirectory = Path.GetDirectoryName(typeof(DeliverableArtifactSelectionIsRoutedTests).Assembly.Location)!;

        var shipped = Directory.GetFiles(outputDirectory, "ProcuLink*.dll")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => name is not null && !name.EndsWith(".Tests", StringComparison.Ordinal)
                                            && name != "ProcuLink.TestSupport")
            .ToHashSet(StringComparer.Ordinal)!;

        shipped.Should().BeEquivalentTo(ProductionAssemblyNames,
            "the guard is only as wide as the assemblies it opens. A new production project that "
            + "nobody adds to ProductionAssemblyNames is a place this rule does not reach, and "
            + "nothing else would say so.");
    }

    // ── Fixture half: the scanner can tell the shapes apart ──────────────────

    private static IReadOnlyList<SelectionSite> Fixtures() =>
        Scan([typeof(OutboundArtifactSelectionFixtures).Assembly],
             type => type == typeof(OutboundArtifactSelectionFixtures));

    private static SelectionSite? Fixture(string method) =>
        Fixtures().SingleOrDefault(
            s => s.Key == $"{typeof(OutboundArtifactSelectionFixtures).FullName}.{method}");

    [Theory]
    [InlineData(nameof(OutboundArtifactSelectionFixtures.TheOriginalDefect))]
    [InlineData(nameof(OutboundArtifactSelectionFixtures.TheOriginalDefectOnTheQuerySide))]
    [InlineData(nameof(OutboundArtifactSelectionFixtures.TheDefectProjectedToAnId))]
    [InlineData(nameof(OutboundArtifactSelectionFixtures.TheDefectAsAnAggregate))]
    [InlineData(nameof(OutboundArtifactSelectionFixtures.TheDefectInsideALambda))]
    [InlineData(nameof(OutboundArtifactSelectionFixtures.ListsEveryArtifactNewestFirst))]
    [InlineData(nameof(OutboundArtifactSelectionFixtures.SelectsTwiceButRoutesOnce))]
    public void TheScannerReportsAnUnroutedSelection(string method)
    {
        var site = Fixture(method);

        site.Should().NotBeNull($"{method} selects an OutboundArtifact by recency");
        site!.IsRouted.Should().BeFalse(
            $"{method} never consults {nameof(OutboundArtifactSelection)}, so the guard has to fail on it");
    }

    /// <summary>
    /// The original defect, character for character, is caught. If only one test in this file is
    /// ever read, read this one: a guard that cannot fail on the defect it was written for is
    /// decoration.
    /// </summary>
    [Fact]
    public void TheOriginalDefectIsCaughtVerbatim()
    {
        var site = Fixture(nameof(OutboundArtifactSelectionFixtures.TheOriginalDefect));

        site.Should().NotBeNull();
        site!.Shapes.Should().Contain(SelectionShape.RecencyOrdering);
        site.Operators.Should().ContainSingle()
            .Which.Should().Contain(nameof(OutboundArtifact),
                "the operator has to be the one instantiated over OutboundArtifact — that generic "
                + "argument is the only thing keeping this guard from reporting every "
                + "OrderByDescending in the codebase");
        site.RoutingEvidence.Should().BeEmpty();
    }

    /// <summary>
    /// TRAP 13, answered rather than restated: the specimen names the rule in a comment, in a
    /// commented-out call, and in a string literal, and still selects the newest artifact raw. A
    /// source-text guard would clear it.
    /// </summary>
    [Fact]
    public void ACommentedOutRoutingCallIsNotEvidenceOfRouting()
    {
        var site = Fixture(nameof(OutboundArtifactSelectionFixtures.TheDefectWithRoutingOnlyInCommentsAndStrings));

        site.Should().NotBeNull();
        site!.RoutingEvidence.Should().BeEmpty(
            "the only mentions of the rule in that specimen are a comment, a commented-out call and "
            + "a string literal. None of them is a call, and IL contains none of them.");
    }

    [Theory]
    [InlineData(nameof(OutboundArtifactSelectionFixtures.RoutedThroughTheDeliverableExtension),
                nameof(OutboundArtifactSelection.Deliverable))]
    [InlineData(nameof(OutboundArtifactSelectionFixtures.RoutesFromTheBodyWhileSelectingInALambda),
                nameof(OutboundArtifactSelection.NewestDeliverable))]
    public void TheScannerClearsASelectionThatConsultsTheRule(string method, string expectedMember)
    {
        var site = Fixture(method);

        site.Should().NotBeNull($"{method} still performs the ordering itself, so it must be judged");
        site!.IsRouted.Should().BeTrue();
        site.RoutingEvidence.Should().Contain(e => e.Contains(expectedMember, StringComparison.Ordinal));
    }

    /// <summary>
    /// The one place the scanner accepts a bare string literal, and why it has to: C# inlines a
    /// <c>const string</c> at every use site, so <c>OutboundArtifactSelection.ReprocessKeyMarker</c>
    /// leaves no reference to the type that declares it. This is the shape the stranded-ready
    /// sweep's candidate query is written in — the one send path with no operator behind it.
    /// </summary>
    [Fact]
    public void TheInlinedMarkerConstantCountsAsConsultingTheRule()
    {
        var site = Fixture(nameof(OutboundArtifactSelectionFixtures.RoutedThroughTheInlinedMarkerConstant));

        site.Should().NotBeNull();
        site!.Shapes.Should().Contain(SelectionShape.AggregatePick);
        site.IsRouted.Should().BeTrue();
        site.RoutingEvidence.Should().Contain(e => e.Contains(ReprocessKeyMarker, StringComparison.Ordinal));
    }

    /// <summary>
    /// The measured hole that made this guard count instead of asking a yes/no question.
    ///
    /// <para>Both mutants of <c>StrandedReadyOrderDetectionService.RunAsync</c> — delete the marker
    /// from the candidate query's cutoff, delete <c>Deliverable()</c> from the artifact it
    /// dispatches — SURVIVED a boolean "does this method mention the rule" guard, because the
    /// surviving half's evidence sat in the same method. That method is the one send path with no
    /// operator in the loop.</para>
    /// </summary>
    [Fact]
    public void TwoPicksNeedTwoConsultationsOfTheRule()
    {
        var underRouted = Fixture(nameof(OutboundArtifactSelectionFixtures.SelectsTwiceButRoutesOnce));
        underRouted.Should().NotBeNull();
        underRouted!.PickCount.Should().Be(2);
        underRouted.EvidenceCount.Should().Be(1);
        underRouted.IsRouted.Should().BeFalse(
            "one Deliverable() call cannot answer for both the Max cutoff and the artifact pick");

        var routed = Fixture(nameof(OutboundArtifactSelectionFixtures.SelectsTwiceAndRoutesTwice));
        routed.Should().NotBeNull();
        routed!.PickCount.Should().Be(2);
        routed.IsRouted.Should().BeTrue("each pick carries its own discriminator, as production does");
    }

    /// <summary>
    /// <c>ThenBy</c> cannot start a pick, so a tie-broken ordering is one pick and not two. Without
    /// this, writing <c>OrderByDescending(…).ThenBy(…)</c> would demand a second, meaningless
    /// consultation of the rule — the kind of false positive that gets a guard allowlisted into
    /// uselessness.
    /// </summary>
    [Fact]
    public void ATieBreakerDoesNotCountAsASecondPick()
    {
        var site = Fixture(nameof(OutboundArtifactSelectionFixtures.OrdersOnceWithASecondaryThenBy));

        site.Should().NotBeNull();
        site!.Operators.Should().HaveCount(2, "OrderByDescending and ThenBy are both detected");
        site.PickCount.Should().Be(1, "only OrderByDescending starts a pick");
        site.EvidenceCount.Should().Be(1);
        site.IsRouted.Should().BeTrue();
    }

    [Fact]
    public void ADelegatedSelectionIsNotReportedBecauseItNoLongerSelects()
    {
        Fixture(nameof(OutboundArtifactSelectionFixtures.RoutedThroughNewestDeliverable)).Should().BeNull(
            "handing the whole question to the rule leaves no ordering behind to judge. Three "
            + "production sites are written this way — the ops requeue and both OrdersController "
            + "resend paths — which is why the anti-vacuity floor pins the sites that DO still order, "
            + "not a count of everything WP-35 touched.");
    }

    [Theory]
    [InlineData(nameof(OutboundArtifactSelectionFixtures.LooksUpOneArtifactByIdentity))]
    [InlineData(nameof(OutboundArtifactSelectionFixtures.TakesEveryArtifactsKey))]
    [InlineData(nameof(OutboundArtifactSelectionFixtures.OrdersSomethingElseByRecency))]
    public void TheScannerIgnoresWhatIsNotARecencySelectionOverAnArtifact(string method) =>
        Fixture(method).Should().BeNull();
}
