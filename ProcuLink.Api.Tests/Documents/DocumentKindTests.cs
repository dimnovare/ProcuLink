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
    }

    [Theory]
    [InlineData("Invoice", "Invoice")]
    [InlineData("invoice", "Invoice")]
    [InlineData("INVOICE", "Invoice")]
    [InlineData("PURCHASE ORDER", "Purchase order")]
    [InlineData("Purchase Orders", "Purchase Orders")]
    [InlineData("Sales order", "Sales order")]
    [InlineData("Bill of lading", "Bill of lading")]
    [InlineData("CMR note", "CMR note")]
    [InlineData("Rechnung", "Rechnung")]
    [InlineData("Supplier invoice", "Supplier invoice")]
    [InlineData("Invoice (copy)", "Invoice (copy)")]
    [InlineData("  Credit   note ", "Credit note")]
    [InlineData("?!", "Documents")]
    [InlineData(null, "Documents")]
    public void Flow_name_is_the_kind_as_read(string? label, string expected) =>
        Assert.Equal(expected, DocumentKind.FlowNameFor(label));

    [Fact]
    public void A_long_kind_is_cut_at_a_word()
    {
        var name = DocumentKind.FlowNameFor(string.Join(' ', Enumerable.Repeat("Delivery", 22)) + " (copy)"); // 205 characters
        Assert.InRange(name.Length, 41, 80);
        Assert.Equal(name.Trim(), name);
        Assert.DoesNotContain("(", name);
    }
}
