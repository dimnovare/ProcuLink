namespace ProcuLink.Api.Services.Documents;

/// <summary>The built-in sample invoice: invented, one page, text. Read like any upload; flagged so it never counts as usage.</summary>
public static class SampleDocument
{
    public const string FileName = "invoice-2250.pdf";
    public const string ContentType = "application/pdf";

    public static Stream Open() =>
        typeof(SampleDocument).Assembly.GetManifestResourceStream("sample/" + FileName)
        ?? throw new InvalidOperationException("The sample document is not embedded.");
}
