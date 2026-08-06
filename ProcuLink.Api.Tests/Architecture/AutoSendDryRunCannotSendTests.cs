using System.Reflection;
using Microsoft.Extensions.Logging;
using ProcuLink.Api.Jobs;
using ProcuLink.Api.Services;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Infrastructure;
using Xunit;

namespace ProcuLink.Api.Tests.Architecture;

/// <summary>
/// WP-33 stage 1 — <b>the dry run cannot send.</b>
///
/// <para>The founder's ruling is that no purchase order reaches a real supplier without a human
/// click, and stage 1 exists to prove the auto-send decision is sound BEFORE that ruling is
/// relaxed. A dry run that could send is not a dry run, so "it only writes a row" cannot be left as
/// something a reader verifies by reading — it has to be a thing the build refuses to break.</para>
///
/// <para><b>The argument, in two halves.</b></para>
/// <list type="number">
///   <item><description><b>It cannot start a transform.</b> Proven from compiled IL by the repo's
///     existing <see cref="TransformTriggerDetector"/> — the same machinery
///     <c>AcceptanceGateSingleDoorTests</c> uses, whose own doc names "an auto-send after parse" as
///     precisely the future hole it was written to catch. Neither the evaluator nor the job that
///     calls it appears among the types that can start or run a transform.</description></item>
///   <item><description><b>It cannot reach anything else that sends.</b> The evaluator is
///     <c>sealed</c> and its collaborators are a closed, pinned set — a database context, the
///     acceptance gate, a config resolver, a logger. None of them can transform or dispatch, so
///     there is nothing for it to delegate a send to. Adding a dependency that could fails
///     <see cref="TheEvaluator_isHandedNothingThatCouldSend"/> in the same change that adds
///     it.</description></item>
/// </list>
///
/// <para><b>Why the transform matters as much as the dispatch.</b> Producing an artifact is not a
/// harmless side effect that stops short of sending. A completed transform commits
/// <c>ready_to_deliver</c> with an artifact and no delivery attempt against it — which is exactly
/// the signature <c>StrandedReadyOrderDetectionService</c> sweeps for, and it enqueues delivery
/// within its aged window. So an evaluator that "only" transformed, to obtain the bytes of the
/// document it would have sent, would have sent it: not through a delivery call of its own, but
/// through the recovery sweep, minutes later, with nobody having clicked anything.</para>
/// </summary>
public sealed class AutoSendDryRunCannotSendTests
{
    // ── Half one: it cannot start or run a transform ──────────────────────────

    [Fact]
    public void TheDryRunEvaluator_cannotStartOrRunATransform()
    {
        var starters = TransformTriggerDetector.TypesThatCanStartATransform();
        var runners  = TransformTriggerDetector.TypesThatRunATransform();

        // Anti-vacuity: a detector that found nothing would pass every assertion below while
        // proving nothing at all. These two are known-present, so a broken scan fails here first.
        Assert.Contains(typeof(TransformOrderJob), starters);
        Assert.Contains(typeof(TransformOrderJob), runners);

        Assert.DoesNotContain(typeof(AutoSendDryRunEvaluator), starters);
        Assert.DoesNotContain(typeof(AutoSendDryRunEvaluator), runners);
    }

    /// <summary>
    /// The job that HOSTS the evaluation must not become a transform trigger either. Stage 2 will
    /// change that deliberately; until then, a transform reachable from parse completion is an
    /// order moving unattended, which is the whole thing being staged.
    /// </summary>
    [Fact]
    public void ParseCompletion_cannotStartOrRunATransform()
    {
        Assert.DoesNotContain(typeof(ParseOrderJob), TransformTriggerDetector.TypesThatCanStartATransform());
        Assert.DoesNotContain(typeof(ParseOrderJob), TransformTriggerDetector.TypesThatRunATransform());
    }

    // ── Half two: it is handed nothing that could send ────────────────────────

    /// <summary>
    /// Every collaborator the evaluator is constructed with, named. The list is short on purpose:
    /// it is the reason a direct scan of this one class generalises to "it cannot send at all",
    /// without anyone having to write — and trust — a transitive IL walker.
    ///
    /// <para>Adding <c>IOrderService</c>, <c>IDeliveryService</c>, <c>IDeliveryDispatchEnqueuer</c>,
    /// <c>IBackgroundJobClient</c> or anything else with a send on it fails this test. If that is
    /// deliberate — i.e. this is stage 2 — the change belongs in a packet that says so, alongside
    /// the founder's authorisation, not slipped in under a dry run.</para>
    /// </summary>
    private static readonly HashSet<Type> PermittedDependencies =
    [
        typeof(ProcuLinkDbContext),
        typeof(IAcceptanceGate),
        typeof(IEffectiveConnectionConfigResolver),
        typeof(ILogger<AutoSendDryRunEvaluator>),
    ];

    [Fact]
    public void TheEvaluator_isHandedNothingThatCouldSend()
    {
        var ctor = Assert.Single(typeof(AutoSendDryRunEvaluator).GetConstructors());
        var actual = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        Assert.All(actual, t => Assert.True(
            PermittedDependencies.Contains(t),
            $"AutoSendDryRunEvaluator now depends on {t.FullName}. Stage 1 must not be able to send: "
          + "if this dependency can transform, dispatch, or enqueue a job, the dry run is no longer dry. "
          + "Add it to PermittedDependencies only after confirming it cannot."));

        // The class is sealed, so there is no subclass that could be handed something else.
        Assert.True(typeof(AutoSendDryRunEvaluator).IsSealed);
    }

    /// <summary>
    /// The send primitives, named so this file fails loudly if one is ever renamed rather than
    /// silently guarding nothing. None may appear among the evaluator's dependencies.
    /// </summary>
    [Fact]
    public void TheSendPrimitives_areNotAmongTheEvaluatorsDependencies()
    {
        Type[] sendPrimitives =
        [
            typeof(IOrderService),                  // .TransformAsync — the transform door
            typeof(IDeliveryService),               // .DispatchArtifactAsync — the delivery door
            typeof(IDeliveryDispatchEnqueuer),      // the seam that enqueues a first delivery
            typeof(Hangfire.IBackgroundJobClient),  // anything at all, scheduled
        ];

        foreach (var primitive in sendPrimitives)
            Assert.DoesNotContain(primitive, PermittedDependencies);
    }

    // ── The switch means one thing ────────────────────────────────────────────

    /// <summary>
    /// <c>AutoTransform</c> and <c>AutoDeliver</c> are separate columns and must stay separate.
    /// Folding the new decision onto the existing flag would promote every live
    /// <c>AutoDeliver = true</c> supplier — who consented to "one click instead of two" — to
    /// unattended sending, retroactively, on deploy.
    /// </summary>
    [Fact]
    public void AutoTransform_isItsOwnSwitch()
    {
        var config = typeof(ProcuLink.Core.Entities.SupplierDeliveryConfig);

        var autoTransform = config.GetProperty("AutoTransform");
        var autoDeliver   = config.GetProperty("AutoDeliver");

        Assert.NotNull(autoTransform);
        Assert.NotNull(autoDeliver);
        Assert.NotSame(autoTransform, autoDeliver);
        Assert.Equal(typeof(bool), autoTransform!.PropertyType);
    }

    // ── Both hosts run the decision ───────────────────────────────────────────

    /// <summary>
    /// <c>ParseOrderJob</c> runs in the Worker, so an evaluator registered only in the API would be
    /// a feature that never executes. Mirrors <c>AcceptanceGateHostRegistrationTests</c>, which
    /// exists because the acceptance gate was once exactly that.
    /// </summary>
    [Theory]
    [InlineData("ProcuLink.Api/Program.cs")]
    [InlineData("ProcuLink.Worker/Program.cs")]
    public void BothHosts_registerTheEvaluator(string relativePath)
    {
        var root = OrphanDetector.FindRepoRoot();
        var text = File.ReadAllText(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));

        Assert.Contains("AddScoped<IAutoSendDryRunEvaluator, AutoSendDryRunEvaluator>()", text, StringComparison.Ordinal);
    }
}
