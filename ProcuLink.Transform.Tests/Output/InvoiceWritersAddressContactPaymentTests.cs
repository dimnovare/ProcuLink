using System.Xml.Linq;
using ProcuLink.Core.Entities;
using ProcuLink.Transform.Output;
using Xunit;

namespace ProcuLink.Transform.Tests.Output;

public class InvoiceWritersAddressContactPaymentTests
{
    private static readonly InvoiceEntity Inv = new()
    {
        InvoiceNumber = "INV-1", IssueDate = new DateOnly(2026, 11, 1), Currency = "EUR", SubTotal = 100m, TaxTotal = 19m, GrandTotal = 119m,
    };
    private static readonly InvoiceLineEntity[] Lines = [new() { LineNumber = 1, Description = "Item", Quantity = 1, UnitPrice = 100m, LineTotal = 100m, TaxRate = 19m }];

    private static (XDocument Ubl, XDocument Cii) Render(PeppolPartyOptions p) =>
        (new PeppolBisInvoiceTransformService().BuildDocument(Inv, Lines, p, false), CiiInvoiceWriter.Build(Inv, Lines, p, false));

    private static IEnumerable<string> Names(XDocument d, string name) => d.Descendants().Where(e => e.Name.LocalName == name).Select(e => e.Value);

    private static IEnumerable<string> Leaves(XDocument d, string name) =>
        d.Descendants().First(e => e.Name.LocalName == name).Descendants().Where(e => !e.HasElements).Select(e => e.Value);

    [Fact]
    public void Ubl_and_cii_write_address_contact_and_payment_when_present()
    {
        var (ubl, cii) = Render(new PeppolPartyOptions
        {
            SellerName = "Seller", BuyerName = "Buyer", SellerCountry = "DE", BuyerCountry = "DE",
            SellerStreet = "Main 1", SellerCity = "Berlin", SellerPostcode = "10115", BuyerStreet = "Side 2", BuyerCity = "Bonn", BuyerPostcode = "53111",
            SellerContactName = "Ann", SellerContactPhone = "+49 30 0000000", SellerContactEmail = "ann@example.com",
            PayeeIban = "DE02120300000000202051", PaymentReference = "REF-1",
        });
        Assert.Equal(["Main 1", "Side 2"], Names(ubl, "StreetName"));
        Assert.Equal(["Berlin", "Bonn"], Names(ubl, "CityName"));
        Assert.Equal(["10115", "53111"], Names(ubl, "PostalZone"));
        Assert.Equal(["Ann", "+49 30 0000000", "ann@example.com"], Leaves(ubl, "Contact"));
        Assert.Equal(["58", "REF-1", "DE02120300000000202051"], Leaves(ubl, "PaymentMeans"));
        Assert.Equal(["Main 1", "Side 2"], Names(cii, "LineOne"));
        Assert.Equal(["Berlin", "Bonn"], Names(cii, "CityName"));
        Assert.Equal(["10115", "53111"], Names(cii, "PostcodeCode"));
        Assert.Equal(["Ann", "+49 30 0000000", "ann@example.com"], Leaves(cii, "DefinedTradeContact"));
        Assert.Equal(["58", "DE02120300000000202051"], Leaves(cii, "SpecifiedTradeSettlementPaymentMeans"));
        Assert.Equal(["REF-1"], Names(cii, "PaymentReference"));
    }

    [Fact]
    public void Only_a_city_writes_no_empty_elements()
    {
        var (ubl, cii) = Render(new PeppolPartyOptions { SellerName = "Seller", SellerCity = "Berlin" });
        Assert.Equal(["Berlin"], Names(ubl, "CityName"));
        Assert.Equal(["Berlin"], Names(cii, "CityName"));
        foreach (var gone in new[] { "StreetName", "PostalZone", "Contact", "PaymentMeans", "LineOne", "PostcodeCode", "DefinedTradeContact", "SpecifiedTradeSettlementPaymentMeans" })
        {
            Assert.DoesNotContain(ubl.Descendants(), e => e.Name.LocalName == gone);
            Assert.DoesNotContain(cii.Descendants(), e => e.Name.LocalName == gone);
        }
    }
}
