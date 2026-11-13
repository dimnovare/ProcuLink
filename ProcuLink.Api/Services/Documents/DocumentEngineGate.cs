using Microsoft.Extensions.Options;
using ProcuLink.Core.Documents;

namespace ProcuLink.Api.Services.Documents;

public sealed class DocumentEngineOptions
{
    public string[] EnabledOrgIds { get; set; } = [];
    public bool EnabledForAll { get; set; }
    public string[] LookupSuggestionOrgIds { get; set; } = [];
    public bool LookupSuggestionsForAll { get; set; }
    public string[] ScanOrgIds { get; set; } = [];
    public bool ScansForAll { get; set; }
    public string[] FormatOrgIds { get; set; } = [];
    public bool FormatsForAll { get; set; }
    public string[] DocsOutputOrgIds { get; set; } = [];
    public bool DocsOutputsForAll { get; set; }
    public string[] PeppolOrgIds { get; set; } = [];
    public bool PeppolForAll { get; set; }
    public string[] NativeConnectorOrgIds { get; set; } = [];
    public bool NativeConnectorsForAll { get; set; }
}

/// <summary>Per-org switch for the document engine; off unless listed or enabled for all.</summary>
public sealed class DocumentEngineGate(IOptions<DocumentEngineOptions> options) : ProcuLink.Core.Services.IOrderIntakeSwitch
{
    private static bool Listed(string[] ids, Guid orgId) => ids.Any(s => Guid.TryParse(s, out var g) && g == orgId);

    public bool IsEnabled(Guid orgId) => options.Value.EnabledForAll || Listed(options.Value.EnabledOrgIds, orgId);

    /// <summary>An org on the document engine no longer creates legacy purchase orders.</summary>
    public bool IsRetired(Guid orgId) => IsEnabled(orgId);

    /// <summary>AI-suggested lookup matches: the engine must be on, then the org is listed or all are on.</summary>
    public bool LookupSuggestionsOn(Guid orgId) =>
        IsEnabled(orgId) &&
        (options.Value.LookupSuggestionsForAll || Listed(options.Value.LookupSuggestionOrgIds, orgId));

    /// <summary>Scanned PDFs and photos: the engine must be on, then the org is listed or all are on.</summary>
    public bool ScansOn(Guid orgId) =>
        IsEnabled(orgId) && (options.Value.ScansForAll || Listed(options.Value.ScanOrgIds, orgId));

    /// <summary>Word, text, XML, JSON and EDI files: the engine must be on, then the org is listed or all are on.</summary>
    public bool FormatsOn(Guid orgId) =>
        IsEnabled(orgId) && (options.Value.FormatsForAll || Listed(options.Value.FormatOrgIds, orgId));

    /// <summary>Outputs designed from documentation by the AI: the engine must be on, then the org is listed or all are on.</summary>
    public bool DocsOutputsOn(Guid orgId) =>
        IsEnabled(orgId) && (options.Value.DocsOutputsForAll || Listed(options.Value.DocsOutputOrgIds, orgId));

    /// <summary>Peppol BIS Billing outputs and their rule checks: the engine must be on, then the org is listed or all are on.</summary>
    public bool PeppolOn(Guid orgId) =>
        IsEnabled(orgId) && (options.Value.PeppolForAll || Listed(options.Value.PeppolOrgIds, orgId));

    /// <summary>Google Drive and Google Sheets destinations: the engine must be on, then the org is listed or all are on.</summary>
    public bool NativeConnectorsOn(Guid orgId) =>
        IsEnabled(orgId) && (options.Value.NativeConnectorsForAll || Listed(options.Value.NativeConnectorOrgIds, orgId));

    public string[] AcceptedFormats(Guid orgId) => DocumentFormats.For(ScansOn(orgId), FormatsOn(orgId));
}
