using ProcuLink.Core.Documents;
using ProcuLink.Transform.Documents;
using UglyToad.PdfPig;
using Xunit;

namespace ProcuLink.Api.Tests.Documents;

public class FacturXOutputTests
{
    private static async Task<byte[]> Render(string id, ReadResult read)
    {
        var render = await StandardOutputs.RenderAsync(id, read, FlowFieldList.Empty, "lines", Guid.Empty, default);
        Assert.True(render.Ok, render.Detail);
        using var bytes = new MemoryStream();
        await render.Result!.Content.CopyToAsync(bytes);
        if (StandardOutputs.Find(id)!.Format == FlowOutputFormats.Pdf) Assert.Equal(("application/pdf", ".pdf"), (render.Result.ContentType, render.Result.FileExtension));
        return bytes.ToArray();
    }

    // A line description far past one table row: QuestPDF wraps it, so the render stays Ok (Review Focus 4).
    private static ReadResult LongDescription(ReadResult read)
    {
        var t = read.Tables[0];
        Assert.Contains("description", t.Rows[0].Cells.Keys);
        var first = new ReadRow(t.Rows[0].Cells.ToDictionary(c => c.Key, c => c.Key == "description" ? c.Value with { Value = new string('x', 2000) } : c.Value));
        return new ReadResult(read.KindLabel, read.Fields, [t with { Rows = [first, .. t.Rows.Skip(1)] }]);
    }

    [Theory]
    [InlineData("factur-x-en16931", "en16931-cii", "factur-x.xml", false, false)]
    [InlineData("factur-x-en16931-credit-note", "en16931-cii-credit-note", "factur-x.xml", false, false)]
    [InlineData("factur-x-en16931", "en16931-cii", "factur-x.xml", false, true)]
    [InlineData("zugferd-xrechnung", "xrechnung-cii", "xrechnung.xml", true, false)]
    [InlineData("zugferd-xrechnung-credit-note", "xrechnung-cii-credit-note", "xrechnung.xml", true, false)]
    public async Task Each_facturx_output_carries_its_twins_exact_xml(string id, string twin, string fileName, bool rich, bool longDescription)
    {
        var read = rich ? PeppolRulesProofTests.RichRead() : PeppolRulesProofTests.Read();
        if (longDescription) read = LongDescription(read);

        using var pdf = PdfDocument.Open(await Render(id, read));

        Assert.Equal(twin, StandardOutputs.Find(id)!.Embeds);
        Assert.True(pdf.Advanced.TryGetEmbeddedFiles(out var files));
        var file = Assert.Single(files);
        Assert.Equal(fileName, file.FileSpecification);
        Assert.True(file.Memory.ToArray().SequenceEqual(await Render(twin, read)));
    }
}
