using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;

namespace ProcuLink.Api.Tests.Architecture;

/// <summary>
/// Answers, from compiled IL rather than from prose: <b>which methods pick one
/// <see cref="OutboundArtifact"/> out of many by recency, and does each of them consult
/// <see cref="OutboundArtifactSelection"/> while doing it?</b>
///
/// <para><b>The defect this exists to make un-writable.</b> Before WP-35 every artifact an order
/// held was a deliverable one, so seven separate paths each answered "which artifact does this
/// order send?" with the same three lines:</para>
/// <code>
/// var artifact = order.OutboundArtifacts
///     .OrderByDescending(a =&gt; a.CreatedAt)
///     .FirstOrDefault();
/// </code>
/// <para>Re-processing broke the assumption underneath all seven: it appends an artifact the
/// operator asked to SEE, and that artifact is by construction the newest. Two of the seven were
/// found only by adversarial review after the first implementation pass was believed complete —
/// which is the whole point. The failure is silent, and it ends either with a supplier receiving a
/// document no human approved, or with a preview's SHA-256 published as the record of what was
/// sent.</para>
///
/// <para><b>Why the rule is "who selects", not "who dispatches".</b> The obvious guard — flag a
/// method that touches artifacts and then reaches a delivery dispatcher — was measured against the
/// real seven and provably misses two of them. <c>OrderTransformService</c>'s already-transformed
/// branch dispatches nothing: it returns an artifact id to <c>TransformOrderJob</c>, and the JOB
/// decides whether to enqueue a delivery, so forward reachability from the selecting method finds
/// no dispatcher. <c>PassportService</c> dispatches nothing at all — it publishes the hash of "what
/// we sent". A dispatch-reachability guard would have waved both through, so this scanner polices
/// the <b>selection</b> instead: picking an artifact by recency is the operation that has to be
/// right, wherever the answer subsequently travels.</para>
///
/// <para><b>Why IL and not a regex over source.</b> A regex cannot tell code from a comment. The
/// progress log records a source-text guard that re-anchored onto an explanatory comment and went
/// silently blind, and a mounting assertion that matched inside <c>{/* */}</c> while the real
/// element was gone — four gates green with the defect fully restored. Metadata tokens cannot be
/// fooled by a comment, a string literal, a <c>#if</c>, or a rename that updates one spelling of
/// two. <see cref="BillingGateIlScanner"/> made the same call for the same reason, and
/// <c>ACommentedOutRoutingCallIsNotEvidenceOfRouting</c> in the guard's tests pins that this one
/// really has that property rather than merely claiming it.</para>
///
/// <para><b>Known blind spot, stated rather than papered over.</b> The scanner recognises the LINQ
/// idiom — every one of the seven sites used it — and would not recognise a hand-rolled
/// <c>foreach</c> that tracked a maximum <c>CreatedAt</c> itself. That shape has never appeared in
/// this codebase and would be conspicuous in review; the guard is a floor under the idiom that
/// actually gets written, not a proof that no ordering can ever be expressed another way.</para>
/// </summary>
internal static class OutboundArtifactSelectionIlScanner
{
    /// <summary>What the method was seen doing to a set of artifacts.</summary>
    public enum SelectionShape
    {
        /// <summary>Ordered them by a key — the <c>OrderByDescending(a =&gt; a.CreatedAt)</c> half of the idiom.</summary>
        RecencyOrdering,

        /// <summary>Reduced them to one extreme value — <c>Max</c> / <c>MaxBy</c> / <c>Min</c> / <c>MinBy</c>.</summary>
        AggregatePick,
    }

    /// <summary>
    /// One user-written method that performs a recency selection over <see cref="OutboundArtifact"/>,
    /// with everything the guard needs to explain itself when it fails.
    /// </summary>
    public sealed record SelectionSite(
        string Key,
        string Display,
        IReadOnlySet<SelectionShape> Shapes,
        IReadOnlyList<string> Operators,
        IReadOnlyList<string> RoutingEvidence)
    {
        /// <summary>True when this method demonstrably consulted <see cref="OutboundArtifactSelection"/>.</summary>
        public bool IsRouted => RoutingEvidence.Count > 0;
    }

    /// <summary>
    /// The type that owns the answer. Resolved by reflection so a rename fails here, loudly and
    /// once, instead of quietly turning every site in the codebase into "not routed" (or, worse,
    /// turning the guard vacuous).
    /// </summary>
    public static readonly Type SelectionType = typeof(OutboundArtifactSelection);

    /// <summary>
    /// The marker constant, read from the production type rather than typed here. It matters that
    /// this is not a literal in the test: renaming the storage segment must move the guard with it.
    /// </summary>
    public static readonly string ReprocessKeyMarker =
        (string?)SelectionType.GetField(nameof(OutboundArtifactSelection.ReprocessKeyMarker),
                                        BindingFlags.Public | BindingFlags.Static)
            ?.GetRawConstantValue()
        ?? throw new InvalidOperationException(
            "OutboundArtifactSelection.ReprocessKeyMarker not found as a public static constant — the " +
            "selection rule was renamed or restructured. Update this scanner in the same change, or " +
            "every artifact selection in the codebase silently reads as routed-by-nothing.");

    /// <summary>
    /// The members that answer "is this artifact deliverable?" — and ONLY those.
    ///
    /// <para><c>BuildReprocessKey</c> is deliberately absent even though it lives on the same type.
    /// It is the WRITER of a re-processed artifact, not a reader of the rule: a method could mint a
    /// preview and then still pick its order's newest artifact raw, and counting that call as
    /// evidence would clear it. <c>ReplayService.ReprocessAsync</c> — the one production method that
    /// calls it — is routed here on its own separate <c>Deliverable()</c> call, so nothing is lost
    /// by the exclusion and a real class of false clearance is closed.</para>
    ///
    /// <para>Names, not <see cref="MethodInfo"/> handles, because a lifted lambda inside the rule
    /// (<c>&lt;NewestDeliverable&gt;b__5_0</c>) has to count as the member it was written inside.
    /// Every name is checked to exist below, so a rename fails loudly rather than silently emptying
    /// the set.</para>
    /// </summary>
    public static readonly IReadOnlySet<string> RoutingMembers = BuildRoutingMembers();

    private static IReadOnlySet<string> BuildRoutingMembers()
    {
        string[] names =
        [
            nameof(OutboundArtifactSelection.NewestDeliverable),
            nameof(OutboundArtifactSelection.Deliverable),
            nameof(OutboundArtifactSelection.IsReprocessed),
            nameof(OutboundArtifactSelection.IsReprocessedKey),
            nameof(OutboundArtifactSelection.ReprocessKeyMarker),
            nameof(OutboundArtifactSelection.ReprocessKeySegment),
        ];

        const BindingFlags flags = BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var missing = names
            .Where(n => SelectionType.GetMethod(n, flags) is null && SelectionType.GetField(n, flags) is null)
            .ToList();

        if (missing.Count > 0)
            throw new InvalidOperationException(
                $"OutboundArtifactSelection no longer declares: {string.Join(", ", missing)}. The " +
                "selection rule was renamed or restructured — update this scanner in the same change, " +
                "or every routed site in the codebase silently reads as unrouted (and, once someone " +
                "'fixes' the guard by emptying it, as clean).");

        return new HashSet<string>(names, StringComparer.Ordinal);
    }

    /// <summary>
    /// LINQ operators that pick a winner out of a set. Matched by NAME and by the presence of
    /// <see cref="OutboundArtifact"/> among the call's generic arguments — deliberately NOT
    /// restricted to <c>System.Linq.Enumerable</c> / <c>Queryable</c> / EF's extensions, so a new
    /// LINQ-shaped host (a helper of our own, a third-party operator set) is covered the day it
    /// arrives rather than the day someone remembers to add it here.
    /// </summary>
    private static readonly HashSet<string> OrderingOperators =
        ["OrderBy", "OrderByDescending", "ThenBy", "ThenByDescending"];

    private static readonly HashSet<string> AggregateOperators =
        ["Max", "MaxBy", "Min", "MinBy", "MaxAsync", "MinAsync"];

    /// <summary>
    /// Production assemblies this guard is responsible for. Kept honest by
    /// <c>TheScannedAssembliesCoverEveryProductionAssembly</c>, which fails if a new production
    /// project ships and nobody adds it here.
    /// </summary>
    public static readonly string[] ProductionAssemblyNames =
    [
        "ProcuLink.Api",
        "ProcuLink.Core",
        "ProcuLink.Infrastructure",
        "ProcuLink.Transform",
        "ProcuLink.Worker",
    ];

    public static IEnumerable<Assembly> ProductionAssemblies() =>
        ProductionAssemblyNames.Select(Assembly.Load);

    /// <summary>
    /// Every user-written method in <paramref name="assemblies"/> that performs a recency selection
    /// over <see cref="OutboundArtifact"/>.
    ///
    /// <para>Compiler-generated bodies — an <c>async</c> state machine's <c>MoveNext</c>, a lambda
    /// on a <c>&lt;&gt;c</c> cache, a closure's display class, a local function — are attributed
    /// back to the method a human wrote, and their evidence is merged into it. Both halves matter:
    /// without the merge, a selection expressed inside a lambda would be reported under a name no
    /// one can find, and a method whose selection sits in the lambda while its
    /// <c>OutboundArtifactSelection</c> call sits in the body would be reported as unrouted.</para>
    /// </summary>
    public static IReadOnlyList<SelectionSite> Scan(
        IEnumerable<Assembly> assemblies, Func<Type, bool>? typeFilter = null)
    {
        var accumulator = new Dictionary<string, Accumulator>(StringComparer.Ordinal);

        foreach (var assembly in assemblies)
        foreach (var type in IlReader.SafeTypes(assembly))
        {
            if (typeFilter is not null && !typeFilter(RootDeclaringType(type))) continue;

            foreach (var method in IlReader.DeclaredMethods(type))
            {
                var (operators, evidence) = Read(method);
                if (operators.Count == 0 && evidence.Count == 0) continue;

                var owner = OwnerOf(method);
                if (!accumulator.TryGetValue(owner.Key, out var acc))
                    accumulator[owner.Key] = acc = new Accumulator(owner.Key, owner.Display);

                foreach (var op in operators)
                {
                    acc.Shapes.Add(op.Shape);
                    acc.Operators.Add(op.Label);
                }

                foreach (var e in evidence) acc.Evidence.Add(e);
            }
        }

        return accumulator.Values
            .Where(a => a.Shapes.Count > 0)
            .Select(a => new SelectionSite(
                a.Key,
                a.Display,
                a.Shapes,
                a.Operators.OrderBy(x => x, StringComparer.Ordinal).ToList(),
                a.Evidence.OrderBy(x => x, StringComparer.Ordinal).ToList()))
            .OrderBy(s => s.Key, StringComparer.Ordinal)
            .ToList();
    }

    private sealed class Accumulator(string key, string display)
    {
        public string Key { get; } = key;
        public string Display { get; } = display;
        public HashSet<SelectionShape> Shapes { get; } = [];
        public HashSet<string> Operators { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Evidence { get; } = new(StringComparer.Ordinal);
    }

    // ── Reading one compiled body ─────────────────────────────────────────────

    private readonly record struct OperatorHit(SelectionShape Shape, string Label);

    /// <summary>
    /// The selection operators and the routing evidence in a single compiled body.
    ///
    /// <para>Three operand kinds carry the information, and leaving any of them out loses real
    /// sites:</para>
    /// <list type="bullet">
    /// <item><description><c>InlineMethod</c> — an ordinary <c>call</c>/<c>callvirt</c>, and also
    /// the <c>ldftn</c> that caches a lambda, so a delegate body is reachable from the method that
    /// builds it.</description></item>
    /// <item><description><c>InlineTok</c> — <c>ldtoken</c>. This is how a method reference inside
    /// an EXPRESSION TREE is emitted, and the codebase's EF queries are expression trees. The
    /// stranded-ready sweep's <c>Max(art =&gt; art.CreatedAt)</c> sits inside a <c>Where</c>
    /// subquery and appears in IL only as a token; a scanner reading calls alone would not see it
    /// at all.</description></item>
    /// <item><description><c>InlineString</c> — <c>ldstr</c>. C# inlines a <c>const string</c> at
    /// every use site, so <c>OutboundArtifactSelection.ReprocessKeyMarker</c> leaves behind a bare
    /// literal and no reference to the type that declares it. IL genuinely cannot distinguish
    /// "used the constant" from "typed the same characters" — and for this rule the two are the
    /// same act, because the string IS the discriminator. It is read from the production constant
    /// (see <see cref="ReprocessKeyMarker"/>) so a rename cannot leave the guard matching a stale
    /// spelling.</description></item>
    /// </list>
    /// </summary>
    private static (List<OperatorHit> Operators, List<string> Evidence) Read(MethodBase method)
    {
        var operators = new List<OperatorHit>();
        var evidence = new List<string>();

        var located = IlReader.BodyOf(method);
        if (located is null) return (operators, evidence);
        var (body, owner) = located.Value;

        var il = IlReader.IlOf(body);
        if (il.Length == 0) return (operators, evidence);

        var module = owner.Module;
        var typeArgs = owner.DeclaringType?.GetGenericArguments();
        var methodArgs = owner is MethodInfo mi ? mi.GetGenericArguments() : null;

        foreach (var instruction in IlReader.Decode(il))
        {
            var (op, operandStart) = (instruction.Op, instruction.OperandStart);
            if (operandStart + 4 > il.Length) continue;

            switch (op.OperandType)
            {
                case OperandType.InlineString:
                {
                    string? text = null;
                    try { text = module.ResolveString(BitConverter.ToInt32(il, operandStart)); }
                    catch { /* not a string token in this module — ignore */ }

                    if (text == ReprocessKeyMarker)
                        evidence.Add($"inlines the ReprocessKeyMarker constant \"{ReprocessKeyMarker}\"");
                    break;
                }

                case OperandType.InlineMethod or OperandType.InlineTok:
                {
                    var member = ResolveMember(module, il, operandStart, typeArgs, methodArgs);
                    if (member is null) break;

                    if (member is MethodBase callee)
                    {
                        if (SelectionOperator(callee) is { } hit) operators.Add(hit);
                        if (RoutingReference(callee.DeclaringType, UserFacingName(callee)) is { } m)
                            evidence.Add(m);
                    }
                    else if (member is FieldInfo tokenField
                             && RoutingReference(tokenField.DeclaringType, tokenField.Name) is { } f)
                    {
                        evidence.Add(f);
                    }

                    break;
                }

                case OperandType.InlineField:
                {
                    FieldInfo? field = null;
                    try { field = module.ResolveField(BitConverter.ToInt32(il, operandStart), typeArgs, methodArgs); }
                    catch { /* generic context we cannot reconstruct — skipping only makes us stricter */ }

                    if (field is not null && RoutingReference(field.DeclaringType, field.Name) is { } e)
                        evidence.Add(e);
                    break;
                }
            }
        }

        return (operators, evidence);
    }

    private static MemberInfo? ResolveMember(
        Module module, byte[] il, int operandStart, Type[]? typeArgs, Type[]? methodArgs)
    {
        try { return module.ResolveMember(BitConverter.ToInt32(il, operandStart), typeArgs, methodArgs); }
        catch { return null; }
    }

    /// <summary>
    /// The selection operator this call is, if it is one: a name from
    /// <see cref="OrderingOperators"/> or <see cref="AggregateOperators"/> whose generic
    /// instantiation actually names <see cref="OutboundArtifact"/>.
    ///
    /// <para>The generic-argument requirement is what keeps the guard from reporting every method
    /// in the codebase. <c>OrderByDescending</c> and <c>Max</c> are two of the most common calls
    /// there are; only the instantiations over <c>OutboundArtifact</c> are this rule's business.
    /// It is also why a projection is harmless: <c>Select(a =&gt; (Guid?)a.Id)</c> moves the element
    /// type to <c>Guid?</c>, and the ORDERING that preceded it — which is the act being policed —
    /// is still instantiated over <c>OutboundArtifact</c> and still seen.</para>
    /// </summary>
    private static OperatorHit? SelectionOperator(MethodBase callee)
    {
        var ordering = OrderingOperators.Contains(callee.Name);
        var aggregate = AggregateOperators.Contains(callee.Name);
        if (!ordering && !aggregate) return null;

        if (callee is not MethodInfo method || !method.IsGenericMethod) return null;

        Type[] arguments;
        try { arguments = method.GetGenericArguments(); }
        catch { return null; }

        if (!arguments.Contains(typeof(OutboundArtifact))) return null;

        var host = callee.DeclaringType?.Name ?? "?";
        var args = string.Join(", ", arguments.Select(a => a.Name));
        return new OperatorHit(
            ordering ? SelectionShape.RecencyOrdering : SelectionShape.AggregatePick,
            $"{host}.{callee.Name}<{args}>");
    }

    /// <summary>
    /// The evidence line for a reference to <paramref name="member"/> on <paramref name="declaring"/>,
    /// or null when it is not one of the rule's selection members.
    ///
    /// <para>The declaring-type walk goes outward through compiler-generated nesting, so the rule's
    /// own <c>Where(a =&gt; !IsReprocessedKey(a.FileKey))</c> lambda — lifted to
    /// <c>OutboundArtifactSelection.&lt;&gt;c</c> — still reads as the rule being consulted.</para>
    /// </summary>
    private static string? RoutingReference(Type? declaring, string member)
    {
        for (var t = declaring; t is not null; t = t.DeclaringType)
        {
            if (t != SelectionType) continue;
            return RoutingMembers.Contains(member) ? $"references {SelectionType.Name}.{member}" : null;
        }
        return null;
    }

    // ── Attributing a compiled body to the method a human wrote ───────────────

    /// <summary>
    /// The user-written method a (possibly compiler-generated) body belongs to.
    ///
    /// <para>Walks out of every generated nesting level — <c>&lt;RunAsync&gt;d__5</c> for an async
    /// state machine, <c>&lt;&gt;c</c> for a cached lambda, <c>&lt;&gt;c__DisplayClass12_0</c> for a
    /// closure — and unwraps generated method names such as <c>&lt;RunAsync&gt;b__12_0</c> and
    /// <c>&lt;RunAsync&gt;g__Local|12_0</c>. An async lambda nests twice
    /// (<c>&lt;&lt;RunAsync&gt;b__1&gt;d</c>), so the unwrap repeats until it stops changing.</para>
    /// </summary>
    private static (string Key, string Display) OwnerOf(MethodBase method)
    {
        var type = method.DeclaringType;
        var name = method.Name;

        while (type is not null && IsCompilerGenerated(type))
        {
            if (NameInsideAngles(type.Name) is { } fromType) name = fromType;
            if (type.DeclaringType is null) break;
            type = type.DeclaringType;
        }

        if (NameInsideAngles(name) is { } fromName) name = fromName;
        name = TrimLocalFunctionSuffix(name);

        var typeName = type?.FullName ?? method.DeclaringType?.FullName ?? "<unknown>";
        var shortName = type?.Name ?? method.DeclaringType?.Name ?? "<unknown>";
        return ($"{typeName}.{name}", $"{shortName}.{name}");
    }

    private static bool IsCompilerGenerated(Type type) =>
        type.GetCustomAttribute<CompilerGeneratedAttribute>() is not null
        || type.Name.StartsWith('<')
        || type.Name.StartsWith("<>", StringComparison.Ordinal);

    /// <summary>
    /// The innermost name between angle brackets, repeatedly, or null when there is none.
    /// <c>&lt;&gt;c</c> and <c>&lt;&gt;c__DisplayClass12_0</c> deliberately yield null: they carry
    /// no user method name, so the walk keeps going outward.
    /// </summary>
    private static string? NameInsideAngles(string candidate)
    {
        string? result = null;
        var current = candidate;

        while (true)
        {
            var open = current.IndexOf('<');
            if (open < 0) return result;
            var close = current.IndexOf('>', open + 1);
            if (close <= open + 1) return result;

            current = current.Substring(open + 1, close - open - 1);
            result = current;
        }
    }

    /// <summary><c>Local|12_0</c> → <c>Local</c>, for a local function's generated name.</summary>
    private static string TrimLocalFunctionSuffix(string name)
    {
        var bar = name.IndexOf('|');
        return bar > 0 ? name[..bar] : name;
    }

    private static Type RootDeclaringType(Type type)
    {
        var current = type;
        while (current.DeclaringType is not null) current = current.DeclaringType;
        return current;
    }

    private static string UserFacingName(MethodBase method) =>
        NameInsideAngles(method.Name) ?? method.Name;
}
