using System.Reflection;
using System.Reflection.Emit;
using FluentAssertions;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Transform.Output;
using Xunit;

namespace ProcuLink.Api.Tests.Architecture;

/// <summary>
/// Answers, from compiled IL: <b>does every write path that takes an output format from a CALLER and
/// stores it on a transform-driving row actually check it against the buildable set?</b>
///
/// <para><b>Which direction the next instance comes from.</b> The reported defect was one write path
/// (<c>SupplierConnectionService.ApplyScalars</c>) that never had the check its sibling
/// (<c>DeliveryConfigService.UpsertAsync</c>) did. Fixing that one and pinning it with a behavioural
/// test would leave the shape untouched: the next instance is a THIRD write path, added later,
/// which nobody remembers to route through the allow-list — and it would fail the same way, silently,
/// until an order died at transform. So this guard is not about the two known sites. It enumerates
/// the sites, from metadata, and demands the property of all of them.</para>
///
/// <para><b>The rule.</b> A method that (a) writes <c>OutputFormat</c> onto
/// <see cref="SupplierConnectionRevision"/> or <see cref="SupplierDeliveryConfig"/> — the two rows
/// <c>IEffectiveConnectionConfigResolver</c> hands to the transform — AND (b) reads
/// <c>OutputFormat</c> off some OTHER type, i.e. takes it from a caller-supplied contract rather than
/// copying it forward from a row that is already stored, must provably reach
/// <see cref="OutputFormatCatalog.Normalize"/>.</para>
///
/// <para><b>Why the second clause.</b> Clone-from-active, rollback, republish-from-live and the V1
/// backfill all assign <c>OutputFormat</c> and are deliberately NOT validated — they carry a bundle
/// that is already live, and refusing them would turn a write-time guard into an outage for an
/// organisation that already holds such a revision. Every one of those reads the value off an entity;
/// every caller-facing path reads it off a request/DTO type. The distinction is structural, so it
/// needs no hand-maintained exemption list — which is the thing that rots.</para>
///
/// <para><b>Deliberately not in scope:</b> <c>SupplierProfileEntity.OutputFormat</c>
/// (<c>SuppliersController.UpsertProfile</c>). That column is reporting data surfaced by
/// <c>PassportService</c>; it never reaches <c>OrderTransformService</c>, and it is populated from a
/// supplier's declared <c>AcceptedFormats</c>, which are not ProcuLink format tokens. A bad value
/// there does not kill an order, so demanding the transform allow-list of it would be wrong, not
/// stricter.</para>
///
/// <para>IL rather than source text, for the reason <see cref="BillingGateIlScanner"/> gives: a
/// metadata token cannot be fooled by a comment, a string literal or a half-applied rename. The
/// decoder itself is <see cref="IlReader"/>, shared — only the question asked of it lives here.</para>
/// </summary>
public sealed class OutputFormatWritePathsValidateTests
{
    private static readonly string[] ProductionAssemblies =
    [
        "ProcuLink.Api",
        "ProcuLink.Core",
        "ProcuLink.Infrastructure",
        "ProcuLink.Worker",
    ];

    /// <summary>
    /// The rows whose <c>OutputFormat</c> decides what a real order is transformed into: the pinned
    /// connection revision and the live delivery config. Asserted to exist by
    /// <see cref="TheScanFindsTheSitesItIsSupposedTo"/>, so a rename fails loudly instead of quietly
    /// emptying the scan.
    /// </summary>
    private static readonly Type[] TransformDrivingRows =
    [
        typeof(SupplierConnectionRevision),
        typeof(SupplierDeliveryConfig),
    ];

    private static readonly MethodInfo NormalizePrimitive =
        typeof(OutputFormatCatalog).GetMethod(nameof(OutputFormatCatalog.Normalize))
        ?? throw new InvalidOperationException(
            "OutputFormatCatalog.Normalize not found — the allow-list primitive was renamed. Update " +
            "this guard in the same change, or every write path silently reads as validated.");

    // ── The guard ─────────────────────────────────────────────────────────────

    [Fact]
    public void EveryCallerFacingOutputFormatWriteReachesTheAllowList()
    {
        var unguarded = CallerFacingWriteSites()
            .Where(site => !ReachesNormalize(site.Method))
            .Select(site => $"{site.Method.DeclaringType?.FullName}.{site.Method.Name} " +
                            $"(takes OutputFormat from {site.SourceTypes}, writes it to {site.SinkTypes})")
            .ToList();

        unguarded.Should().BeEmpty(
            "a method that stores a caller-supplied output format on a transform-driving row must run " +
            "it through OutputFormatCatalog.Normalize. Without it, a format no registered transform " +
            "can build (UblOrder / X12_850 / EdifactOrders — they name conformance profiles, and " +
            "EDIFACT is inbound-only) is accepted at write time and discovered only when an order " +
            "reaches \"No transform service registered for format '...'\" and dies terminally. If the " +
            "site listed here legitimately copies an already-stored bundle forward, it should be " +
            "reading OutputFormat off the entity, not off a request type");
    }

    /// <summary>
    /// The anti-vacuity floor. A scanner that resolved nothing — wrong assembly names, a renamed
    /// property, an IL decode that bails on the first instruction — would report an empty violation
    /// list and pass. So: the scan must find the two sites we know are caller-facing, and both must
    /// be validated.
    /// </summary>
    [Fact]
    public void TheScanFindsTheSitesItIsSupposedTo()
    {
        foreach (var row in TransformDrivingRows)
            row.GetProperty("OutputFormat").Should().NotBeNull(
                $"{row.Name}.OutputFormat is what this guard watches; a rename must fail here rather " +
                "than silently empty the scan");

        var sites = CallerFacingWriteSites()
            .Select(s => $"{s.Method.DeclaringType?.Name}.{s.Method.Name}")
            .ToList();

        sites.Should().Contain(
            s => s.Contains("SupplierConnectionService") && s.Contains("ApplyScalars"),
            "the connection-revision write path is the site the defect was reported on");
        sites.Should().Contain(
            s => s.Contains("DeliveryConfigService") && s.Contains("UpsertAsync"),
            "the live delivery-config write path is its sibling, and has always validated");
    }

    /// <summary>
    /// Both write paths reach the SAME primitive. Two independent checks that agree today are how the
    /// gap opened in the first place — the delivery-config path had a hand-typed
    /// <c>{ "xml", "csv", "cxml", "json", "ubl", "x12" }</c> and the revision path had nothing.
    /// </summary>
    [Fact]
    public void BothWritePathsReachTheSamePrimitive()
    {
        var reached = CallerFacingWriteSites()
            .Select(s => (Site: $"{s.Method.DeclaringType?.Name}.{s.Method.Name}", Reaches: ReachesNormalize(s.Method)))
            .ToList();

        reached.Should().HaveCountGreaterThanOrEqualTo(2);
        reached.Should().OnlyContain(r => r.Reaches);
    }

    // ── Scanner ───────────────────────────────────────────────────────────────

    private readonly record struct WriteSite(MethodBase Method, string SourceTypes, string SinkTypes);

    /// <summary>
    /// Every production method that reads <c>OutputFormat</c> off a non-row type and writes it onto
    /// one of <see cref="TransformDrivingRows"/>.
    /// </summary>
    private static IReadOnlyList<WriteSite> CallerFacingWriteSites()
    {
        var sites = new List<WriteSite>();

        foreach (var assembly in ProductionAssemblies.Select(Assembly.Load))
        foreach (var type in IlReader.SafeTypes(assembly))
        foreach (var method in IlReader.DeclaredMethods(type))
        {
            var sinks = new HashSet<Type>();
            var sources = new HashSet<Type>();

            foreach (var callee in CallsIn(method))
            {
                var owner = callee.DeclaringType;
                if (owner is null) continue;

                if (callee.Name == "set_OutputFormat" && TransformDrivingRows.Contains(owner))
                    sinks.Add(owner);

                // A read off one of the rows themselves is a copy-forward of an already-stored bundle,
                // not a caller introducing a value. Anything else is caller-supplied.
                if (callee.Name == "get_OutputFormat" && !TransformDrivingRows.Contains(owner))
                    sources.Add(owner);
            }

            if (sinks.Count > 0 && sources.Count > 0)
            {
                sites.Add(new WriteSite(
                    Unwrap(method, type),
                    string.Join("/", sources.Select(t => t.Name).Order()),
                    string.Join("/", sinks.Select(t => t.Name).Order())));
            }
        }

        return sites;
    }

    /// <summary>
    /// True when <paramref name="root"/> provably reaches <see cref="OutputFormatCatalog.Normalize"/>,
    /// following calls into our own assemblies. Depth is needed because neither site calls it
    /// directly: <c>ApplyScalars</c> → <c>ValidateOutputFormat</c> → <c>Normalize</c>, and
    /// <c>UpsertAsync</c> → <c>NormalizeOutputFormat</c> → <c>Normalize</c>. Demanding a direct call
    /// would force both sites to inline the primitive, which is the duplication this replaces.
    /// </summary>
    private static bool ReachesNormalize(MethodBase root, int maxDepth = 4)
    {
        var seen = new HashSet<string>();
        var queue = new Queue<(MethodBase Method, int Depth)>();
        queue.Enqueue((root, 0));

        while (queue.Count > 0)
        {
            var (method, depth) = queue.Dequeue();
            if (!seen.Add(Key(method))) continue;

            foreach (var callee in CallsIn(method))
            {
                if (callee.Name == NormalizePrimitive.Name
                    && callee.DeclaringType == NormalizePrimitive.DeclaringType) return true;

                if (depth < maxDepth && IsOurs(callee)) queue.Enqueue((callee, depth + 1));
            }
        }

        return false;
    }

    private static string Key(MethodBase m) =>
        $"{m.DeclaringType?.FullName}.{m.Name}/{m.GetParameters().Length}";

    private static bool IsOurs(MethodBase method) =>
        method.DeclaringType?.Assembly.GetName().Name is { } name && ProductionAssemblies.Contains(name);

    /// <summary>
    /// The methods <paramref name="method"/> calls. The IL decoding is <see cref="IlReader"/>'s;
    /// resolving the token to a <see cref="MethodBase"/> is the only step here, and an unresolvable
    /// generic context is skipped — this scanner only ever PROVES a check is reached, so a skipped
    /// edge can make a test stricter, never wave a missing check through.
    /// </summary>
    private static IEnumerable<MethodBase> CallsIn(MethodBase method)
    {
        var located = IlReader.BodyOf(method);
        if (located is null) yield break;
        var (body, owner) = located.Value;

        var il = IlReader.IlOf(body);
        if (il.Length == 0) yield break;

        var module = owner.Module;

        foreach (var instruction in IlReader.Decode(il))
        {
            if (instruction.Op.OperandType != OperandType.InlineMethod) continue;

            MethodBase? callee = null;
            try
            {
                var token = BitConverter.ToInt32(il, instruction.OperandStart);
                callee = module.ResolveMethod(
                    token,
                    owner.DeclaringType?.GetGenericArguments(),
                    owner is MethodInfo mi ? mi.GetGenericArguments() : null);
            }
            catch
            {
                // Unreconstructable generic context — skip, never guess.
            }

            if (callee is not null) yield return callee;
        }
    }

    /// <summary>
    /// Reports a compiler-generated state-machine <c>MoveNext</c> under the async method a human
    /// wrote, so a failure names <c>DeliveryConfigService.UpsertAsync</c> rather than
    /// <c>&lt;UpsertAsync&gt;d__7.MoveNext</c>.
    /// </summary>
    private static MethodBase Unwrap(MethodBase method, Type declaringType)
    {
        if (method.Name != "MoveNext" || declaringType.DeclaringType is null) return method;

        var generatedName = declaringType.Name;
        var open = generatedName.IndexOf('<');
        var close = generatedName.IndexOf('>');
        if (open < 0 || close <= open) return method;

        var originalName = generatedName.Substring(open + 1, close - open - 1);
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
                                 | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var match = declaringType.DeclaringType.GetMethods(flags).FirstOrDefault(m => m.Name == originalName);
        return match ?? method;
    }
}
