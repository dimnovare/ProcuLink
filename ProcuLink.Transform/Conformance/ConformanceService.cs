using ProcuLink.Core.Services;

namespace ProcuLink.Transform.Conformance;

/// <summary>
/// Default <see cref="IConformanceService"/>. Maps each standards
/// <see cref="OutputFormat"/> to its named <see cref="StandardsProfile"/> and
/// dispatches the document to the matching <see cref="IProfileChecker"/>.
///
/// <para>
/// The checkers are pure and never throw on malformed input (a well-formedness /
/// structure failure is itself a failed check), so <see cref="Check(string, OutputFormat)"/>
/// always returns a report rather than throwing — except for an unsupported format,
/// which is a programming error guarded by <see cref="SupportsFormat"/>.
/// </para>
/// </summary>
public sealed class ConformanceService : IConformanceService
{
    private readonly IReadOnlyDictionary<StandardsProfile, IProfileChecker> _checkers;

    public ConformanceService()
    {
        var checkers = new IProfileChecker[]
        {
            new CxmlProfileChecker(),
            new UblProfileChecker(),
            new X12ProfileChecker(),
            new EdifactProfileChecker(),
            new IDocProfileChecker(),
        };
        _checkers = checkers.ToDictionary(c => c.Profile);
    }

    public bool SupportsFormat(OutputFormat format) => ProfileForFormat(format) is not null;

    public StandardsProfile? ProfileForFormat(OutputFormat format) => format switch
    {
        OutputFormat.CXml          => StandardsProfile.CXml12OrderRequest,
        OutputFormat.Ubl           => StandardsProfile.Ubl21Order,
        OutputFormat.UblOrder      => StandardsProfile.Ubl21Order,
        OutputFormat.X12           => StandardsProfile.X12_850,
        OutputFormat.X12_850       => StandardsProfile.X12_850,
        OutputFormat.EdifactOrders => StandardsProfile.EdifactOrdersD96A,
        // Xml / Csv / Json are generic supplier templates with no named standard profile.
        _ => null,
    };

    public ConformanceReport Check(string documentText, OutputFormat format)
    {
        var profile = ProfileForFormat(format)
            ?? throw new NotSupportedException(
                $"Output format '{format}' has no named conformance profile. " +
                "Guard with SupportsFormat(format) first.");
        return Check(documentText, profile);
    }

    public ConformanceReport Check(string documentText, StandardsProfile profile)
    {
        if (!_checkers.TryGetValue(profile, out var checker))
            throw new NotSupportedException($"No conformance checker is registered for profile '{profile}'.");

        return checker.Check(documentText ?? string.Empty);
    }
}
