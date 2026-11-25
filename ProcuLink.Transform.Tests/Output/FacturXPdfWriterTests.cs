using System.Xml.Linq;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Transform.Documents;
using ProcuLink.Transform.Output;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Tokens;
using Xunit;

namespace ProcuLink.Transform.Tests.Output;

public class FacturXPdfWriterTests
{
    private static readonly PeppolPartyOptions Parties = new()
    {
        SellerName = "Seller Co", SellerCountry = "LV", SellerVatId = "LV00000000000", BuyerName = "Buyer Co", BuyerCountry = "LV",
    };

    private static (InvoiceEntity, InvoiceLineEntity[]) Invoice(string number) =>
    (
        new() { InvoiceNumber = number, IssueDate = new DateOnly(2026, 11, 1), Currency = "EUR", SubTotal = 100m, TaxTotal = 21m, GrandTotal = 121m },
        [
            new() { LineNumber = 1, Description = "Bolt", Quantity = 3, UnitPrice = 20m, LineTotal = 60m, TaxRate = 21m },
            new() { LineNumber = 2, Description = "Nut", Quantity = 2, UnitPrice = 20m, LineTotal = 40m, TaxRate = 21m },
        ]
    );

    // Serialised exactly as StandardOutputs.RenderAsync serialises it.
    private static (byte[] Pdf, byte[] Xml) Write(string number, bool creditNote, FacturXProfile profile)
    {
        var (inv, lines) = Invoice(number);
        var doc = CiiInvoiceWriter.Build(inv, lines, Parties, creditNote, null);
        var xml = System.Text.Encoding.UTF8.GetBytes(doc.Declaration + Environment.NewLine + doc);
        return (FacturXPdfWriter.Write(inv, lines, Parties, creditNote, profile, xml), xml);
    }

    private static byte[] Attachment(byte[] pdf, string name)
    {
        using var document = PdfDocument.Open(pdf);
        Assert.True(document.Advanced.TryGetEmbeddedFiles(out var files));
        var file = Assert.Single(files);
        Assert.Equal(name, string.IsNullOrEmpty(file.FileSpecification) ? file.Name : file.FileSpecification);
        return file.Memory.ToArray();
    }

    [Theory]
    [InlineData(FacturXProfile.En16931, false, "EN 16931", "factur-x.xml", "Invoice")]
    [InlineData(FacturXProfile.En16931, true, "EN 16931", "factur-x.xml", "Credit note")]
    [InlineData(FacturXProfile.XRechnung, false, "XRECHNUNG", "xrechnung.xml", "Invoice")]
    public async Task Embeds_the_given_xml_once_in_a_pdfa3b_with_its_profile_metadata(FacturXProfile profile, bool creditNote, string level, string fileName, string title)
    {
        var (pdf, xml) = Write("INV-1", creditNote, profile);

        Assert.True(Attachment(pdf, fileName).SequenceEqual(xml));
        using var document = PdfDocument.Open(pdf);
        Assert.True(document.TryGetXmpMetadata(out var xmp));
        var values = xmp.GetXDocument().Descendants().Where(e => !e.HasElements).GroupBy(e => e.Name).ToDictionary(g => g.Key, g => g.First().Value);
        XNamespace pdfaid = "http://www.aiim.org/pdfa/ns/id/", fx = "urn:factur-x:pdfa:CrossIndustryDocument:invoice:1p0#";
        Assert.Equal("3", values[pdfaid + "part"]);
        Assert.Equal("B", values[pdfaid + "conformance"]);
        Assert.Equal("INVOICE", values[fx + "DocumentType"]);
        Assert.Equal(fileName, values[fx + "DocumentFileName"]);
        Assert.Equal("1.0", values[fx + "Version"]);
        Assert.Equal(level, values[fx + "ConformanceLevel"]);

        var af = Assert.IsType<ArrayToken>(document.Structure.Catalog.CatalogDictionary.Data["AF"]);
        var spec = Assert.IsType<DictionaryToken>(document.Structure.GetObject(((IndirectReferenceToken)Assert.Single(af.Data)).Data).Data);
        Assert.Equal("Alternative", Assert.IsType<NameToken>(spec.Data["AFRelationship"]).Data);

        var text = string.Join(" ", document.GetPages().Select(p => p.Text));
        foreach (var expected in new[] { title, "INV-1", "121.00", "Seller Co", "LV00000000000" }) Assert.Contains(expected, text);

        // Review Focus 1: two writes at once never swap their attachments.
        if (profile != FacturXProfile.En16931 || creditNote) return;
        var both = await Task.WhenAll(Task.Run(() => Write("INV-1", false, profile)), Task.Run(() => Write("INV-2", false, profile)));
        Assert.All(both, w => Assert.True(Attachment(w.Pdf, fileName).SequenceEqual(w.Xml)));
        Assert.False(both[0].Xml.SequenceEqual(both[1].Xml));
    }

    [Fact]
    public void Our_own_reader_reads_back_what_we_wrote__smoke_only_not_proof()
    {
        // Our reader on our writer compares our constants to themselves; the proof is the external validators in CI.
        var read = EmbeddedInvoice.Read(Write("INV-1", false, FacturXProfile.En16931).Pdf, null);

        Assert.NotNull(read);
        Assert.Equal("Invoice", read.KindLabel);
        Assert.Equal("INV-1", read.Fields.Single(f => f.Key == FieldMeanings.DocumentNumber).Value);
        Assert.Equal("121.00", read.Fields.Single(f => f.Key == FieldMeanings.TotalGross).Value);
    }
}
