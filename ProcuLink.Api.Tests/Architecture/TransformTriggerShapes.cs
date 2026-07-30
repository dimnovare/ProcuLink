using Hangfire;
using Hangfire.Common;
using ProcuLink.Api.Jobs;
using ProcuLink.Core.Services;

namespace ProcuLink.Api.Tests.Architecture;

/// <summary>
/// Eight ways someone could plausibly add a second transform trigger, WRITTEN AND COMPILED rather
/// than described. <see cref="AcceptanceGateSingleDoorTests.TheTripwire_seesEveryPlausibleTriggerShape"/>
/// runs the detector over this assembly and asserts it sees all eight.
///
/// <para>They are real code on purpose. A list of source-text snippets measures a regex against
/// strings a human chose, which is how the previous guard came to be confident about shapes it
/// could not actually see; these go through the same compiler and produce the same IL as the
/// production code they imitate, so "the detector sees it" means what it says.</para>
///
/// <para>Each shape differs from <c>TransformOrderJob</c>'s real call site along ONE axis, so a miss
/// names the axis the detector is blind to instead of a strawman. Nothing here is ever executed —
/// the arguments would be null — and nothing references these types from production code; they
/// exist to be READ, by the detector.</para>
///
/// <para>Each shape is a TOP-LEVEL type, not a nested one. The detector attributes a reference to
/// the outermost declaring type, because lambdas, local functions and async bodies all live in
/// compiler-generated nested classes and reporting those would name a type nobody wrote. Nesting the
/// fixtures would collapse all eight onto this registry class and the test would report every shape
/// as missed while the detector was working perfectly — which is exactly what happened first.</para>
/// </summary>
internal static class TransformTriggerShapes
{
    /// <summary>The eight shapes, each paired with the type that carries it.</summary>
    public static IReadOnlyList<(string Description, Type Type)> All => new (string, Type)[]
    {
        ("the shape TransformOrderJob uses today",     typeof(ShapeTodaysCallSite)),
        ("a differently-named service field",          typeof(ShapeDifferentlyNamedField)),
        ("a differently-named cancellation token",     typeof(ShapeDifferentlyNamedToken)),
        ("an assignment with short locals",            typeof(ShapeAssignmentWithShortLocals)),
        ("a delayed enqueue",                          typeof(ShapeDelayedEnqueue)),
        ("a recurring sweep",                          typeof(ShapeRecurringSweep)),
        ("a job built from an expression",             typeof(ShapeJobFromExpression)),
        ("a direct generic enqueue",                   typeof(ShapeDirectGenericEnqueue)),
    };
}

// ── Running a transform: IOrderService.TransformAsync ─────────────────────────

internal static class ShapeTodaysCallSite
{
    public static async Task Run(IOrderService _orderService, Guid organisationId, Guid orderId, OutputFormat outputFormat, CancellationToken ct)
    {
        var result = await _orderService.TransformAsync(organisationId, orderId, outputFormat, ct);
        GC.KeepAlive(result);
    }
}

internal static class ShapeDifferentlyNamedField
{
    public static async Task Run(IOrderService _orders, Guid organisationId, Guid orderId, OutputFormat outputFormat, CancellationToken ct)
    {
        var result = await _orders.TransformAsync(organisationId, orderId, outputFormat, ct);
        GC.KeepAlive(result);
    }
}

internal static class ShapeDifferentlyNamedToken
{
    public static async Task Run(IOrderService _orderService, Guid organisationId, Guid orderId, OutputFormat outputFormat, CancellationToken cancellationToken)
    {
        var result = await _orderService.TransformAsync(organisationId, orderId, outputFormat, cancellationToken);
        GC.KeepAlive(result);
    }
}

internal static class ShapeAssignmentWithShortLocals
{
    public static async Task Run(IOrderService svc, Guid orgId, Guid orderId, OutputFormat format, CancellationToken ct)
    {
        var r = await svc.TransformAsync(orgId, orderId, format, ct);
        GC.KeepAlive(r);
    }
}

// ── Starting a transform: scheduling TransformOrderJob ────────────────────────

internal static class ShapeDelayedEnqueue
{
    public static void Run(IBackgroundJobClient jobs, Guid orderId, Guid orgId, string format) =>
        jobs.Schedule<TransformOrderJob>(
            j => j.ExecuteAsync(orderId, orgId, format, CancellationToken.None),
            TimeSpan.FromMinutes(5));
}

internal static class ShapeRecurringSweep
{
    public static void Run(Guid orderId, Guid orgId, string format) =>
        RecurringJob.AddOrUpdate<TransformOrderJob>(
            "nightly-resend",
            j => j.ExecuteAsync(orderId, orgId, format, CancellationToken.None),
            Cron.Daily());
}

internal static class ShapeJobFromExpression
{
    public static Job Run(Guid orderId, Guid orgId, string format) =>
        Job.FromExpression<TransformOrderJob>(
            j => j.ExecuteAsync(orderId, orgId, format, CancellationToken.None));
}

internal static class ShapeDirectGenericEnqueue
{
    public static void Run(IBackgroundJobClient jobs, Guid orderId, Guid orgId, string format) =>
        jobs.Enqueue<TransformOrderJob>(
            j => j.ExecuteAsync(orderId, orgId, format, CancellationToken.None));
}
