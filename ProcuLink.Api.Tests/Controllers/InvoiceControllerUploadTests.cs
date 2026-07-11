using System.Text;
using FluentAssertions;
using Hangfire;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Api.Controllers;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Transform.Output;
using Xunit;

namespace ProcuLink.Api.Tests.Controllers;

/// <summary>
/// Audit FINDING 4 (offer ⇔ works): the invoice-upload whitelist must NOT accept EDIFACT
/// <c>.edi</c> invoices — the EDIFACT INVOIC parser is a NotImplementedException stub, so an
/// accepted <c>.edi</c> upload would parse-fail in the background and land the invoice in
/// "failed" (an error the user never asked for). Per the product rule, a stub is presented as
/// "coming soon" and rejected cleanly UP-FRONT. UBL XML (<c>.xml</c>) invoices still parse and
/// must remain accepted. EDIFACT ORDERS (a working parser) is untouched — this is invoices only.
/// </summary>
public class InvoiceControllerUploadTests
{
    private static InvoiceController Build(
        out Mock<IInvoiceService> invoices,
        out Mock<IBackgroundJobClient> jobs)
    {
        invoices = new Mock<IInvoiceService>();
        jobs = new Mock<IBackgroundJobClient>();
        var tenant = new Mock<ICurrentTenantService>();
        tenant.SetupGet(t => t.OrganisationId).Returns(Guid.NewGuid());

        return new InvoiceController(
            invoices.Object,
            tenant.Object,
            jobs.Object,
            new PeppolBisValidator(),
            new IInvoiceTransformService[] { new PeppolBisInvoiceTransformService() },
            NullLogger<InvoiceController>.Instance);
    }

    private static IFormFile MakeFile(string fileName, string content = "test")
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", fileName)
        {
            Headers = new HeaderDictionary(),
            ContentType = "application/octet-stream",
        };
    }

    [Fact]
    public async Task Upload_EdifactInvoice_IsCleanlyRejected_NotAccepted()
    {
        var ctrl = Build(out var invoices, out var jobs);

        var result = await ctrl.Upload(MakeFile("supplier-invoice.edi"), supplierId: null, CancellationToken.None);

        // Rejected up-front with a clear message — never accepted-then-silently-failed.
        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        bad.Value!.ToString().Should().ContainEquivalentOf("edi");

        // Not accepted: no stub created, no parse job enqueued (which would later NotImplemented-fail).
        invoices.Verify(
            s => s.CreateStubAsync(It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<Stream>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        jobs.Verify(j => j.Create(It.IsAny<Hangfire.Common.Job>(), It.IsAny<Hangfire.States.IState>()), Times.Never);
    }

    [Fact]
    public async Task Upload_UblXmlInvoice_IsStillAccepted()
    {
        var ctrl = Build(out var invoices, out _);
        invoices
            .Setup(s => s.CreateStubAsync(It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<Stream>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InvoiceEntity
            {
                Id = Guid.NewGuid(),
                Status = "parsing",
                SourceFileName = "invoice.xml",
                CreatedAt = DateTime.UtcNow,
            });

        var result = await ctrl.Upload(MakeFile("invoice.xml"), supplierId: null, CancellationToken.None);

        // The whitelist still lets UBL XML invoices through (working parser).
        result.Should().BeOfType<OkObjectResult>();
    }
}
