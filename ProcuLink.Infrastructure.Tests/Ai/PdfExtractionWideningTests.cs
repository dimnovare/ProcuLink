using System;
using ProcuLink.Core.Services.Ai;          // ExtractedOrder / ExtractedOrderLine / ExtractedParty
using ProcuLink.Api.Services;               // OrderIngestionService.MapExtractedToParsedForTest
using Xunit;

namespace ProcuLink.Infrastructure.Tests.Ai;

public class MapExtractedToParsedTests
{
    [Fact]
    public void Map_propagates_parties_contact_and_line_mpn()
    {
        var extracted = new ExtractedOrder(
            "PO1", new DateTime(2026, 6, 12), "Acme Buyer", "EUR",
            new[] { new ExtractedOrderLine(1, "B1", "Widget", 2m, "PC", 5m, 10m, 0m, null,
                ManufacturerPartNumber: "MPN-9", Recipient: "redacted@example.invalid") },
            Parties: new[] { new ExtractedParty("shipTo", Name: "Acme DC", City: "Linz", Vat: "ATU1") },
            ContactEmail: "redacted@example.invalid", Incoterms: "DDP");

        var parsed = OrderIngestionService.MapExtractedToParsedForTest(extracted);

        Assert.Equal("DDP", parsed.Incoterms);
        Assert.Equal("redacted@example.invalid", parsed.ContactEmail);
        Assert.Single(parsed.Parties!);
        Assert.Equal("ATU1", parsed.Parties![0].Vat);
        Assert.Equal("MPN-9", parsed.Lines[0].ManufacturerPartNumber);
        Assert.Equal("redacted@example.invalid", parsed.Lines[0].Recipient);
    }
}
