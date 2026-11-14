using ProcuLink.Core.Services;

namespace ProcuLink.Transform.Output;

/// <summary>
/// THE list of entity-based outbound transforms ProcuLink ships. The engine's standard outputs
/// (<c>StandardOutputs</c>) pick their writer from <see cref="All"/> by asking each transform
/// <see cref="ITransformService.CanTransform"/>. Nothing registers these in DI: the
/// <c>AddOutputTransforms</c> registration had no consumer and went (plan 13b, 2026-11-13).
///
/// <para>The instances are shared — every implementation is stateless and holds no per-request
/// state.</para>
/// </summary>
public static class OutputTransformRegistry
{
    /// <summary>Every shipped <see cref="ITransformService"/>.</summary>
    public static IReadOnlyList<ITransformService> All { get; } = new ITransformService[]
    {
        new XmlTransformService(),
        new CsvTransformService(),
        new CxmlTransformService(),
        new JsonTransformService(),
        new UblOrderTransformService(), // Group M Phase 1 — plain OASIS UBL 2.1 Order; no Peppol profile is declared
        new X12TransformService(),      // Group M — ANSI X12 850
        new PdfTransformService(),      // orders
    };
}
