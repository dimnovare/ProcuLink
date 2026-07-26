using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Ai;
using ProcuLink.Core.Services.Email;
using ProcuLink.Core.Services.Ingress;
using ProcuLink.Infrastructure.Services.Email;

namespace ProcuLink.Infrastructure.Tests.Services.Email;

/// <summary>
/// Verifies that the inbound-email router routes attachments to the right
/// tenant, skips unsupported types, gates on account status, and creates
/// one order stub + parse job per accepted attachment.
/// </summary>
public class InboundEmailRouterTests
{
    private const string Slug = "acme";

    // ── 1. Happy path ────────────────────────────────────────────────────────

    [Fact]
    public async Task HappyPath_SingleCsvAttachment_CreatesOneOrderAndEnqueuesParseJob()
    {
        await using var db = CreateDb();
        var orgId = await SeedOrgAsync(db, AccountStatusConstants.Active);
        var supplierId = await SeedSupplierAsync(db, orgId);

        var orders = new FakeOrderService();
        var enqueuer = new RecordingEnqueuer();
        var router = MakeRouter(db, orders, enqueuer, slug: Slug, orgId: orgId);

        var payload = new InboundEmailPayload(
            FromEmail: "buyer@example.com",
            ToEmail:   $"orders@{Slug}.proculink.eu",
            Subject:   "PO #12345",
            Attachments: new[]
            {
                new InboundAttachment("po.csv", "text/csv", Encoding.UTF8.GetBytes("po,date\r\n001,2026-05-28")),
            });

        var result = await router.RouteAsync(payload, default);

        result.Success.Should().BeTrue();
        result.OrgId.Should().Be(orgId);
        result.CreatedOrderIds.Should().HaveCount(1);

        orders.CalledWith.Should().HaveCount(1);
        orders.CalledWith[0].OrgId.Should().Be(orgId);
        orders.CalledWith[0].SupplierId.Should().Be(supplierId);
        orders.CalledWith[0].FileName.Should().Be("po.csv");

        enqueuer.Calls.Should().HaveCount(1);
        enqueuer.Calls[0].OrderId.Should().Be(result.CreatedOrderIds[0]);
        enqueuer.Calls[0].OrgId.Should().Be(orgId);
    }

    // ── 2. Multiple attachments ──────────────────────────────────────────────

    [Fact]
    public async Task MultipleSupportedAttachments_CreatesMultipleOrders()
    {
        await using var db = CreateDb();
        var orgId = await SeedOrgAsync(db, AccountStatusConstants.Trialing);
        await SeedSupplierAsync(db, orgId);

        var orders = new FakeOrderService();
        var enqueuer = new RecordingEnqueuer();
        var router = MakeRouter(db, orders, enqueuer, slug: Slug, orgId: orgId);

        var payload = new InboundEmailPayload(
            FromEmail: "buyer@example.com",
            ToEmail:   $"orders@{Slug}.proculink.eu",
            Subject:   "Multiple POs",
            Attachments: new[]
            {
                new InboundAttachment("po-a.csv",  "text/csv", new byte[] { 1, 2, 3 }),
                new InboundAttachment("po-b.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", new byte[] { 4, 5, 6 }),
                new InboundAttachment("po-c.pdf",  "application/pdf", new byte[] { 7, 8, 9 }),
            });

        var result = await router.RouteAsync(payload, default);

        result.Success.Should().BeTrue();
        result.CreatedOrderIds.Should().HaveCount(3);
        orders.CalledWith.Should().HaveCount(3);
        enqueuer.Calls.Should().HaveCount(3);

        // Each attachment produced a distinct order id.
        result.CreatedOrderIds.Distinct().Should().HaveCount(3);
    }

    // ── 3. Unsupported attachment ────────────────────────────────────────────

    [Fact]
    public async Task UnsupportedAttachment_IsSkippedReturnsSuccessWithEmptyList()
    {
        await using var db = CreateDb();
        var orgId = await SeedOrgAsync(db, AccountStatusConstants.Active);
        await SeedSupplierAsync(db, orgId);

        var orders = new FakeOrderService();
        var enqueuer = new RecordingEnqueuer();
        var router = MakeRouter(db, orders, enqueuer, slug: Slug, orgId: orgId);

        var payload = new InboundEmailPayload(
            FromEmail: "buyer@example.com",
            ToEmail:   $"orders@{Slug}.proculink.eu",
            Subject:   "Word doc disguised as PO",
            Attachments: new[]
            {
                new InboundAttachment("po.docx",
                    "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                    new byte[] { 1, 2, 3 }),
            });

        var result = await router.RouteAsync(payload, default);

        result.Success.Should().BeTrue("the message itself was valid even if no attachment was usable");
        result.OrgId.Should().Be(orgId);
        result.CreatedOrderIds.Should().BeEmpty();

        orders.CalledWith.Should().BeEmpty("the .docx attachment must not reach CreateStubAsync");
        enqueuer.Calls.Should().BeEmpty();
    }

    // ── 4. Unknown recipient ─────────────────────────────────────────────────

    [Fact]
    public async Task UnknownRecipient_ReturnsFailureWithoutCreatingOrders()
    {
        await using var db = CreateDb();
        // No org seeded — the mapping config also points nowhere useful.

        var orders = new FakeOrderService();
        var enqueuer = new RecordingEnqueuer();
        var router = MakeRouter(db, orders, enqueuer, slug: Slug, orgId: Guid.NewGuid());

        var payload = new InboundEmailPayload(
            FromEmail: "buyer@example.com",
            ToEmail:   "orders@unknown-tenant.proculink.eu",
            Subject:   "Mystery PO",
            Attachments: new[]
            {
                new InboundAttachment("po.csv", "text/csv", new byte[] { 1, 2, 3 }),
            });

        var result = await router.RouteAsync(payload, default);

        result.Success.Should().BeFalse();
        result.OrgId.Should().BeNull();
        result.CreatedOrderIds.Should().BeEmpty();
        result.Error.Should().NotBeNullOrWhiteSpace();

        orders.CalledWith.Should().BeEmpty();
        enqueuer.Calls.Should().BeEmpty();
    }

    // ── 5. Read-only tenant gate ─────────────────────────────────────────────

    [Fact]
    public async Task ReadOnlyTenant_ReturnsFailureAndCreatesNoOrders()
    {
        await using var db = CreateDb();
        var orgId = await SeedOrgAsync(db, AccountStatusConstants.ReadOnly);
        await SeedSupplierAsync(db, orgId);

        var orders = new FakeOrderService();
        var enqueuer = new RecordingEnqueuer();
        var router = MakeRouter(db, orders, enqueuer, slug: Slug, orgId: orgId);

        var payload = new InboundEmailPayload(
            FromEmail: "buyer@example.com",
            ToEmail:   $"orders@{Slug}.proculink.eu",
            Subject:   "PO during read-only",
            Attachments: new[]
            {
                new InboundAttachment("po.csv", "text/csv", new byte[] { 1, 2, 3 }),
            });

        var result = await router.RouteAsync(payload, default);

        result.Success.Should().BeFalse();
        result.OrgId.Should().Be(orgId, "the tenant resolved — we just refused to ingest");
        result.CreatedOrderIds.Should().BeEmpty();
        result.Error.Should().Contain("read_only");

        orders.CalledWith.Should().BeEmpty();
        enqueuer.Calls.Should().BeEmpty();
    }

    // ── 6. Trial-expired tenant gate ─────────────────────────────────────────

    [Fact]
    public async Task TrialExpiredTenant_ReturnsFailureAndCreatesNoOrders()
    {
        await using var db = CreateDb();
        var orgId = await SeedOrgAsync(db, AccountStatusConstants.TrialExpired);
        await SeedSupplierAsync(db, orgId);

        var orders = new FakeOrderService();
        var enqueuer = new RecordingEnqueuer();
        var router = MakeRouter(db, orders, enqueuer, slug: Slug, orgId: orgId);

        var payload = new InboundEmailPayload(
            FromEmail: "buyer@example.com",
            ToEmail:   $"orders@{Slug}.proculink.eu",
            Subject:   "PO after trial",
            Attachments: new[]
            {
                new InboundAttachment("po.csv", "text/csv", new byte[] { 1, 2, 3 }),
            });

        var result = await router.RouteAsync(payload, default);

        result.Success.Should().BeFalse();
        result.OrgId.Should().Be(orgId);
        result.CreatedOrderIds.Should().BeEmpty();
        result.Error.Should().Contain("trial_expired");

        orders.CalledWith.Should().BeEmpty();
        enqueuer.Calls.Should().BeEmpty();
    }

    // ── 7. No attachments at all ─────────────────────────────────────────────

    [Fact]
    public async Task NoAttachments_ReturnsSuccessWithEmptyList()
    {
        await using var db = CreateDb();
        var orgId = await SeedOrgAsync(db, AccountStatusConstants.Active);
        await SeedSupplierAsync(db, orgId);

        var orders = new FakeOrderService();
        var enqueuer = new RecordingEnqueuer();
        var router = MakeRouter(db, orders, enqueuer, slug: Slug, orgId: orgId);

        var payload = new InboundEmailPayload(
            FromEmail: "buyer@example.com",
            ToEmail:   $"orders@{Slug}.proculink.eu",
            Subject:   "Just a note, no file",
            Attachments: Array.Empty<InboundAttachment>());

        var result = await router.RouteAsync(payload, default);

        result.Success.Should().BeTrue();
        result.OrgId.Should().Be(orgId);
        result.CreatedOrderIds.Should().BeEmpty();
        orders.CalledWith.Should().BeEmpty();
        enqueuer.Calls.Should().BeEmpty();
    }

    // ── 8. Mixed supported + unsupported attachments ─────────────────────────

    [Fact]
    public async Task MixedAttachments_OnlySupportedTypesCreateOrders()
    {
        await using var db = CreateDb();
        var orgId = await SeedOrgAsync(db, AccountStatusConstants.Active);
        await SeedSupplierAsync(db, orgId);

        var orders = new FakeOrderService();
        var enqueuer = new RecordingEnqueuer();
        var router = MakeRouter(db, orders, enqueuer, slug: Slug, orgId: orgId);

        var payload = new InboundEmailPayload(
            FromEmail: "buyer@example.com",
            ToEmail:   $"orders@{Slug}.proculink.eu",
            Subject:   "Mixed bag",
            Attachments: new[]
            {
                new InboundAttachment("po.csv",         "text/csv",                   new byte[] { 1, 2, 3 }),
                new InboundAttachment("signature.png",  "image/png",                  new byte[] { 4, 5, 6 }),
                new InboundAttachment("notes.docx",     "application/msword",         new byte[] { 7, 8, 9 }),
                new InboundAttachment("backup.xml",     "application/xml",            new byte[] { 10, 11 }),
            });

        var result = await router.RouteAsync(payload, default);

        result.Success.Should().BeTrue();
        result.CreatedOrderIds.Should().HaveCount(2,
            "only the .csv and .xml attachments are in the supported set");
        orders.CalledWith.Select(c => c.FileName).Should().BeEquivalentTo(new[] { "po.csv", "backup.xml" });
    }

    // ── 8b. Oversized attachment → skipped before CreateStubAsync ────────────

    [Fact]
    public async Task OversizedAttachment_IsSkipped_NeverReachesCreateStub()
    {
        await using var db = CreateDb();
        var orgId = await SeedOrgAsync(db, AccountStatusConstants.Active);
        await SeedSupplierAsync(db, orgId);

        var orders = new FakeOrderService();
        var enqueuer = new RecordingEnqueuer();
        var router = MakeRouter(db, orders, enqueuer, slug: Slug, orgId: orgId);

        var payload = new InboundEmailPayload(
            FromEmail: "buyer@example.com",
            ToEmail:   $"orders@{Slug}.proculink.eu",
            Subject:   "Huge PO",
            Attachments: new[]
            {
                new InboundAttachment("huge.csv", "text/csv", new byte[IngressLimits.MaxFileBytes + 1]),
            });

        var result = await router.RouteAsync(payload, default);

        result.Success.Should().BeTrue("the message was valid even though the attachment was too large");
        result.CreatedOrderIds.Should().BeEmpty();
        orders.CalledWith.Should().BeEmpty("an oversized attachment must never reach CreateStubAsync");
        enqueuer.Calls.Should().BeEmpty();
    }

    // ── 8c. No supplier at all → UNROUTED hold, not a reject ─────────────────
    //    The webhook used to answer 422 "no supplier configured" and drop the mail
    //    (audit inbound_email.rejected_no_supplier). It now mirrors the pull channels
    //    (SftpIngressService / S3IngressService / EmailPollOrgJob): the attachment is
    //    imported via CreateUnroutedStubAsync, the parse job parks it 'unrouted', and
    //    POST /api/orders/{id}/assign-supplier resolves it later.

    [Fact]
    public async Task NoSupplierConfigured_Attachment_ImportedUnrouted_AndParseEnqueued()
    {
        await using var db = CreateDb();
        var orgId = await SeedOrgAsync(db, AccountStatusConstants.Active);
        // Deliberately NO supplier seeded for this org.

        var orders = new FakeOrderService();
        var enqueuer = new RecordingEnqueuer();
        var router = MakeRouter(db, orders, enqueuer, slug: Slug, orgId: orgId);

        var payload = new InboundEmailPayload(
            FromEmail: "buyer@example.com",
            ToEmail:   $"orders@{Slug}.proculink.eu",
            Subject:   "PO #98765",
            Attachments: new[]
            {
                new InboundAttachment("po.csv", "text/csv", Encoding.UTF8.GetBytes("po,qty\r\nUNROUTED-1,5\r\n")),
            });

        var result = await router.RouteAsync(payload, default);

        result.Success.Should().BeTrue(
            "an org with no supplier is not an unprocessable message — the webhook must answer 200 so Postmark stops");
        result.OrgId.Should().Be(orgId);
        result.CreatedOrderIds.Should().HaveCount(1);

        orders.UnroutedCalledWith.Should().HaveCount(1,
            "with no supplier the attachment must go through the unrouted hold path");
        orders.UnroutedCalledWith[0].OrgId.Should().Be(orgId);
        orders.UnroutedCalledWith[0].FileName.Should().Be("po.csv");
        orders.RoutedCalledWith.Should().BeEmpty(
            "an order must never be routed to a supplier that does not exist");

        enqueuer.Calls.Should().HaveCount(1,
            "the unrouted order still needs a parse job — the parse parks it 'unrouted'");
        enqueuer.Calls[0].OrderId.Should().Be(result.CreatedOrderIds[0]);
    }

    [Fact]
    public async Task NoSupplierConfigured_WritesUnroutedAudit_NotRejectedAudit()
    {
        await using var db = CreateDb();
        var orgId = await SeedOrgAsync(db, AccountStatusConstants.Active);

        var orders = new FakeOrderService();
        var router = MakeRouter(db, orders, new RecordingEnqueuer(), slug: Slug, orgId: orgId);

        var payload = new InboundEmailPayload(
            FromEmail: "buyer@example.com",
            ToEmail:   $"orders@{Slug}.proculink.eu",
            Subject:   "PO #98765",
            Attachments: new[]
            {
                new InboundAttachment("po.csv", "text/csv", Encoding.UTF8.GetBytes("po,qty\r\nA,1\r\n")),
            });

        await router.RouteAsync(payload, default);

        var actions = await db.AuditEvents.AsNoTracking()
            .Where(a => a.OrgId == orgId)
            .Select(a => a.Action)
            .ToListAsync();

        actions.Should().Contain("inbound_email.unrouted_no_supplier",
            "operators need an audit trail explaining why the order arrived without a supplier");
        actions.Should().NotContain("inbound_email.rejected_no_supplier",
            "the reject is gone — the message is held, not dropped");
        actions.Should().Contain("inbound_email.processed");
    }

    [Fact]
    public async Task OnlySoftDeletedSupplier_ImportsUnrouted_NeverRoutesToDeletedSupplier()
    {
        await using var db = CreateDb();
        var orgId = await SeedOrgAsync(db, AccountStatusConstants.Active);
        db.Suppliers.Add(new Supplier
        {
            Id = Guid.NewGuid(),
            OrgId = orgId,
            Name = "Retired supplier",
            CreatedAt = DateTime.UtcNow.AddDays(-30),
            DeletedAt = DateTime.UtcNow.AddMinutes(-5),
        });
        await db.SaveChangesAsync();

        var orders = new FakeOrderService();
        var enqueuer = new RecordingEnqueuer();
        var router = MakeRouter(db, orders, enqueuer, slug: Slug, orgId: orgId);

        var payload = new InboundEmailPayload(
            FromEmail: "buyer@example.com",
            ToEmail:   $"orders@{Slug}.proculink.eu",
            Subject:   "PO after supplier removal",
            Attachments: new[]
            {
                new InboundAttachment("po.csv", "text/csv", Encoding.UTF8.GetBytes("po,qty\r\nB,2\r\n")),
            });

        var result = await router.RouteAsync(payload, default);

        result.Success.Should().BeTrue();
        orders.UnroutedCalledWith.Should().HaveCount(1,
            "a soft-deleted supplier degrades to the unrouted hold instead of dropping the mail");
        orders.RoutedCalledWith.Should().BeEmpty();
        enqueuer.Calls.Should().HaveCount(1);
    }

    [Fact]
    public async Task NoSupplierConfigured_BodyOnlyEmail_CreatesUnroutedOrder()
    {
        // The gap this used to pin is closed. The body-NLP fallback no longer needs a
        // supplier to persist: with none configured it takes the unrouted sibling
        // (CreateUnroutedStubFromParsedOrderAsync), exactly as the attachment path takes
        // CreateUnroutedStubAsync. A prose-only email to a supplier-less org now lands as
        // a real order parked 'unrouted' for the assign-supplier flow to resolve, instead
        // of being accepted, audited and silently dropped.
        await using var db = CreateDb();
        var orgId = await SeedOrgAsync(db, AccountStatusConstants.Active);

        var orders = new FakeOrderService();
        var enqueuer = new RecordingEnqueuer();
        var extractor = new FakeBodyExtractor(new EmailBodyExtractionResult(
            Success: true,
            Confidence: 0.9,
            Order: new ExtractedOrder(
                PoNumber: "PO-BODY-NOSUP",
                OrderDate: new DateTime(2026, 7, 24),
                BuyerName: "Acme Buyer",
                Currency: "EUR",
                Lines: new[] { new ExtractedOrderLine(1, "WIDGET-A", "Widget A", 1m, "pcs", 1m) }),
            FailureReason: null));

        var router = MakeRouter(db, orders, enqueuer, slug: Slug, orgId: orgId, extractor: extractor);

        var payload = new InboundEmailPayload(
            FromEmail: "buyer@example.com",
            ToEmail:   $"orders@{Slug}.proculink.eu",
            Subject:   "Order request (no attachment)",
            Attachments: Array.Empty<InboundAttachment>(),
            Body: "Please send 1 WIDGET-A.");

        var result = await router.RouteAsync(payload, default);

        result.Success.Should().BeTrue("the message is accepted — Postmark must not retry it");
        result.CreatedOrderIds.Should().HaveCount(1,
            "a prose-only order to a supplier-less org is held for routing, not dropped");
        extractor.Calls.Should().Be(1, "the extraction is now persistable, so it is worth running");

        // The ROUTED persist path must not have been used — it would have needed a
        // supplier id, and there is none to invent.
        orders.ParsedOrderCalls.Should().BeEmpty();
        orders.UnroutedParsedOrderCalls.Should().HaveCount(1);
        orders.UnroutedParsedOrderCalls[0].OrgId.Should().Be(orgId);
        orders.UnroutedParsedOrderCalls[0].Source.Should().Be("email_body_nlp");
        orders.UnroutedParsedOrderCalls[0].Order.PoNumber.Should().Be("PO-BODY-NOSUP");

        // No parse job — the order is already populated; there is no source file to parse.
        enqueuer.Calls.Should().BeEmpty();

        var actions = await db.AuditEvents.Select(a => a.Action).ToListAsync();
        actions.Should().Contain("inbound_email.processed");
    }

    [Fact]
    public async Task ReadOnlyTenant_WithNoSupplier_StillReturnsFailure()
    {
        // The unrouted hold must not weaken the account-status gate: a read-only org
        // is still an unprocessable message, supplier or no supplier.
        await using var db = CreateDb();
        var orgId = await SeedOrgAsync(db, AccountStatusConstants.ReadOnly);

        var orders = new FakeOrderService();
        var enqueuer = new RecordingEnqueuer();
        var router = MakeRouter(db, orders, enqueuer, slug: Slug, orgId: orgId);

        var payload = new InboundEmailPayload(
            FromEmail: "buyer@example.com",
            ToEmail:   $"orders@{Slug}.proculink.eu",
            Subject:   "PO for a frozen account",
            Attachments: new[]
            {
                new InboundAttachment("po.csv", "text/csv", Encoding.UTF8.GetBytes("po,qty\r\nC,3\r\n")),
            });

        var result = await router.RouteAsync(payload, default);

        result.Success.Should().BeFalse();
        orders.UnroutedCalledWith.Should().BeEmpty();
        orders.RoutedCalledWith.Should().BeEmpty();
        enqueuer.Calls.Should().BeEmpty();
    }

    // ── 9. Email-body NLP fallback ───────────────────────────────────────────

    [Fact]
    public async Task BodyExtractionPath_NoAttachments_CreatesStubFromExtractedOrder()
    {
        await using var db = CreateDb();
        var orgId = await SeedOrgAsync(db, AccountStatusConstants.Active);
        await SeedSupplierAsync(db, orgId);

        var orders = new FakeOrderService();
        var enqueuer = new RecordingEnqueuer();

        // Extractor returns a successful extraction — the router must call
        // CreateStubFromParsedOrderAsync (NOT CreateStubAsync) and report the
        // resulting order id back to the caller.
        var extractedOrder = new ExtractedOrder(
            PoNumber:  "PO-FROM-BODY-001",
            OrderDate: new DateTime(2026, 5, 28),
            BuyerName: "Acme Buyer",
            Currency:  "EUR",
            Lines: new[]
            {
                new ExtractedOrderLine(1, "WIDGET-A", "Widget A blue", 10m, "pcs", 2.50m),
                new ExtractedOrderLine(2, "WIDGET-B", "Widget B red",   5m, "pcs", 3.00m),
            });
        var extractor = new FakeBodyExtractor(
            new EmailBodyExtractionResult(Success: true, Confidence: 0.85, Order: extractedOrder, FailureReason: null));

        var router = MakeRouter(db, orders, enqueuer, slug: Slug, orgId: orgId, extractor: extractor);

        var payload = new InboundEmailPayload(
            FromEmail: "buyer@example.com",
            ToEmail:   $"orders@{Slug}.proculink.eu",
            Subject:   "Order request (no attachment)",
            Attachments: Array.Empty<InboundAttachment>(),
            Body: "Hi team, please send 10 of WIDGET-A at 2.50 EUR and 5 of WIDGET-B at 3.00 EUR. Thanks!");

        var result = await router.RouteAsync(payload, default);

        result.Success.Should().BeTrue();
        result.OrgId.Should().Be(orgId);
        result.CreatedOrderIds.Should().HaveCount(1,
            "the extractor returned a usable order from the email body");

        // The body path uses the new CreateStubFromParsedOrderAsync — the
        // attachment path (CreateStubAsync) must not have been invoked.
        orders.CalledWith.Should().BeEmpty();
        orders.ParsedOrderCalls.Should().HaveCount(1);
        orders.ParsedOrderCalls[0].OrgId.Should().Be(orgId);
        orders.ParsedOrderCalls[0].Source.Should().Be("email_body_nlp");
        orders.ParsedOrderCalls[0].Order.PoNumber.Should().Be("PO-FROM-BODY-001");
        orders.ParsedOrderCalls[0].Order.Lines.Should().HaveCount(2);

        // No parse job — the order is already populated, there is nothing to parse.
        enqueuer.Calls.Should().BeEmpty();

        extractor.Calls.Should().Be(1);
    }

    // ── 10. Local-part addressing — {slug}@{InboundDomain} ───────────────────

    [Fact]
    public async Task LocalPartAddressing_RoutesToOrg_WhenInboundDomainConfigured()
    {
        await using var db = CreateDb();
        var orgId = await SeedOrgAsync(db, AccountStatusConstants.Active);
        var supplierId = await SeedSupplierAsync(db, orgId);

        var orders = new FakeOrderService();
        var enqueuer = new RecordingEnqueuer();
        var router = MakeRouter(db, orders, enqueuer, slug: Slug, orgId: orgId,
            inboundDomain: "orders.proculink.eu");

        // Slug is the LOCAL part; host is the single fixed inbound domain.
        var payload = new InboundEmailPayload(
            FromEmail: "buyer@example.com",
            ToEmail:   $"{Slug}@orders.proculink.eu",
            Subject:   "PO via local-part address",
            Attachments: new[]
            {
                new InboundAttachment("po.csv", "text/csv", new byte[] { 1, 2, 3 }),
            });

        var result = await router.RouteAsync(payload, default);

        result.Success.Should().BeTrue();
        result.OrgId.Should().Be(orgId);
        result.CreatedOrderIds.Should().HaveCount(1);
        orders.CalledWith.Should().HaveCount(1);
        orders.CalledWith[0].SupplierId.Should().Be(supplierId);
    }

    // ── 10b. Plus-addressing tag is stripped from the local-part slug ────────

    [Fact]
    public async Task LocalPartAddressing_StripsPlusTag()
    {
        await using var db = CreateDb();
        var orgId = await SeedOrgAsync(db, AccountStatusConstants.Active);
        await SeedSupplierAsync(db, orgId);

        var orders = new FakeOrderService();
        var enqueuer = new RecordingEnqueuer();
        var router = MakeRouter(db, orders, enqueuer, slug: Slug, orgId: orgId,
            inboundDomain: "orders.proculink.eu");

        // "acme+urgent@orders.proculink.eu" must still resolve to org "acme".
        var payload = new InboundEmailPayload(
            FromEmail: "buyer@example.com",
            ToEmail:   $"{Slug}+urgent@orders.proculink.eu",
            Subject:   "PO with plus-addressing",
            Attachments: new[]
            {
                new InboundAttachment("po.csv", "text/csv", new byte[] { 1, 2, 3 }),
            });

        var result = await router.RouteAsync(payload, default);

        result.Success.Should().BeTrue();
        result.OrgId.Should().Be(orgId);
        result.CreatedOrderIds.Should().HaveCount(1);
    }

    // ── 10c. Subdomain scheme still works even when InboundDomain is set ─────

    [Fact]
    public async Task SubdomainAddressing_StillWorks_WhenInboundDomainConfigured()
    {
        await using var db = CreateDb();
        var orgId = await SeedOrgAsync(db, AccountStatusConstants.Active);
        await SeedSupplierAsync(db, orgId);

        var orders = new FakeOrderService();
        var enqueuer = new RecordingEnqueuer();
        // InboundDomain configured, but the recipient uses the legacy subdomain
        // scheme — it must still resolve (back-compat).
        var router = MakeRouter(db, orders, enqueuer, slug: Slug, orgId: orgId,
            inboundDomain: "orders.proculink.eu");

        var payload = new InboundEmailPayload(
            FromEmail: "buyer@example.com",
            ToEmail:   $"orders@{Slug}.proculink.eu",
            Subject:   "PO via legacy subdomain address",
            Attachments: new[]
            {
                new InboundAttachment("po.csv", "text/csv", new byte[] { 1, 2, 3 }),
            });

        var result = await router.RouteAsync(payload, default);

        result.Success.Should().BeTrue();
        result.OrgId.Should().Be(orgId);
        result.CreatedOrderIds.Should().HaveCount(1);
    }

    // ── 10d. Local-part rejection cases ──────────────────────────────────────

    [Theory]
    [InlineData("+@orders.proculink.eu")]        // empty slug after stripping the +tag
    [InlineData("acme.corp@orders.proculink.eu")] // dots are not valid in a kebab-case slug
    public async Task LocalPartAddressing_InvalidSlug_IsRejected(string toEmail)
    {
        await using var db = CreateDb();
        var orgId = await SeedOrgAsync(db, AccountStatusConstants.Active);
        await SeedSupplierAsync(db, orgId);

        var orders = new FakeOrderService();
        var enqueuer = new RecordingEnqueuer();
        var router = MakeRouter(db, orders, enqueuer, slug: Slug, orgId: orgId,
            inboundDomain: "orders.proculink.eu");

        var payload = new InboundEmailPayload(
            FromEmail: "buyer@example.com",
            ToEmail:   toEmail,
            Subject:   "PO with an unroutable local part",
            Attachments: new[]
            {
                new InboundAttachment("po.csv", "text/csv", new byte[] { 1, 2, 3 }),
            });

        var result = await router.RouteAsync(payload, default);

        result.Success.Should().BeFalse("the local part does not resolve to a valid tenant slug");
        result.CreatedOrderIds.Should().BeEmpty();
        orders.CalledWith.Should().BeEmpty();
        enqueuer.Calls.Should().BeEmpty();
    }

    // ── 11. GDPR: the processed audit row is keyed to the created order ──────
    // Finding #8: the inbound-email audit must be erasable via the per-order erase
    // path, which finds audit rows by EntityId == orderId. A single-attachment email
    // creates exactly one order, so the "processed" audit MUST carry EntityId == that
    // order id (previously it was Guid.Empty and survived erasure forever).
    [Fact]
    public async Task ProcessedAudit_IsKeyedToCreatedOrder_SoErasureRemovesIt()
    {
        await using var db = CreateDb();
        var orgId = await SeedOrgAsync(db, AccountStatusConstants.Active);
        await SeedSupplierAsync(db, orgId);

        var orders = new FakeOrderService();
        var enqueuer = new RecordingEnqueuer();
        var router = MakeRouter(db, orders, enqueuer, slug: Slug, orgId: orgId);

        var payload = new InboundEmailPayload(
            FromEmail: "buyer@example.com",
            ToEmail:   $"orders@{Slug}.proculink.eu",
            Subject:   "PO #12345 — confidential pricing",
            Attachments: new[]
            {
                new InboundAttachment("po.csv", "text/csv", new byte[] { 1, 2, 3 }),
            });

        var result = await router.RouteAsync(payload, default);

        result.CreatedOrderIds.Should().HaveCount(1);
        var orderId = result.CreatedOrderIds[0];

        var processed = await db.AuditEvents
            .SingleAsync(e => e.Action == "inbound_email.processed");
        processed.EntityId.Should().Be(orderId,
            "the processed audit must be keyed to the order so the erase path removes it");
        processed.OrgId.Should().Be(orgId);
    }

    // ── 11b. GDPR: the audit payload omits raw sender email + subject ────────
    // Finding #8: the audit payload previously stored the raw sender address and
    // subject line (third-party PII) verbatim. The summary must now hash the sender
    // and drop the subject entirely.
    [Fact]
    public void BuildAuditSummary_HashesSender_AndOmitsRawSenderAndSubject()
    {
        const string sender = "buyer@example.com";
        const string subject = "PO #12345 — confidential pricing";
        var payload = new InboundEmailPayload(
            FromEmail: sender,
            ToEmail:   $"orders@{Slug}.proculink.eu",
            Subject:   subject,
            Attachments: Array.Empty<InboundAttachment>());

        var json = System.Text.Json.JsonSerializer.Serialize(
            InboundEmailRouter.BuildAuditSummary(payload, extra: null));

        json.Should().NotContain(sender, "the raw sender address is third-party PII and must not be persisted");
        json.Should().NotContain(subject, "the raw subject line is PII and must not be persisted");
        json.Should().Contain(InboundEmailRouter.Sha256Hex(sender),
            "the sender is stored only as a one-way hash for correlation/diagnostics");
    }

    // ── 12. Log levels — routine chatter at Debug, rejects at Warning ────────
    // The webhook fires on every message an org receives (including the ones that
    // carry nothing but a signature image). Production runs at Default=Information,
    // so anything routine must sit at Debug to stay out of the log; anything an
    // operator has to act on must sit at Warning to stay visible.

    [Fact]
    public async Task NoAttachmentsNotice_IsLoggedAtDebug()
    {
        var log = await RunAndCaptureLogAsync(
            AccountStatusConstants.Active,
            Array.Empty<InboundAttachment>());

        log.LevelOf("carried no attachments").Should().Be(LogLevel.Debug,
            "an attachment-less email is routine — the body-NLP fallback note is diagnostics, not news");
    }

    [Fact]
    public async Task UnsupportedAttachmentSkip_IsLoggedAtDebug()
    {
        var log = await RunAndCaptureLogAsync(
            AccountStatusConstants.Active,
            new[] { new InboundAttachment("signature.png", "image/png", new byte[] { 1, 2, 3 }) });

        log.LevelOf("skipped: unsupported type").Should().Be(LogLevel.Debug,
            "email signatures and logos ride along on nearly every message — skipping them is expected");
    }

    [Fact]
    public async Task BodyExtractionYieldingNoOrder_IsLoggedAtDebug()
    {
        var log = await RunAndCaptureLogAsync(
            AccountStatusConstants.Active,
            Array.Empty<InboundAttachment>(),
            body: "Hi, just checking in on last week's order. Thanks!");

        log.LevelOf("did not yield an order").Should().Be(LogLevel.Debug,
            "most prose emails are not orders, and the extractor is a no-op without an AI key");
    }

    [Fact]
    public async Task BlockedAccountStatus_IsLoggedAtWarning()
    {
        var log = await RunAndCaptureLogAsync(
            AccountStatusConstants.ReadOnly,
            new[] { new InboundAttachment("po.csv", "text/csv", new byte[] { 1, 2, 3 }) });

        log.LevelOf("blocks ingest").Should().Be(LogLevel.Warning,
            "the message is rejected and the sender is never told — the operator must see it");
    }

    [Fact]
    public async Task CreatedOrder_StaysAtInformation()
    {
        var log = await RunAndCaptureLogAsync(
            AccountStatusConstants.Active,
            new[] { new InboundAttachment("po.csv", "text/csv", new byte[] { 1, 2, 3 }) });

        log.LevelOf("created routed order").Should().Be(LogLevel.Information,
            "one line per order actually created is the operational trail, not chatter");
    }

    // ── 13. Retry contract — what the mail provider is told to do next ───────
    // Postmark retries every non-200 response ten times over ~10.5 hours and only
    // then files the message under Failed (where a human can still re-fire it).
    // So each reject branch has to answer one question: could re-sending this exact
    // message ever work? Sender-side faults say no; our own outages say yes.

    [Fact]
    public async Task UnparseableRecipient_IsPermanent_BecauseNoRetryCanChangeTheAddress()
    {
        await using var db = CreateDb();
        var orders = new FakeOrderService();
        var router = MakeRouter(db, orders, new RecordingEnqueuer(), slug: Slug, orgId: Guid.NewGuid());

        var payload = new InboundEmailPayload(
            FromEmail: "buyer@example.com",
            ToEmail:   "redacted@example.invalid",
            Subject:   "Not for us",
            Attachments: Array.Empty<InboundAttachment>());

        var result = await router.RouteAsync(payload, default);

        result.Success.Should().BeFalse();
        result.RejectionKind.Should().Be(InboundEmailRejectionKind.Permanent,
            "the address is not a ProcuLink inbound address — the tenth delivery reads exactly like the first");
    }

    [Fact]
    public async Task UnknownTenantSlug_IsPermanent_SoStrayMailIsNotDeliveredTenTimes()
    {
        await using var db = CreateDb();
        var orders = new FakeOrderService();
        var router = MakeRouter(db, orders, new RecordingEnqueuer(), slug: Slug, orgId: Guid.NewGuid());

        var payload = new InboundEmailPayload(
            FromEmail: "buyer@example.com",
            ToEmail:   "orders@unknown-tenant.proculink.eu",
            Subject:   "Mystery PO",
            Attachments: Array.Empty<InboundAttachment>());

        var result = await router.RouteAsync(payload, default);

        result.Success.Should().BeFalse();
        result.OrgId.Should().BeNull();
        result.RejectionKind.Should().Be(InboundEmailRejectionKind.Permanent,
            "anything can be mailed to a made-up slug; retrying it ten times only multiplies the noise");
    }

    [Fact]
    public async Task MissingOrganisation_IsTransient_SoFixingTheMappingStillLandsTheOrder()
    {
        // The slug resolved through Inbound:Postmark:TenantMapping but the org row is
        // gone — that is our own misconfiguration, not a bad address, and the operator
        // can repair it inside the retry window.
        await using var db = CreateDb();
        var orders = new FakeOrderService();
        var router = MakeRouter(db, orders, new RecordingEnqueuer(), slug: Slug, orgId: Guid.NewGuid());

        var payload = new InboundEmailPayload(
            FromEmail: "buyer@example.com",
            ToEmail:   $"orders@{Slug}.proculink.eu",
            Subject:   "PO for a vanished org",
            Attachments: Array.Empty<InboundAttachment>());

        var result = await router.RouteAsync(payload, default);

        result.Success.Should().BeFalse();
        result.OrgId.Should().NotBeNull("the slug did resolve — the organisation behind it is what is missing");
        result.RejectionKind.Should().Be(InboundEmailRejectionKind.Transient);
    }

    [Fact]
    public async Task ReadOnlyTenant_IsTransient_SoLiftingTheBlockLandsTheOrder()
    {
        await using var db = CreateDb();
        var orgId = await SeedOrgAsync(db, AccountStatusConstants.ReadOnly);
        await SeedSupplierAsync(db, orgId);

        var orders = new FakeOrderService();
        var router = MakeRouter(db, orders, new RecordingEnqueuer(), slug: Slug, orgId: orgId);

        var payload = new InboundEmailPayload(
            FromEmail: "buyer@example.com",
            ToEmail:   $"orders@{Slug}.proculink.eu",
            Subject:   "PO during read-only",
            Attachments: new[]
            {
                new InboundAttachment("po.csv", "text/csv", new byte[] { 1, 2, 3 }),
            });

        var result = await router.RouteAsync(payload, default);

        result.Success.Should().BeFalse();
        result.RejectionKind.Should().Be(InboundEmailRejectionKind.Transient,
            "a frozen account is a billing state a human clears in minutes — the retries are the grace window");
    }

    [Fact]
    public async Task TrialExpiredTenant_IsTransient_ForTheSameReasonAsReadOnly()
    {
        await using var db = CreateDb();
        var orgId = await SeedOrgAsync(db, AccountStatusConstants.TrialExpired);
        await SeedSupplierAsync(db, orgId);

        var orders = new FakeOrderService();
        var router = MakeRouter(db, orders, new RecordingEnqueuer(), slug: Slug, orgId: orgId);

        var payload = new InboundEmailPayload(
            FromEmail: "buyer@example.com",
            ToEmail:   $"orders@{Slug}.proculink.eu",
            Subject:   "PO after the trial ran out",
            Attachments: new[]
            {
                new InboundAttachment("po.csv", "text/csv", new byte[] { 1, 2, 3 }),
            });

        var result = await router.RouteAsync(payload, default);

        result.Success.Should().BeFalse();
        result.RejectionKind.Should().Be(InboundEmailRejectionKind.Transient);
    }

    [Fact]
    public async Task BlockedAccountStatus_WritesItsAuditRow_SoTheRefusalIsNeverInvisible()
    {
        // The audit row is the only product-side evidence that a message arrived and
        // was refused — the sender is never told, and no order exists to look at.
        await using var db = CreateDb();
        var orgId = await SeedOrgAsync(db, AccountStatusConstants.ReadOnly);
        await SeedSupplierAsync(db, orgId);

        var router = MakeRouter(db, new FakeOrderService(), new RecordingEnqueuer(), slug: Slug, orgId: orgId);

        await router.RouteAsync(new InboundEmailPayload(
            FromEmail: "buyer@example.com",
            ToEmail:   $"orders@{Slug}.proculink.eu",
            Subject:   "PO for a frozen account",
            Attachments: new[]
            {
                new InboundAttachment("po.csv", "text/csv", new byte[] { 1, 2, 3 }),
            }), default);

        var actions = await db.AuditEvents.AsNoTracking()
            .Where(a => a.OrgId == orgId)
            .Select(a => a.Action)
            .ToListAsync();

        actions.Should().Contain("inbound_email.rejected_read_only");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Routes one payload through a router wired to a <see cref="RecordingLogger"/>
    /// and returns the captured log so a test can assert the level of a single line.
    /// </summary>
    private static async Task<RecordingLogger> RunAndCaptureLogAsync(
        string accountStatus,
        IReadOnlyList<InboundAttachment> attachments,
        string? body = null)
    {
        await using var db = CreateDb();
        var orgId = await SeedOrgAsync(db, accountStatus);
        await SeedSupplierAsync(db, orgId);

        var logger = new RecordingLogger();
        var router = MakeRouter(db, new FakeOrderService(), new RecordingEnqueuer(),
            slug: Slug, orgId: orgId, logger: logger);

        await router.RouteAsync(new InboundEmailPayload(
            FromEmail: "buyer@example.com",
            ToEmail:   $"orders@{Slug}.proculink.eu",
            Subject:   "PO #12345",
            Attachments: attachments,
            Body: body), default);

        return logger;
    }

    private static InboundEmailRouter MakeRouter(
        ProcuLinkDbContext db,
        IOrderService orders,
        IParseJobEnqueuer enqueuer,
        string slug,
        Guid orgId,
        IEmailBodyOrderExtractor? extractor = null,
        string? inboundDomain = null,
        ILogger<InboundEmailRouter>? logger = null)
    {
        var settings = new Dictionary<string, string?>
        {
            [$"Inbound:Postmark:TenantMapping:{slug}"] = orgId.ToString(),
        };
        if (inboundDomain is not null)
            settings["Inbound:Postmark:InboundDomain"] = inboundDomain;

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

        return new InboundEmailRouter(
            db, orders, enqueuer,
            extractor ?? FakeBodyExtractor.NoOp,
            config,
            logger ?? NullLogger<InboundEmailRouter>.Instance);
    }

    private static async Task<Guid> SeedOrgAsync(ProcuLinkDbContext db, string accountStatus)
    {
        var orgId = Guid.NewGuid();
        db.Organisations.Add(new Organisation
        {
            Id = orgId,
            ClerkOrgId = $"org_{orgId:N}",
            Name = "Acme Distribution",
            AccountStatus = accountStatus,
            CreatedAt = DateTime.UtcNow,
            EmailConfigJson = "{}",
        });
        await db.SaveChangesAsync();
        return orgId;
    }

    private static async Task<Guid> SeedSupplierAsync(ProcuLinkDbContext db, Guid orgId)
    {
        var supplierId = Guid.NewGuid();
        db.Suppliers.Add(new Supplier
        {
            Id = supplierId,
            OrgId = orgId,
            Name = "Acme Components",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return supplierId;
    }

    private static ProcuLinkDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<ProcuLinkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new InboundEmailTestDbContext(options);
    }

    // ── Doubles ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Records the args passed to <see cref="IOrderService.CreateStubAsync"/>
    /// and returns a successful stub. Other methods throw — the router must
    /// not touch them.
    /// </summary>
    // ── Sender-domain capture (founder ruling D2) ────────────────────────────

    [Theory]
    [InlineData("redacted@example.invalid", "acme.com")]
    [InlineData("redacted@example.invalid", "acme.com")]
    [InlineData("\"Acme Orders\" <redacted@example.invalid>", "example.invalid")]
    [InlineData("redacted@example.invalid", "acme.com")]
    public void ExtractSenderDomain_keepsOnlyTheDomain(string from, string expected)
    {
        // The local part is the half that identifies a PERSON. It must not survive this call —
        // the full address keeps its existing SHA-256-only treatment and nothing else.
        Assert.Equal(expected, InboundEmailRouter.ExtractSenderDomain(from));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-address")]
    [InlineData("someone@localhost")]   // no dot: a host name, never a shared match key
    [InlineData("someone@")]
    public void ExtractSenderDomain_returnsNull_forAnythingThatIsNotClearlyADomain(string? from)
    {
        Assert.Null(InboundEmailRouter.ExtractSenderDomain(from));
    }

    [Fact]
    public async Task RoutedAttachment_passesTheSenderDomainToOrderCreation()
    {
        // Captured on ROUTED orders too, and that is the point: an order whose supplier is already
        // known is exactly what teaches the domain→supplier history the next UNROUTED one is
        // scored against. Capture only the unrouted ones and the signal never accumulates.
        await using var db = CreateDb();
        var orgId = await SeedOrgAsync(db, AccountStatusConstants.Active);
        await SeedSupplierAsync(db, orgId);

        var orders = new FakeOrderService();
        var router = MakeRouter(db, orders, new RecordingEnqueuer(), slug: Slug, orgId: orgId);

        var payload = new InboundEmailPayload(
            FromEmail: "redacted@example.invalid",
            ToEmail:   $"orders@{Slug}.proculink.eu",
            Subject:   "PO #12345",
            Attachments: new[]
            {
                new InboundAttachment("po.csv", "text/csv", Encoding.UTF8.GetBytes("po,date\r\n001,2026-05-28")),
            });

        var result = await router.RouteAsync(payload, default);

        result.Success.Should().BeTrue();
        orders.SenderDomains.Should().ContainSingle().Which.Should().Be("acme.com");
    }

    [Fact]
    public async Task Attachment_fromAnUnparseableSenderAddress_passesNoDomain()
    {
        await using var db = CreateDb();
        var orgId = await SeedOrgAsync(db, AccountStatusConstants.Active);
        await SeedSupplierAsync(db, orgId);

        var orders = new FakeOrderService();
        var router = MakeRouter(db, orders, new RecordingEnqueuer(), slug: Slug, orgId: orgId);

        var payload = new InboundEmailPayload(
            FromEmail: "not-an-address",
            ToEmail:   $"orders@{Slug}.proculink.eu",
            Subject:   "PO #12345",
            Attachments: new[]
            {
                new InboundAttachment("po.csv", "text/csv", Encoding.UTF8.GetBytes("po,date\r\n001,2026-05-28")),
            });

        await router.RouteAsync(payload, default);

        orders.SenderDomains.Should().ContainSingle().Which.Should().BeNull();
    }

    private sealed class FakeOrderService : IOrderService
    {
        /// <summary>Every stub creation, routed and unrouted alike (unrouted records Guid.Empty).</summary>
        public List<(Guid OrgId, Guid SupplierId, string FileName, string ContentType, long Size)> CalledWith { get; } = new();
        /// <summary>Only the routed calls — <c>CreateStubAsync</c> with a real supplier.</summary>
        public List<(Guid OrgId, Guid SupplierId, string FileName, string ContentType, long Size)> RoutedCalledWith { get; } = new();
        /// <summary>Only the unrouted-hold calls — <c>CreateUnroutedStubAsync</c>, no supplier.</summary>
        public List<(Guid OrgId, string FileName, string ContentType, long Size)> UnroutedCalledWith { get; } = new();
        public List<(Guid OrgId, Guid SupplierId, ExtractedOrder Order, string Source)> ParsedOrderCalls { get; } = new();
        public List<(Guid OrgId, ExtractedOrder Order, string Source)> UnroutedParsedOrderCalls { get; } = new();
        /// <summary>Sender domain handed to EVERY creation path, in call order. Null entries are real.</summary>
        public List<string?> SenderDomains { get; } = new();

        public Task<Result<PurchaseOrderEntity>> CreateStubAsync(
            Guid organisationId, Guid supplierId, Stream fileStream,
            string filename, string contentType, CancellationToken ct, string? inboundSenderDomain = null)
        {
            SenderDomains.Add(inboundSenderDomain);
            var stub = Record(organisationId, supplierId, fileStream, filename, contentType, out var size);
            RoutedCalledWith.Add((organisationId, supplierId, filename, contentType, size));
            return Task.FromResult(Result<PurchaseOrderEntity>.Ok(stub));
        }

        // Unrouted hold path records a Guid.Empty supplier in CalledWith and its own
        // UnroutedCalledWith entry, so a test can assert WHICH creation path ran.
        public Task<Result<PurchaseOrderEntity>> CreateUnroutedStubAsync(
            Guid organisationId, Stream fileStream, string filename, string contentType, CancellationToken ct,
            string? inboundSenderDomain = null)
        {
            SenderDomains.Add(inboundSenderDomain);
            var stub = Record(organisationId, Guid.Empty, fileStream, filename, contentType, out var size);
            stub.SupplierId = null;
            UnroutedCalledWith.Add((organisationId, filename, contentType, size));
            return Task.FromResult(Result<PurchaseOrderEntity>.Ok(stub));
        }

        private PurchaseOrderEntity Record(
            Guid organisationId, Guid supplierId, Stream fileStream,
            string filename, string contentType, out long size)
        {
            // Drain the stream so we record the actual byte count the router sent.
            using var ms = new MemoryStream();
            fileStream.CopyTo(ms);
            size = ms.Length;
            CalledWith.Add((organisationId, supplierId, filename, contentType, size));

            return new PurchaseOrderEntity
            {
                Id = Guid.NewGuid(),
                OrgId = organisationId,
                SupplierId = supplierId,
                Status = "parsing",
                SourceFileKey = $"{organisationId}/{Guid.NewGuid()}/{filename}",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
        }

        public Task<Result<PurchaseOrderEntity>> CreateStubFromParsedOrderAsync(
            Guid organisationId, Guid supplierId, ExtractedOrder order, string source, CancellationToken ct,
            string? inboundSenderDomain = null)
        {
            SenderDomains.Add(inboundSenderDomain);
            ParsedOrderCalls.Add((organisationId, supplierId, order, source));
            return Task.FromResult(Result<PurchaseOrderEntity>.Ok(
                ParsedStub(organisationId, supplierId, "pending_review")));
        }

        // Supplier-less sibling of the above — the body-NLP path takes this when the org has
        // no resolvable supplier. Recorded separately so a test can assert WHICH path ran.
        public Task<Result<PurchaseOrderEntity>> CreateUnroutedStubFromParsedOrderAsync(
            Guid organisationId, ExtractedOrder order, string source, CancellationToken ct,
            string? inboundSenderDomain = null)
        {
            SenderDomains.Add(inboundSenderDomain);
            UnroutedParsedOrderCalls.Add((organisationId, order, source));
            return Task.FromResult(Result<PurchaseOrderEntity>.Ok(
                ParsedStub(organisationId, supplierId: null, "unrouted")));
        }

        private static PurchaseOrderEntity ParsedStub(Guid organisationId, Guid? supplierId, string status) => new()
        {
            Id = Guid.NewGuid(),
            OrgId = organisationId,
            SupplierId = supplierId,
            Status = status,
            SourceFileKey = null,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        public Task<Result<PurchaseOrderEntity>> CreateFromFileAsync(Guid organisationId, Guid supplierId, Stream fileStream, string filename, string contentType, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Result<ParsedFileOutput>> ParseStoredFileAsync(Guid organisationId, Guid orderId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Result<PurchaseOrderEntity>> GetByIdAsync(Guid organisationId, Guid orderId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Result<IReadOnlyList<PurchaseOrderSummary>>> ListAsync(Guid organisationId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Result<(IReadOnlyList<PurchaseOrderSummary> Items, int TotalCount)>> ListPagedAsync(Guid organisationId, int page, int pageSize, string? status, Guid? supplierId, string? search, DateTime? dateFrom, DateTime? dateTo, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Result<(IReadOnlyList<PurchaseOrderSummary> Items, int TotalCount)>> ListWindowAsync(Guid organisationId, int skip, int take, string? status, Guid? supplierId, string? search, DateTime? dateFrom, DateTime? dateTo, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Result<TransformResponse>> TransformAsync(Guid organisationId, Guid orderId, OutputFormat format, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Result<DownloadUrl>> GetDownloadUrlAsync(Guid organisationId, Guid orderId, Guid artifactId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Result<PurchaseOrderEntity>> ResolveAsync(Guid organisationId, Guid orderId, IReadOnlyList<LineResolution> resolutions, bool saveMappings, CancellationToken ct, ResolveHeaderFields? header = null)
            => throw new NotImplementedException();
        public Task<Result<int>> AcceptAiSuggestionsAsync(Guid organisationId, Guid orderId, double minConfidence, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<Result<PurchaseOrderEntity>> MarkRejectedAsync(Guid organisationId, Guid orderId, string reason, CancellationToken ct)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// Test double for <see cref="IEmailBodyOrderExtractor"/>. Returns a fixed
    /// result on every call; <see cref="NoOp"/> short-circuits with
    /// <c>Success=false</c> so tests that don't care about the body path see
    /// the router behave exactly as before.
    /// </summary>
    private sealed class FakeBodyExtractor : IEmailBodyOrderExtractor
    {
        public static readonly FakeBodyExtractor NoOp = new(
            new EmailBodyExtractionResult(Success: false, Confidence: 0, Order: null, FailureReason: "no-op fake"));

        private readonly EmailBodyExtractionResult _result;
        public int Calls { get; private set; }

        public FakeBodyExtractor(EmailBodyExtractionResult result) { _result = result; }

        public Task<EmailBodyExtractionResult> ExtractAsync(string emailBody, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(_result);
        }
    }

    /// <summary>
    /// Captures every log line with its level so tests can pin the level of one
    /// message. <see cref="LevelOf"/> fails loudly when the message is absent or
    /// ambiguous — a renamed template must break the test, not silently pass it.
    /// </summary>
    private sealed class RecordingLogger : ILogger<InboundEmailRouter>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));

        public LogLevel LevelOf(string messageFragment)
        {
            var matches = Entries
                .Where(e => e.Message.Contains(messageFragment, StringComparison.OrdinalIgnoreCase))
                .ToList();

            matches.Should().ContainSingle(
                $"exactly one log line should contain '{messageFragment}'; captured: "
                + string.Join(" | ", Entries.Select(e => $"[{e.Level}] {e.Message}")));

            return matches[0].Level;
        }
    }

    private sealed class RecordingEnqueuer : IParseJobEnqueuer
    {
        public List<(Guid OrderId, Guid OrgId)> Calls { get; } = new();

        public Task EnqueueAsync(Guid orderId, Guid orgId, CancellationToken ct)
        {
            Calls.Add((orderId, orgId));
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Minimal in-memory DbContext that materialises only what the router
    /// touches: Organisations, Suppliers, and AuditEvents. Other entities are
    /// ignored to avoid fabricating fixtures.
    /// </summary>
    private sealed class InboundEmailTestDbContext : ProcuLinkDbContext
    {
        public InboundEmailTestDbContext(DbContextOptions<ProcuLinkDbContext> options)
            : base(options)
        {
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Ignore<AppUser>();
            modelBuilder.Ignore<Membership>();
            modelBuilder.Ignore<SupplierProfileEntity>();
            modelBuilder.Ignore<PurchaseOrderEntity>();
            modelBuilder.Ignore<PurchaseOrderLineEntity>();
            modelBuilder.Ignore<OrderParty>();
            modelBuilder.Ignore<SourceCapture>();
            modelBuilder.Ignore<CanonicalFieldDef>();
            modelBuilder.Ignore<ItemMapping>();
            modelBuilder.Ignore<OutboundArtifact>();
            modelBuilder.Ignore<DeliveryAttempt>();
            modelBuilder.Ignore<SupplierPoMapping>();
            modelBuilder.Ignore<SupplierDeliveryConfig>();
            modelBuilder.Ignore<IdempotencyKey>();
            modelBuilder.Ignore<TenantApiKey>();
            modelBuilder.Ignore<IntegrationSubscription>();
            modelBuilder.Ignore<AiUsageMonthly>();
            modelBuilder.Ignore<PoPassportEvent>();
            modelBuilder.Ignore<SftpIngressConfig>();
            modelBuilder.Ignore<ImportedSftpFile>();
            modelBuilder.Ignore<S3IngressConfig>();
            modelBuilder.Ignore<ImportedS3Object>();
            modelBuilder.Ignore<Buyer>();
            modelBuilder.Ignore<ValidationRule>();
            modelBuilder.Ignore<OutputTemplate>();
            modelBuilder.Ignore<InvoiceEntity>();
            modelBuilder.Ignore<InvoiceLineEntity>();
            modelBuilder.Ignore<AdvanceShippingNoticeEntity>();
            modelBuilder.Ignore<AsnPackageEntity>();
            modelBuilder.Ignore<AsnPackageLineEntity>();

            modelBuilder.Entity<Organisation>(b =>
            {
                b.HasKey(x => x.Id);
                b.Ignore(x => x.Memberships);
                b.Ignore(x => x.PurchaseOrders);
                b.Ignore(x => x.ItemMappings);
                b.Ignore(x => x.OutboundArtifacts);
                b.Ignore(x => x.DeliveryAttempts);
                b.Ignore(x => x.AuditEvents);
                b.Ignore(x => x.ApiKeys);
                b.Ignore(x => x.IntegrationSubscriptions);
            });

            modelBuilder.Entity<Supplier>(b =>
            {
                b.HasKey(x => x.Id);
                b.Ignore(x => x.Organisation);
                b.Ignore(x => x.SupplierProfiles);
                b.Ignore(x => x.PurchaseOrders);
                b.Ignore(x => x.ItemMappings);
                b.Ignore(x => x.PoMappings);
                b.Ignore(x => x.DeliveryConfigs);
            });

            modelBuilder.Entity<AuditEvent>(b =>
            {
                b.HasKey(x => x.Id);
                b.Ignore(x => x.Organisation);
                b.Ignore(x => x.User);
                // JsonDocument is not supported by InMemory; ignore the payload
                // column so audit writes don't blow up. The router writes audit
                // best-effort and swallows exceptions either way.
                b.Ignore(x => x.Payload);
            });
        }
    }
}
