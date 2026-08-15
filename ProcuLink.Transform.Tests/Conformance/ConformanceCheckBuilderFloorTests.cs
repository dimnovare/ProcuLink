using FluentAssertions;
using ProcuLink.Transform.Conformance;
using Xunit;

namespace ProcuLink.Transform.Tests.Conformance;

/// <summary>
/// A report that examined nothing must not report favourably.
///
/// <para><b>The defect these pin.</b> <see cref="ConformanceCheckBuilder.Build"/> computed its
/// verdict as <c>_checks.All(c =&gt; c.Passed || c.Severity != Error)</c>. <c>All()</c> is true on an
/// empty sequence, so a builder that added no checks produced <c>OverallPass == true</c>, and
/// <see cref="ConformanceReport.ToMarkdown"/> rendered "**Result:** PASS" into a document the
/// product invites customers to forward to a supplier or an access point as evidence. A pass over
/// an examination that never happened is the strongest possible version of the failure this whole
/// area exists to remove.</para>
///
/// <para><b>Why test something no checker can reach.</b> All five live checkers add a first check
/// unconditionally, so nothing in production hits the empty case today. That is a coincidence of
/// their current control flow, not a property of the builder — one early return, one guard clause,
/// or one new checker restores it, and the symptom is a downloadable PASS rather than a crash, so
/// nothing else would catch it. The floor belongs in the builder because the builder is the single
/// construction site for both records (verified: <c>new ConformanceReport(...)</c> and
/// <c>new ConformanceCheck(...)</c> appear nowhere else in the solution), which makes it complete
/// coverage rather than one patched path.</para>
///
/// <para><b>These assert on the ABSENCE of a favourable claim, deliberately.</b> A test written
/// against ProcuLink's own conformant output cannot detect this class of fault — the document
/// passes, so PASS is the expected answer and the vacuity is invisible. The control has to be the
/// empty report itself, and the assertion has to be that the claim is not made.</para>
/// </summary>
public class ConformanceCheckBuilderFloorTests
{
    private static ConformanceCheckBuilder NewBuilder() =>
        new(StandardsProfile.Ubl21Order, "OASIS UBL 2.1 Order — mandatory elements", "2.1");

    [Fact]
    public void AReportThatRanNoChecksDoesNotPass()
    {
        var report = NewBuilder().Build();

        report.Checks.Should().BeEmpty("the control is a report that examined nothing");
        report.OverallPass.Should().BeFalse(
            "All() is true on an empty sequence, so without a count floor a report that checked " +
            "nothing claims everything passed");
    }

    [Fact]
    public void TheRenderedReportOfNoChecksNeverPrintsAPassVerdict()
    {
        var markdown = NewBuilder().Build().ToMarkdown();

        markdown.Should().NotContain("**Result:** PASS",
            "this file is downloadable and forwardable as evidence — it may not state a verdict " +
            "over an examination that never ran");
        markdown.Should().Contain("- **Profile:**",
            "anti-vacuity floor: the report must really have rendered for the assertion above to mean anything");
    }

    /// <summary>
    /// FAIL would be as false as PASS here: nothing failed, because nothing ran. Same three-valued
    /// reasoning as the publish gate's <c>not_exercised</c> outcome.
    /// </summary>
    [Fact]
    public void AReportThatRanNoChecksSaysSoRatherThanReportingAFailure()
    {
        var markdown = NewBuilder().Build().ToMarkdown();

        markdown.Should().Contain("NOT CHECKED");
        markdown.Should().NotContain("**Result:** FAIL",
            "nothing failed — claiming a failure invents a defect just as a PASS invents a verdict");
    }

    /// <summary>
    /// The floor must not be implemented as "empty ⇒ false" bolted beside an unchanged predicate:
    /// a single failing Error check must still fail, and a populated all-passing report must still
    /// pass, or the floor would have been a way of breaking the verdict rather than flooring it.
    /// </summary>
    [Fact]
    public void TheFloorDoesNotDisturbReportsThatDidRunChecks()
    {
        var passing = NewBuilder()
            .Add("ubl.wellformed", true, "XML 1.0", "Document is well-formed XML.")
            .Build();
        passing.OverallPass.Should().BeTrue("a report with a passing Error-severity check still passes");

        var failing = NewBuilder()
            .Add("ubl.wellformed", false, "XML 1.0", "Document has no root element.")
            .Build();
        failing.OverallPass.Should().BeFalse("a failing Error-severity check still fails");

        var advisory = NewBuilder()
            .Add("ubl.advisory", false, "advisory", "Advisory only.", ConformanceSeverity.Warning)
            .Build();
        advisory.OverallPass.Should().BeTrue("a failing Warning is advisory and never fails the report");
    }
}
