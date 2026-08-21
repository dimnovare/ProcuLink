using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Infrastructure;
using Xunit;

namespace ProcuLink.TestSupport;

/// <summary>
/// The tripwire for the defect class "a new DbSet quietly breaks thirty-nine test contexts".
///
/// <para><b>Read <see cref="InheritedDbSetModelScanner"/> first</b> — it carries the full history
/// and the mechanism. In short: test contexts that override <c>OnModelCreating</c> without calling
/// <c>base</c> still inherit every <c>DbSet&lt;&gt;</c> property, EF still discovers an entity type
/// for each, and an entity whose key is not <c>Id</c> / <c>&lt;Type&gt;Id</c> then fails model
/// validation inside whatever unrelated test happens to touch that context first.</para>
///
/// <para><b>Why this file lives in <c>ProcuLink.TestSupport</c>.</b> Both
/// <c>ProcuLink.Api.Tests</c> and <c>ProcuLink.Infrastructure.Tests</c> link-compile
/// <c>..\ProcuLink.TestSupport\*.cs</c>, and the trap has bitten both — <c>AiUsageMonthly</c>
/// needed twenty-nine files and <c>WorkerHealthAlertCooldown</c> thirty-one, spread across the two
/// projects. A guard placed in only one of them would police half the population and report green
/// over the other half. Compiled into both, it runs twice, each time over
/// <c>Assembly.GetExecutingAssembly()</c>, so each project polices its own contexts.</para>
///
/// <para><b>Rot-resistance runs in both directions</b>, because a guard that can only fail one way
/// is the failure mode this repo keeps paying for. A context that stops handling a fragile entity
/// fails <see cref="EveryTestDbContext_HandlesEveryEntityTypeEfCannotKeyByConvention"/>. A guard
/// that has quietly stopped being able to see anything fails
/// <see cref="TheGuardHasAPopulationToPolice"/> or
/// <see cref="EveryTestDbContext_WasActuallyInspected"/>. And the guard's ability to detect the
/// defect at all is itself asserted, by a matched pair of controls that differ by exactly the
/// <c>Ignore&lt;T&gt;()</c> lines under test:
/// <see cref="TheGuardFailsWhenTheIgnoreLinesAreMissing"/> and
/// <see cref="TheGuardPassesWhenTheIgnoreLinesArePresent"/>. Deleting either control fails the
/// suite rather than silently disarming it.</para>
/// </summary>
public class InheritedDbSetModelCoverageTests
{
    private static Assembly ThisTestAssembly => typeof(InheritedDbSetModelCoverageTests).Assembly;

    private static InheritedDbSetModelScanner.ScanResult Scan =>
        InheritedDbSetModelScanner.Scan(ThisTestAssembly);

    /// <summary>
    /// The guard proper. Every <see cref="ProcuLinkDbContext"/> subclass in this assembly must end
    /// up with a model in which no entity type EF cannot key by convention is left keyless —
    /// whether it gets there by calling <c>base.OnModelCreating</c>, by mapping the entity itself,
    /// or by <c>modelBuilder.Ignore&lt;T&gt;()</c>.
    /// </summary>
    [Fact]
    public void EveryTestDbContext_HandlesEveryEntityTypeEfCannotKeyByConvention()
    {
        var scan = Scan;
        var offenders = scan.Contexts
            .Where(c => !c.IsControl && c.UnhandledFragileEntities.Count > 0)
            .ToList();

        // Asserted unconditionally, and on projected names rather than on the reports themselves:
        // an `if (offenders.Any()) Assert.Fail(…)` reads better but is the shape
        // NoVacuousTestPassTests refuses, because a body whose only assertion sits behind an `if`
        // reports Passed on the green path having verified nothing.
        offenders.Select(o => o.ContextType.FullName).Should().BeEmpty(
            InheritedDbSetModelScanner.DescribeOffenders(scan.FragileEntities, offenders, ThisTestAssembly));
    }

    /// <summary>
    /// A context the guard could not construct or whose model EF never customized is a hole in the
    /// coverage, not a pass. This reports it as a failure so the hole cannot open silently.
    /// </summary>
    [Fact]
    public void EveryTestDbContext_WasActuallyInspected()
    {
        var uninspected = Scan.Contexts
            .Where(c => !c.Inspected)
            .Select(c => $"{c.ContextType.FullName}: {c.Note}")
            .ToList();

        uninspected.Should().BeEmpty(
            "a ProcuLinkDbContext subclass this guard cannot build is a subclass it does not cover, "
            + "and an uncovered subclass is exactly where the next unconfigured DbSet will hide. "
            + "Give it a constructor whose first parameter is DbContextOptions<ProcuLinkDbContext>, "
            + "or teach InheritedDbSetModelScanner.Construct how to build it");
    }

    /// <summary>
    /// Anti-vacuity. Both halves of the guard have to be non-empty for it to mean anything: at
    /// least one entity type EF cannot key unaided, and a real population of contexts to check it
    /// against. Without this, deleting the DbSets or breaking type discovery would leave every
    /// other test here green over nothing.
    /// </summary>
    [Fact]
    public void TheGuardHasAPopulationToPolice()
    {
        var scan = Scan;

        scan.FragileEntities.Should().NotBeEmpty(
            "the guard's whole subject is entity types whose key EF cannot discover — if the real "
            + "ProcuLinkDbContext model has none, this guard is checking nothing and should be "
            + "deleted rather than left reporting green");

        scan.ConventionKeyedEntities.Should().NotBeEmpty(
            "the negative control ignores exactly these to reproduce the defect; an empty list "
            + "would make it ignore nothing and prove nothing");

        scan.Contexts.Count(c => !c.IsControl).Should().BeGreaterThan(9,
            "this assembly is known to hold well over a dozen ProcuLinkDbContext subclasses; a "
            + "handful means type discovery is broken, not that the population shrank");
    }

    /// <summary>
    /// The negative control: a context built exactly like the real ones — no
    /// <c>base.OnModelCreating</c> call, everything EF can key by convention ignored — except that
    /// it deliberately omits the <c>Ignore&lt;T&gt;()</c> lines for the entity types EF cannot key.
    /// The guard must report every one of them.
    ///
    /// <para>This is the assertion that keeps the guard honest. Removing an
    /// <c>Ignore&lt;WorkerHealthAlertCooldown&gt;()</c> line from a real test context was verified
    /// by hand to turn <see cref="EveryTestDbContext_HandlesEveryEntityTypeEfCannotKeyByConvention"/>
    /// red; this makes that verification permanent instead of a one-off someone did once.</para>
    /// </summary>
    [Fact]
    public void TheGuardFailsWhenTheIgnoreLinesAreMissing()
    {
        var scan = Scan;
        var control = scan.Contexts.Should().ContainSingle(c => c.IsNegativeControl).Subject;

        control.Inspected.Should().BeTrue(control.Note ?? "the negative control must be inspectable");
        control.UnhandledFragileEntities.Should().BeEquivalentTo(
            scan.FragileEntities.Select(f => f.ClrType),
            "the negative control ignores every convention-keyed entity and nothing else, so what "
            + "is left discovered-but-keyless must be precisely the fragile set — if it is not, the "
            + "guard is no longer detecting the defect it was written for");
    }

    /// <summary>
    /// The positive control: byte-for-byte the negative control plus the missing
    /// <c>Ignore&lt;T&gt;()</c> lines. It must come back clean, so a failure of
    /// <see cref="TheGuardFailsWhenTheIgnoreLinesAreMissing"/> can only mean the ignores matter —
    /// not that the guard reports everything.
    /// </summary>
    [Fact]
    public void TheGuardPassesWhenTheIgnoreLinesArePresent()
    {
        var control = Scan.Contexts.Should().ContainSingle(c => c.IsPositiveControl).Subject;

        control.Inspected.Should().BeTrue(control.Note ?? "the positive control must be inspectable");
        control.UnhandledFragileEntities.Should().BeEmpty(
            "this control differs from the negative one only by the Ignore<T>() lines, so anything "
            + "reported here means the guard fires regardless of the fix and is worthless");
    }

    /// <summary>
    /// Deliberately broken. Mirrors the real pattern — no <c>base.OnModelCreating</c>, everything
    /// ignored — but stops short of the entity types EF cannot key by convention. The ignore list
    /// is read from the live model rather than typed out, so this control cannot rot: an entity
    /// added tomorrow is classified tomorrow.
    /// </summary>
    [UnconfiguredEntitySetNegativeControl]
    private sealed class MissingIgnoreLinesNegativeControlDbContext : ProcuLinkDbContext
    {
        public MissingIgnoreLinesNegativeControlDbContext(DbContextOptions<ProcuLinkDbContext> options)
            : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // No base call — this is the shape every context in the population uses.
            IgnoreEverythingEfCanKeyByItself(modelBuilder);

            // …and here the Ignore<T>() lines for the entity types EF cannot key are DELIBERATELY
            // absent. That omission is the defect this whole file exists to catch.
        }
    }

    /// <summary>
    /// The same context, correctly written: it also ignores the entity types EF cannot key. The
    /// only difference from the negative control is the loop below.
    /// </summary>
    [UnconfiguredEntitySetPositiveControl]
    private sealed class CompleteIgnoreLinesPositiveControlDbContext : ProcuLinkDbContext
    {
        public CompleteIgnoreLinesPositiveControlDbContext(DbContextOptions<ProcuLinkDbContext> options)
            : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            IgnoreEverythingEfCanKeyByItself(modelBuilder);

            foreach (var clrType in InheritedDbSetModelScanner.FragileEntityClrTypes)
            {
                modelBuilder.Ignore(clrType);
            }
        }
    }

    /// <summary>
    /// Shared by both controls so they differ by exactly one thing. Ignoring <see cref="JsonDocument"/>
    /// mirrors the real <c>ProcuLinkDbContext.OnModelCreating</c>, which does the same to stop EF's
    /// convention scan treating it as an owned entity type.
    /// </summary>
    private static void IgnoreEverythingEfCanKeyByItself(ModelBuilder modelBuilder)
    {
        modelBuilder.Ignore(typeof(JsonDocument));
        foreach (var clrType in InheritedDbSetModelScanner.ConventionKeyedEntityClrTypes)
        {
            modelBuilder.Ignore(clrType);
        }
    }
}
