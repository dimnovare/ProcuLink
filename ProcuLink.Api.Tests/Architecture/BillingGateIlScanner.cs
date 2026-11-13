using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Services;

namespace ProcuLink.Api.Tests.Architecture;

/// <summary>
/// Answers, from compiled IL rather than from prose: <b>which <see cref="BillingFeature"/> values
/// does this method actually gate on?</b>
///
/// <para><b>Why this exists.</b> WP-11's own regression guard was a hand-maintained
/// <c>Dictionary&lt;BillingFeature, string&gt;</c> of free text, and the test that consumed it only
/// asserted <c>ContainKey</c>. Delete the <c>HasFeatureAsync</c> call out of
/// <c>AuditController.GetAuditLog</c> and every one of those tests stays green — the string still
/// reads "AuditController.GetAuditLog". That is the same shape as the defect WP-11 was written to
/// kill: a declaration nothing keeps true. A gate table verified by a string is not verified.</para>
///
/// <para><b>Why IL and not source text.</b> The repo has been bitten before by regex source
/// scanning (the orphan guard's comment stripper silently hid ~680 lines of <c>Program.cs</c>).
/// Metadata tokens cannot be fooled by a comment, a string literal, a <c>#if</c>, or a rename that
/// updates one of two spellings.</para>
///
/// <para><b>Async is the wrinkle that matters.</b> Every gate site in this codebase is an
/// <c>async</c> method, whose visible body is a compiler-generated stub that hands off to a state
/// machine — the <c>HasFeatureAsync</c> call lives in <c>MoveNext</c>, not in the method you named.
/// A scanner that missed that would report "no gate" for every single site and be worse than
/// useless, so <see cref="IlReader.BodyOf"/> follows <see cref="AsyncStateMachineAttribute"/>
/// before reading any IL.</para>
///
/// <para><b>The IL decoding itself lives in <see cref="IlReader"/>,</b> shared with the other IL
/// guards (<see cref="OrgAdminGateIlScanner"/>). What is specific to this scanner — and stays
/// here — is the <see cref="BillingFeature"/> constant tracking. Duplicating the decoder was the
/// alternative, and the log records where that leads: two copies drift and the one nobody fixed is
/// the one still shipping the bug.</para>
/// </summary>
internal static class BillingGateIlScanner
{
    /// <summary>
    /// The two sanctioned ways production code consults the paid ladder.
    ///
    /// <para><see cref="ServerRefusal"/> — <see cref="IBillingService.HasFeatureAsync"/>. The real
    /// thing: it resolves the org's plan AND collapses to false on a read-only account status, and
    /// its callers turn a false into a 403 or an early return.</para>
    ///
    /// <para><see cref="PresentationOnly"/> — <see cref="PlanConstants.PlanHasFeature"/>. A pure
    /// table lookup with no account-status short-circuit. It can drive an availability flag or an
    /// upsell; it is <b>not</b> a refusal, and treating it as one is how a capability comes to be
    /// "gated" only in the UI.</para>
    /// </summary>
    public enum GatePrimitive
    {
        ServerRefusal,
        PresentationOnly,
    }

    /// <summary>Assemblies whose methods this scanner is willing to step into.</summary>
    private static readonly string[] OwnAssemblies =
    [
        "ProcuLink.Api",
        "ProcuLink.Core",
        "ProcuLink.Infrastructure",
        "ProcuLink.Worker",
    ];

    private static readonly MethodInfo HasFeatureAsync =
        typeof(IBillingService).GetMethod(nameof(IBillingService.HasFeatureAsync))
        ?? throw new InvalidOperationException(
            "IBillingService.HasFeatureAsync not found — the gate primitive was renamed. Update this " +
            "scanner in the same change, or every gate silently reads as unenforced.");

    private static readonly MethodInfo PlanHasFeature =
        typeof(PlanConstants).GetMethod(nameof(PlanConstants.PlanHasFeature))
        ?? throw new InvalidOperationException("PlanConstants.PlanHasFeature not found.");

    /// <summary>
    /// Every <see cref="BillingFeature"/> that <paramref name="root"/> consults through
    /// <paramref name="primitive"/>, following calls into our own assemblies up to
    /// <paramref name="maxDepth"/> levels.
    ///
    /// <para>Depth lets a gate sit in a helper the named site calls, so a refactor that extracts the
    /// check into a helper that names the feature itself does not read as a deleted gate. A helper
    /// that takes <see cref="BillingFeature"/> as a parameter does read as one: it consults the
    /// ladder, but not about this feature. That is fail-closed, and intended.</para>
    ///
    /// <para>Only the feature handed to the primitive AT its call site counts. Until plan 13b
    /// (2026-11-13) any feature constant on a gating path counted too, a widening that existed for the
    /// delivery-config gate alone (it passed its features through a list, so the call site carried a
    /// field load, not a constant). That gate was deleted with the legacy order pipeline, and the
    /// widening went with it rather than stay behind as a looser guard than the code needs.</para>
    /// </summary>
    public static IReadOnlySet<BillingFeature> FeaturesGatedBy(
        MethodBase root, GatePrimitive primitive, int maxDepth = 4)
    {
        var target = primitive == GatePrimitive.ServerRefusal ? HasFeatureAsync : PlanHasFeature;
        var atCallSite = new HashSet<BillingFeature>();

        var seen = new HashSet<MethodBase>(MethodComparer.Instance);
        var queue = new Queue<(MethodBase Method, int Depth)>();
        queue.Enqueue((root, 0));

        while (queue.Count > 0)
        {
            var (method, depth) = queue.Dequeue();
            if (!seen.Add(method)) continue;

            foreach (var (callee, feature) in CallsIn(method))
            {
                if (SameMethod(callee, target))
                {
                    if (feature is { } f) atCallSite.Add(f);
                    continue;
                }

                if (depth < maxDepth && IsOurs(callee)) queue.Enqueue((callee, depth + 1));
            }
        }

        return atCallSite;
    }

    /// <summary>
    /// True when <paramref name="root"/> reaches <paramref name="primitive"/> at all — used to tell
    /// "gates on the wrong feature" apart from "does not gate", which are different failures and
    /// deserve different messages.
    /// </summary>
    public static bool ReachesGatePrimitive(MethodBase root, GatePrimitive primitive, int maxDepth = 4)
    {
        var target = primitive == GatePrimitive.ServerRefusal ? HasFeatureAsync : PlanHasFeature;
        var seen = new HashSet<MethodBase>(MethodComparer.Instance);
        var queue = new Queue<(MethodBase Method, int Depth)>();
        queue.Enqueue((root, 0));

        while (queue.Count > 0)
        {
            var (method, depth) = queue.Dequeue();
            if (!seen.Add(method)) continue;

            foreach (var (callee, _) in CallsIn(method))
            {
                if (SameMethod(callee, target)) return true;
                if (depth < maxDepth && IsOurs(callee)) queue.Enqueue((callee, depth + 1));
            }
        }

        return false;
    }

    /// <summary>
    /// Every method in the production assemblies that calls
    /// <see cref="IBillingService.HasFeatureAsync"/> directly, paired with the feature constant it
    /// passes (null when the argument is not a compile-time constant). This is the REVERSE index:
    /// it finds a gate that exists in code but is missing from the declared map, which is how an
    /// enforcement site gets deleted or moved without anyone noticing.
    /// </summary>
    public static IReadOnlyList<(MethodBase Site, BillingFeature? Feature)> DirectGateCallSites()
    {
        var results = new List<(MethodBase, BillingFeature?)>();

        foreach (var assembly in ProductionAssemblies())
        foreach (var type in IlReader.SafeTypes(assembly))
        foreach (var method in IlReader.DeclaredMethods(type))
        {
            foreach (var (callee, feature) in CallsIn(method))
            {
                if (SameMethod(callee, HasFeatureAsync)) results.Add((Unwrap(method, type), feature));
            }
        }

        return results;
    }

    public static IEnumerable<Assembly> ProductionAssemblies() =>
        OwnAssemblies.Select(Assembly.Load);

    // ── IL walking ────────────────────────────────────────────────────────────

    /// <summary>
    /// Call instructions in <paramref name="method"/>, each paired with the
    /// <see cref="BillingFeature"/> constant most recently pushed before it. The "most recently
    /// pushed" heuristic is sound for this codebase's shape because the only int-like constant on
    /// the stack at a <c>HasFeatureAsync</c> call is the feature itself — the org id is a
    /// <c>Guid</c> (loaded, not pushed as <c>ldc.i4</c>) and the cancellation token is an argument.
    /// A site that ever pushes another <c>ldc.i4</c> between the feature and the call would be
    /// mis-read, so the reverse index reports a null feature rather than guessing.
    /// </summary>
    private static IEnumerable<(MethodBase Callee, BillingFeature? Feature)> CallsIn(MethodBase method)
    {
        var located = IlReader.BodyOf(method);
        if (located is null) yield break;
        var (methodBody, owner) = located.Value;

        var il = IlReader.IlOf(methodBody);
        if (il.Length == 0) yield break;

        var module = owner.Module;
        int? lastConstant = null;

        foreach (var instruction in IlReader.Decode(il))
        {
            var (op, operandStart) = (instruction.Op, instruction.OperandStart);

            // Track the freshest int constant so a call can name the feature it was handed.
            var constant = IlReader.Int32Constant(op, il, operandStart);
            if (constant is { } c) { lastConstant = c; continue; }

            if (op.OperandType != OperandType.InlineMethod) continue;

            MethodBase? callee = null;
            try
            {
                var token = BitConverter.ToInt32(il, operandStart);
                callee = module.ResolveMethod(
                    token,
                    owner.DeclaringType?.GetGenericArguments(),
                    owner is MethodInfo mi ? mi.GetGenericArguments() : null);
            }
            catch
            {
                // Generic context we cannot reconstruct. Skipping is safe: this scanner is only
                // ever used to PROVE a gate exists, so an unresolvable edge can only make a test
                // stricter (report "not gated"), never wave a missing gate through.
            }

            if (callee is null) continue;

            BillingFeature? feature = null;
            if (lastConstant is { } value && Enum.IsDefined(typeof(BillingFeature), value))
                feature = (BillingFeature)value;

            yield return (callee, feature);
            lastConstant = null;
        }
    }

    // ── Reflection plumbing ───────────────────────────────────────────────────

    private static bool IsOurs(MethodBase method)
    {
        var name = method.DeclaringType?.Assembly.GetName().Name;
        return name is not null && OwnAssemblies.Contains(name);
    }

    private static bool SameMethod(MethodBase a, MethodBase b) =>
        a.Name == b.Name && a.DeclaringType == b.DeclaringType;

    /// <summary>
    /// Reports a compiler-generated state-machine <c>MoveNext</c> under the async method a human
    /// wrote, so the reverse index names <c>AuditController.GetAuditLog</c> rather than
    /// <c>&lt;GetAuditLog&gt;d__3.MoveNext</c>.
    /// </summary>
    private static MethodBase Unwrap(MethodBase method, Type declaringType)
    {
        if (method.Name != "MoveNext" || declaringType.DeclaringType is null) return method;

        var outer = declaringType.DeclaringType;
        var generatedName = declaringType.Name;
        var open = generatedName.IndexOf('<');
        var close = generatedName.IndexOf('>');
        if (open < 0 || close <= open) return method;

        var originalName = generatedName.Substring(open + 1, close - open - 1);
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
                                 | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var match = outer.GetMethods(flags).FirstOrDefault(m => m.Name == originalName);
        return match ?? method;
    }

    private sealed class MethodComparer : IEqualityComparer<MethodBase>
    {
        public static readonly MethodComparer Instance = new();

        public bool Equals(MethodBase? x, MethodBase? y) =>
            x is not null && y is not null
            && x.Name == y.Name
            && x.DeclaringType == y.DeclaringType
            && x.GetParameters().Length == y.GetParameters().Length;

        public int GetHashCode(MethodBase obj) =>
            HashCode.Combine(obj.Name, obj.DeclaringType, obj.GetParameters().Length);
    }
}
