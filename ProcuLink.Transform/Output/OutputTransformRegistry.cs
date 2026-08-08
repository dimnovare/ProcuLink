using Microsoft.Extensions.DependencyInjection;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Delivery;

namespace ProcuLink.Transform.Output;

/// <summary>
/// THE list of entity-based outbound transforms ProcuLink ships, and the two things that must never
/// disagree about it: what the host REGISTERS, and what the write paths ACCEPT as an output format.
///
/// <para>Both used to be typed out separately — six <c>AddSingleton&lt;ITransformService, …&gt;</c>
/// lines in each of two <c>Program.cs</c> files, and a hand-typed
/// <c>{ "xml", "csv", "cxml", "json", "ubl", "x12" }</c> in the delivery-config service. A seventh
/// transform would have had to be remembered in all three, and the connection-revision write path
/// had never been told about the list at all: it assigned any string
/// <c>Enum.TryParse(ignoreCase: true)</c> could re-hydrate, including
/// <see cref="OutputFormat.EdifactOrders"/>, which no transform in this solution can build.</para>
///
/// <para>So: <see cref="All"/> is the list, <see cref="AddOutputTransforms"/> is how a host registers
/// it, and <see cref="Catalog"/> is the buildable-format allow-list DERIVED from it by asking each
/// transform <see cref="ITransformService.CanTransform"/>. Adding a transform here reaches DI, the
/// allow-list and the operator-facing message in one edit.</para>
///
/// <para>The instances are shared singletons, which is what the hosts already registered them as —
/// every implementation is stateless and holds no per-request state.</para>
/// </summary>
public static class OutputTransformRegistry
{
    /// <summary>Every registered <see cref="ITransformService"/>, in the order the hosts registered them.</summary>
    public static IReadOnlyList<ITransformService> All { get; } = new ITransformService[]
    {
        new XmlTransformService(),
        new CsvTransformService(),
        new CxmlTransformService(),
        new JsonTransformService(),
        new UblOrderTransformService(), // Group M Phase 1 — plain OASIS UBL 2.1 Order; no Peppol profile is declared
        new X12TransformService(),      // Group M — ANSI X12 850
    };

    /// <summary>
    /// The output formats a caller may name, derived from <see cref="All"/>. Used by EVERY write path
    /// that stores an output format — the live delivery-config row and the connection revision — so
    /// the two cannot drift, and a third path added later inherits the same answer by using the same
    /// object.
    /// </summary>
    public static OutputFormatCatalog Catalog { get; } = new(All);

    /// <summary>
    /// Register the transform layer. The hosts call THIS rather than listing the implementations, so
    /// the registered set and <see cref="Catalog"/> are the same list by construction rather than by
    /// anybody remembering.
    /// </summary>
    public static IServiceCollection AddOutputTransforms(this IServiceCollection services)
    {
        foreach (var transform in All)
            services.AddSingleton<ITransformService>(transform);

        return services;
    }
}
