using ProcuLink.Core.Documents;
using Xunit;

namespace ProcuLink.Api.Tests.Documents;

public class DocumentKindTests
{
    [Theory]
    [InlineData("Invoice", "invoice")]
    [InlineData("INVOICES", "invoice")]
    [InlineData("Supplier invoice", "invoice")]
    [InlineData("Invoice (copy)", "invoice")]
    [InlineData("Purchase Orders", "purchase order")]
    [InlineData("delivery-notes", "delivery note")]
    [InlineData("Companies", "company")]
    [InlineData("Boxes", "box")]
    [InlineData("Status", "status")]
    [InlineData("Analysis", "analysis")]
    [InlineData("  ", null)]
    [InlineData(null, null)]
    public void Normalize_is_stable(string? label, string? expected) =>
        Assert.Equal(expected, DocumentKind.Normalize(label));

    [Theory]
    [InlineData("Tax invoice", "Invoice")]
    [InlineData("Bill", "Invoice")]
    [InlineData("PO", "Purchase order")]
    public void Normalize_does_not_merge(string first, string second) =>
        Assert.NotEqual(DocumentKind.Normalize(first), DocumentKind.Normalize(second));

    [Fact]
    public void Sender_key_never_uses_an_email_domain()
    {
        Assert.Null(DocumentKind.SenderKeyOf(new ReadResult("Invoice"), IntakeKind.Email, "orders@example.test"));

        var named = new ReadResult("Invoice",
            [new ReadField("supplier", "Supplier", "Example Supplies", FieldTypes.Text, FieldMeanings.SenderName, 0.9, null, false, false, false)]);
        Assert.Equal("name:example supplies", DocumentKind.SenderKeyOf(named, IntakeKind.Email, "orders@example.test"));

        Assert.Equal("Invoices", DocumentKind.FlowNameFor("Invoice"));
        Assert.Equal("Documents", DocumentKind.FlowNameFor(null));
    }
}
