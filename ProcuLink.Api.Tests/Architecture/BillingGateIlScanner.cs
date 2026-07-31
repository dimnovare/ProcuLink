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
/// updates one of two spellings. <c>AcceptanceGateSingleDoorTests</c> made the same call for the
/// same reason.</para>
///
/// <para><b>Async is the wrinkle that matters.</b> Every gate site in this codebase is an
/// <c>async</c> method, whose visible body is a compiler-generated stub that hands off to a state
/// machine — the <c>HasFeatureAsync</c> call lives in <c>MoveNext</c>, not in the method you named.
/// A scanner that missed that would report "no gate" for every single site and be worse than
/// useless, so <see cref="BodyOf"/> follows <see cref="AsyncStateMachineAttribute"/> before reading
/// any IL.</para>
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

    /// <summary>opcode byte(s) → <see cref="OpCode"/>, built once from the BCL's own table.</summary>
    private static readonly Dictionary<short, OpCode> OpCodeByValue = BuildOpCodeTable();

    private static Dictionary<short, OpCode> BuildOpCodeTable()
    {
        var map = new Dictionary<short, OpCode>();
        foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is OpCode op) map[op.Value] = op;
        }
        return map;
    }

    /// <summary>
    /// Every <see cref="BillingFeature"/> that <paramref name="root"/> consults through
    /// <paramref name="primitive"/>, following calls into our own assemblies up to
    /// <paramref name="maxDepth"/> levels.
    ///
    /// <para>Depth is needed because the delivery gates do not call the primitive themselves:
    /// <c>SuppliersController.UpsertDeliveryConfig</c> → <c>DeliveryCapabilityGate.FirstUnmetAsync</c>
    /// → <c>HasFeatureAsync</c>. A depth-1 scanner would call that site unenforced and demand a
    /// "fix" that duplicates the gate — the opposite of what WP-11 did.</para>
    /// </summary>
    public static IReadOnlySet<BillingFeature> FeaturesGatedBy(
        MethodBase root, GatePrimitive primitive, int maxDepth = 4)
    {
        var target = primitive == GatePrimitive.ServerRefusal ? HasFeatureAsync : PlanHasFeature;
        var atCallSite = new HashSet<BillingFeature>();
        var anywhereOnThePath = new HashSet<BillingFeature>();
        var reachesPrimitive = false;

        var seen = new HashSet<MethodBase>(MethodComparer.Instance);
        var queue = new Queue<(MethodBase Method, int Depth)>();
        queue.Enqueue((root, 0));

        while (queue.Count > 0)
        {
            var (method, depth) = queue.Dequeue();
            if (!seen.Add(method)) continue;

            foreach (var f in ConstantFeaturesIn(method)) anywhereOnThePath.Add(f);

            foreach (var (callee, feature) in CallsIn(method))
            {
                if (SameMethod(callee, target))
                {
                    reachesPrimitive = true;
                    if (feature is { } f) atCallSite.Add(f);
                    continue;
                }

                if (depth < maxDepth && IsOurs(callee)) queue.Enqueue((callee, depth + 1));
            }
        }

        if (!reachesPrimitive) return new HashSet<BillingFeature>();

        // A gate that names its feature right at the call site (SettingsController.UpdateSftp) is
        // read exactly. The DELIVERY gate does not have that shape and must not be forced into it:
        // DeliveryCapabilityGate.RequiredFeatures builds a list of (feature, capability) pairs and
        // FirstUnmetAsync loops it, so at the HasFeatureAsync call the feature is a field load, not
        // a constant — deliberately, because the channel and the output format are sold separately
        // and a single-winner gate was one of the bypasses WP-11 closed. Demanding a constant there
        // would push the codebase back toward a gate per feature.
        //
        // So: once the path is PROVEN to reach the primitive, every BillingFeature constant on that
        // path counts. The proof of enforcement stays exact (reachesPrimitive is not a heuristic);
        // only the attribution of which features is widened, and only for paths that do gate.
        return atCallSite.Count > 0 ? atCallSite : anywhereOnThePath;
    }

    /// <summary>
    /// Every <see cref="BillingFeature"/> this method's IL materialises as a constant AND hands to
    /// something typed to receive one.
    ///
    /// <para>The "and hands to" half is not optional. <see cref="BillingFeature"/> has ten members,
    /// so ordinals 0-8 are exactly the values <c>ldc.i4.0</c>-<c>ldc.i4.8</c> push — the most common
    /// instructions in any method. Reading bare constants would attribute a loop counter or a
    /// boolean to <c>BulkMapping</c> and report every method in the codebase as a billing gate.
    /// Requiring the constant to land on a <see cref="BillingFeature"/>-typed parameter (of
    /// <c>HasFeatureAsync</c>, of <c>PlanHasFeature</c>, or of the
    /// <c>ValueTuple&lt;BillingFeature, string&gt;</c> that <c>RequiredFeatures</c> builds) removes
    /// that entire class of false positive.</para>
    /// </summary>
    private static IEnumerable<BillingFeature> ConstantFeaturesIn(MethodBase method)
    {
        foreach (var (callee, feature) in CallsIn(method))
        {
            if (feature is not { } f) continue;
            if (TakesAFeature(callee)) yield return f;
        }
    }

    private static bool TakesAFeature(MethodBase callee)
    {
        try
        {
            return callee.GetParameters().Any(p => p.ParameterType == typeof(BillingFeature));
        }
        catch
        {
            return false;
        }
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
        foreach (var type in SafeTypes(assembly))
        foreach (var method in DeclaredMethods(type))
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
        var located = BodyOf(method);
        if (located is null) yield break;
        var (methodBody, owner) = located.Value;

        byte[] il;
        try { il = methodBody.GetILAsByteArray() ?? []; }
        catch { yield break; }

        var module = owner.Module;
        int? lastConstant = null;
        var pos = 0;

        while (pos < il.Length)
        {
            short code = il[pos];
            pos++;
            if (code == 0xFE && pos < il.Length)
            {
                code = (short)(0xFE00 | il[pos]);
                pos++;
            }

            if (!OpCodeByValue.TryGetValue(code, out var op)) yield break; // unknown encoding: stop, never guess

            var operandStart = pos;
            pos += OperandSize(op, il, operandStart);
            if (pos > il.Length) yield break;

            // Track the freshest int constant so a call can name the feature it was handed.
            var constant = ConstantOf(op, il, operandStart);
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

    /// <summary>
    /// The body to actually read for <paramref name="method"/>. For an <c>async</c> method that is
    /// the generated state machine's <c>MoveNext</c>, not the stub the compiler leaves behind under
    /// the original name.
    /// </summary>
    private static (MethodBody Body, MethodBase Owner)? BodyOf(MethodBase method)
    {
        var stateMachine = method.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType
                        ?? method.GetCustomAttribute<IteratorStateMachineAttribute>()?.StateMachineType;

        if (stateMachine is not null)
        {
            var moveNext = stateMachine.GetMethod(
                "MoveNext", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (moveNext?.GetMethodBody() is { } smBody) return (smBody, moveNext);
        }

        try
        {
            return method.GetMethodBody() is { } body ? (body, method) : null;
        }
        catch
        {
            return null;
        }
    }

    private static int OperandSize(OpCode op, byte[] il, int operandStart) => op.OperandType switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineBrTarget or OperandType.InlineField or OperandType.InlineI
            or OperandType.InlineMethod or OperandType.InlineSig or OperandType.InlineString
            or OperandType.InlineTok or OperandType.InlineType or OperandType.ShortInlineR => 4,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, operandStart),
        _ => 0,
    };

    /// <summary>The int this instruction pushes, when it pushes one.</summary>
    private static int? ConstantOf(OpCode op, byte[] il, int operandStart)
    {
        if (op == OpCodes.Ldc_I4_M1) return -1;
        if (op == OpCodes.Ldc_I4_0) return 0;
        if (op == OpCodes.Ldc_I4_1) return 1;
        if (op == OpCodes.Ldc_I4_2) return 2;
        if (op == OpCodes.Ldc_I4_3) return 3;
        if (op == OpCodes.Ldc_I4_4) return 4;
        if (op == OpCodes.Ldc_I4_5) return 5;
        if (op == OpCodes.Ldc_I4_6) return 6;
        if (op == OpCodes.Ldc_I4_7) return 7;
        if (op == OpCodes.Ldc_I4_8) return 8;
        if (op == OpCodes.Ldc_I4_S) return (sbyte)il[operandStart];
        if (op == OpCodes.Ldc_I4) return BitConverter.ToInt32(il, operandStart);
        return null;
    }

    // ── Reflection plumbing ───────────────────────────────────────────────────

    private static bool IsOurs(MethodBase method)
    {
        var name = method.DeclaringType?.Assembly.GetName().Name;
        return name is not null && OwnAssemblies.Contains(name);
    }

    private static bool SameMethod(MethodBase a, MethodBase b) =>
        a.Name == b.Name && a.DeclaringType == b.DeclaringType;

    private static IEnumerable<Type> SafeTypes(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t is not null)!; }
    }

    private static IEnumerable<MethodBase> DeclaredMethods(Type type)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
                                 | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        MethodBase[] methods;
        try { methods = type.GetMethods(flags).Cast<MethodBase>().Concat(type.GetConstructors(flags)).ToArray(); }
        catch { return []; }
        return methods;
    }

    /// <summary>
    /// Reports a compiler-generated state-machine <c>MoveNext</c> under the async method a human
    /// wrote, so the reverse index names <c>SettingsController.UpdateSftp</c> rather than
    /// <c>&lt;UpdateSftp&gt;d__12.MoveNext</c>.
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
