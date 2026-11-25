using System.Text;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Transform.Documents;
using ProcuLink.Transform.Output;
using Xunit;

namespace ProcuLink.Api.Tests.Documents;

/// <summary>
/// Every e-invoice output we write, rendered for the external validators that .github/workflows/e-invoice-conformance.yml runs
/// (Mustang, KoSIT). With EINVOICE_SAMPLE_DIR set the files are written there; without it only the assertions run.
/// Each validator also gets a negative control our emitter cannot produce, so a validator that cannot fail is caught.
/// </summary>
public class EInvoiceSamplesTests
{
    private const string Gross = "<ram:GrandTotalAmount>121.00</ram:GrandTotalAmount>";

    [Fact]
    public async Task Every_e_invoice_output_renders_for_the_external_validators()
    {
        var files = new Dictionary<string, byte[]>();
        foreach (var standard in StandardOutputs.All.Where(s => s.Vesid is not null))
        {
            var render = await StandardOutputs.RenderAsync(standard.Id, standard.RuleSet == "XRechnung" ? PeppolRulesProofTests.RichRead() : PeppolRulesProofTests.Read(),
                FlowFieldList.Empty, "lines", Guid.Empty, default);
            Assert.True(render.Ok, $"{standard.Id}: {render.Detail}");
            using var bytes = new MemoryStream();
            await render.Result!.Content.CopyToAsync(bytes);
            files[standard.Id + render.Result.FileExtension] = bytes.ToArray();
        }
        Assert.Equal(StandardOutputs.All.Count(s => s.Vesid is not null), files.Count);
        Assert.True(files.Count >= 10, "anti-vacuity: the e-invoice outputs must not silently drop out");

        // BR-CO-15: the grand total no longer equals net + VAT.
        string Broken(string id)
        {
            var xml = Encoding.UTF8.GetString(files[id + ".xml"]);
            var broken = xml.Replace(Gross, "<ram:GrandTotalAmount>999.00</ram:GrandTotalAmount>");
            Assert.NotEqual(xml, broken);
            return broken;
        }
        var cii = Encoding.UTF8.GetBytes(Broken("en16931-cii"));
        files["negative-factur-x-en16931.pdf"] = FacturXPdfWriter.Write(
            new InvoiceEntity { InvoiceNumber = "INV-1", IssueDate = new DateOnly(2026, 11, 1), Currency = "EUR", SubTotal = 100m, TaxTotal = 21m, GrandTotal = 121m },
            [new InvoiceLineEntity { LineNumber = 1, Description = "Bolt", Quantity = 5, UnitPrice = 20m, LineTotal = 100m, TaxRate = 21m }],
            new PeppolPartyOptions { SellerName = "Seller Co", SellerCountry = "LV", SellerVatId = "LV00000000000", BuyerName = "Buyer Co", BuyerCountry = "LV" },
            false, FacturXProfile.En16931, cii);
        files["negative-xrechnung-cii.xml"] = Encoding.UTF8.GetBytes(Broken("xrechnung-cii"));

        if (Environment.GetEnvironmentVariable("EINVOICE_SAMPLE_DIR") is not { Length: > 0 } dir) return;
        Directory.CreateDirectory(dir);
        foreach (var (name, bytes) in files) await File.WriteAllBytesAsync(Path.Combine(dir, name), bytes);
    }
}
