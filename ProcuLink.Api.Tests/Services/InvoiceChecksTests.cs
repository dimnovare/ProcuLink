using FluentAssertions;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Documents;
using Xunit;

namespace ProcuLink.Api.Tests.Services;

public sealed class InvoiceChecksTests
{
    private static PurchaseOrderEntity Invoice(decimal line1, decimal line2, decimal? net, decimal? tax, decimal? total) => new()
    {
        SubTotal = net, TaxTotal = tax, GrandTotal = total, PaymentIban = "DE89370400440532013000",
        Lines =
        {
            new PurchaseOrderLineEntity { LineNumber = 1, Quantity = 1m, UnitPrice = line1, LineAmount = line1 },
            new PurchaseOrderLineEntity { LineNumber = 2, Quantity = 2m, UnitPrice = line2 / 2m },
        },
    };

    [Fact]
    public void BalancedInvoice_HasNoProblems() =>
        InvoiceChecks.Run(Invoice(60m, 40m, 100m, 21m, 121m)).Should().BeEmpty();

    [Fact]
    public void NetMismatch_ReportsIt() =>
        InvoiceChecks.Run(Invoice(50m, 40m, 100m, 21m, 121m))
            .Should().Equal("Line amounts do not add up to the net total.");

    [Fact]
    public void BalancedCreditNote_HasNoProblems() =>
        InvoiceChecks.Run(Invoice(-60m, -40m, -100m, -21m, -121m)).Should().BeEmpty();

    [Fact]
    public void ZeroVat_HasNoProblems() =>
        InvoiceChecks.Run(Invoice(60m, 40m, 100m, 0m, 100m)).Should().BeEmpty();
}
