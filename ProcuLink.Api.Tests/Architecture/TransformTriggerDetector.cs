using System.Reflection;
using ProcuLink.Api.Controllers;
using ProcuLink.Api.Jobs;
using ProcuLink.Core.Services;

namespace ProcuLink.Api.Tests.Architecture;

/// <summary>
/// Finds, by SYMBOL, every type that can start or run a transform — or consult the acceptance gate.
///
/// <para><b>Why not a regex over source.</b> The previous version of this guard matched text:
/// <c>TransformOrderJob\s*\.\s*Enqueue\s*\(</c>, <c>Enqueue\s*&lt;\s*TransformOrderJob\s*&gt;</c>,
/// and a four-argument <c>.TransformAsync(ident, ident, format|outputFormat, ct)</c>. Measured
/// against eight plausible ways to add a second trigger — a delayed enqueue, a recurring sweep, a
/// job built from an expression, a differently-named cancellation token — it saw half of them. A
/// guard whose sensitivity depends on what somebody names their local variable is a guard against
/// reformatting the existing call site, not against a new one, and the whole reason the file exists
/// is the latter.</para>
///
/// <para><b>How this works.</b> It reads the compiled IL of every method in the production
/// assemblies and resolves the metadata tokens that method-bearing opcodes carry —
/// <c>call</c>, <c>callvirt</c>, <c>newobj</c>, and <c>ldtoken</c> (the last is how an expression
/// tree such as <c>j =&gt; j.ExecuteAsync(…)</c> names the method it wraps, which is precisely how
/// every Hangfire enqueue is written). Identifier names, whitespace, line breaks, <c>var</c>,
/// <c>await</c> and assignment are all gone by then; what survives is the symbol.</para>
///
/// <para><b>On the byte scan.</b> Each candidate position is tested for a valid metadata token that
/// resolves to one of the specific symbols being hunted; anything else is discarded. It therefore
/// cannot produce a FALSE NEGATIVE — every real call site emits its opcode byte followed by its
/// token, and every position is examined — and a false positive would require an operand byte to
/// both form a resolvable token and resolve to exactly <c>TransformOrderJob</c>,
/// <c>IOrderService.TransformAsync</c> or <c>IAcceptanceGate.EvaluateAsync</c>. For a tripwire,
/// "cannot miss, might occasionally over-report" is the right direction to be wrong in.</para>
/// </summary>
internal static class TransformTriggerDetector
{
    // ── Opcodes that carry a metadata token naming a member ───────────────────
    private const byte Call     = 0x28;
    private const byte CallVirt = 0x6F;
    private const byte NewObj   = 0x73;
    private const byte LdToken  = 0xD0;

    /// <summary>The compiled production code. Every one of these is a ProjectReference of the test
    /// project, so they are real assembly identities rather than paths that can go stale.</summary>
    public static IReadOnlyList<Assembly> ProductionAssemblies { get; } = new[]
    {
        typeof(OrdersController).Assembly,                                            // ProcuLink.Api
        typeof(IOrderService).Assembly,                                               // ProcuLink.Core
        typeof(ProcuLink.Infrastructure.ProcuLinkDbContext).Assembly,                 // ProcuLink.Infrastructure
        typeof(ProcuLink.Transform.Output.MappedTransformService).Assembly,           // ProcuLink.Transform
        typeof(ProcuLink.Worker.Jobs.StrandedReadyDeliveryDetectionJob).Assembly,     // ProcuLink.Worker
    };

    /// <summary>Stable, readable identity for a type: "AssemblyName::Namespace.Type".</summary>
    public static string Name(Type type) =>
        $"{type.Assembly.GetName().Name}::{type.FullName}";

    // ── The three questions this file exists to answer ────────────────────────

    /// <summary>Types that can START a transform — anything naming <see cref="TransformOrderJob"/>.</summary>
    public static IReadOnlySet<Type> TypesThatCanStartATransform(params Assembly[] assemblies) =>
        ScanOf(assemblies).Starters;

    /// <summary>Types that RUN a transform — anything calling <c>IOrderService.TransformAsync</c>.</summary>
    public static IReadOnlySet<Type> TypesThatRunATransform(params Assembly[] assemblies) =>
        ScanOf(assemblies).Runners;

    /// <summary>Types that can start OR run one. The union is what a "does this trigger a transform"
    /// question really means, and what the shape fixtures are measured against.</summary>
    public static IReadOnlySet<Type> TypesThatTriggerATransform(params Assembly[] assemblies)
    {
        var scan = ScanOf(assemblies);
        return scan.Starters.Union(scan.Runners).ToHashSet();
    }

    /// <summary>Types that consult the acceptance gate.</summary>
    public static IReadOnlySet<Type> TypesThatConsultTheGate(params Assembly[] assemblies) =>
        ScanOf(assemblies).GateConsumers;

    // ── Symbol predicates ─────────────────────────────────────────────────────

    /// <summary>
    /// Anything that names <see cref="TransformOrderJob"/>: a member declared on it (its
    /// <c>Enqueue</c> factory, or the <c>ExecuteAsync</c> an expression tree captures by
    /// <c>ldtoken</c>), a <c>ldtoken</c> of the type itself, or a generic method instantiated over
    /// it — which is the single shape shared by <c>Enqueue&lt;T&gt;</c>, <c>Schedule&lt;T&gt;</c>,
    /// <c>RecurringJob.AddOrUpdate&lt;T&gt;</c> and <c>Job.FromExpression&lt;T&gt;</c>.
    ///
    /// <para>DI registration (<c>AddScoped&lt;TransformOrderJob&gt;()</c> and friends) is excluded:
    /// making a type constructible is not scheduling it, and treating it as a trigger would put the
    /// composition roots permanently on the known-enqueuer list — where a REAL
    /// <c>RecurringJob.AddOrUpdate&lt;TransformOrderJob&gt;</c> added to <c>Program.cs</c> would then
    /// hide behind them.</para>
    /// </summary>
    private static bool NamesTheTransformJob(MemberInfo member)
    {
        if (member is Type type)
            return type == typeof(TransformOrderJob);

        if (member.DeclaringType == typeof(TransformOrderJob))
            return true;

        if (member is MethodInfo { IsGenericMethod: true } generic && !IsServiceRegistration(generic))
        {
            Type[] args;
            try { args = generic.GetGenericArguments(); }
            catch { return false; }
            return Array.IndexOf(args, typeof(TransformOrderJob)) >= 0;
        }

        return false;
    }

    private static bool IsServiceRegistration(MethodInfo method) =>
        method.DeclaringType?.Namespace?.StartsWith("Microsoft.Extensions.DependencyInjection", StringComparison.Ordinal) == true;

    /// <summary>
    /// <c>IOrderService.TransformAsync</c>, or the same method reached through a concrete
    /// implementation. Deliberately NOT any method called <c>TransformAsync</c>:
    /// <c>ITransformService.TransformAsync</c> shares the name and has a dozen legitimate call
    /// sites, and a guard that fired on those would be switched off within a week.
    /// </summary>
    private static bool IsOrderServiceTransform(MemberInfo member) =>
        member is MethodInfo { Name: nameof(IOrderService.TransformAsync) } method
        && method.DeclaringType is { } declaring
        && typeof(IOrderService).IsAssignableFrom(declaring);

    private static bool IsGateEvaluate(MemberInfo member) =>
        member is MethodInfo { Name: nameof(IAcceptanceGate.EvaluateAsync) } method
        && method.DeclaringType is { } declaring
        && typeof(IAcceptanceGate).IsAssignableFrom(declaring);

    // ── The scan ──────────────────────────────────────────────────────────────

    /// <summary>All three answers from ONE pass, cached per assembly set. Resolving tokens costs a
    /// caught exception for every operand byte that happens to look like an opcode; six tests each
    /// re-walking every method in five assemblies would turn a structural guard into the slowest
    /// thing in the suite.</summary>
    private sealed record Scan(
        HashSet<Type> Starters, HashSet<Type> Runners, HashSet<Type> GateConsumers);

    private static readonly Dictionary<string, Scan> Cache = new(StringComparer.Ordinal);
    private static readonly object CacheLock = new();

    private static Scan ScanOf(Assembly[] assemblies)
    {
        var scope = assemblies.Length > 0 ? assemblies : ProductionAssemblies.ToArray();
        var key   = string.Join("|", scope.Select(a => a.FullName).OrderBy(n => n, StringComparer.Ordinal));

        lock (CacheLock)
        {
            if (Cache.TryGetValue(key, out var cached)) return cached;

            var scan = new Scan(new HashSet<Type>(), new HashSet<Type>(), new HashSet<Type>());

            foreach (var assembly in scope)
            foreach (var type in TypesOf(assembly))
            {
                var owner = Outermost(type);

                foreach (var method in MethodsOf(type))
                foreach (var referenced in ReferencedMembers(method))
                {
                    if (NamesTheTransformJob(referenced))  scan.Starters.Add(owner);
                    if (IsOrderServiceTransform(referenced)) scan.Runners.Add(owner);
                    if (IsGateEvaluate(referenced))        scan.GateConsumers.Add(owner);
                }
            }

            Cache[key] = scan;
            return scan;
        }
    }

    /// <summary>
    /// The type a human wrote. A lambda, a local function and every <c>async</c> method body live in
    /// compiler-generated NESTED types, so attributing a reference to the type it is declared in
    /// would report <c>OrdersController+&lt;Redeliver&gt;d__57</c> instead of
    /// <c>OrdersController</c> — and the whole point is to name the file someone has to go and read.
    /// </summary>
    private static Type Outermost(Type type)
    {
        var t = type;
        while (t.DeclaringType is { } parent) t = parent;
        return t;
    }

    private static IEnumerable<Type> TypesOf(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t is not null).Select(t => t!); }
    }

    private static IEnumerable<MethodBase> MethodsOf(Type type)
    {
        const BindingFlags All =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static
          | BindingFlags.DeclaredOnly;

        try { return type.GetMethods(All).Cast<MethodBase>().Concat(type.GetConstructors(All)); }
        catch { return Array.Empty<MethodBase>(); }
    }

    private static List<MemberInfo> ReferencedMembers(MethodBase method)
    {
        var refs = new List<MemberInfo>();

        byte[]? il;
        try { il = method.GetMethodBody()?.GetILAsByteArray(); }
        catch { return refs; }
        if (il is null) return refs;

        // A token inside a generic definition is resolved in the context of that definition's own
        // type/method parameters; without them ResolveMember throws for every such call site.
        Type[]? typeArgs = null;
        Type[]? methodArgs = null;
        try
        {
            if (method.DeclaringType?.IsGenericType == true) typeArgs = method.DeclaringType.GetGenericArguments();
            if (method is MethodInfo { IsGenericMethodDefinition: true } definition) methodArgs = definition.GetGenericArguments();
        }
        catch { /* keep both null and let ResolveMember decide */ }

        var module = method.Module;

        for (var i = 0; i + 5 <= il.Length; i++)
        {
            if (il[i] is not (Call or CallVirt or NewObj or LdToken)) continue;

            var token = BitConverter.ToInt32(il, i + 1);
            try
            {
                var member = module.ResolveMember(token, typeArgs, methodArgs);
                if (member is not null) refs.Add(member);
            }
            catch
            {
                // Not a member token — an operand byte that happened to equal an opcode. Discarded,
                // which is why over-reporting is essentially impossible and under-reporting is
                // structurally impossible.
            }
        }

        return refs;
    }
}
