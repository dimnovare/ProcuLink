using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Net.Http.Headers;
using Moq;
using ProcuLink.Api.Controllers;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Tenancy;
using Xunit;

namespace ProcuLink.Api.Tests.Controllers;

/// <summary>
/// GET /api/orders/{id}/source — the original uploaded document, streamed through the API.
///
/// <para>Every test here builds the controller over a context armed with
/// <see cref="ProcuLinkDbContext.ScopeToOrganisation"/>, which is what the request pipeline does
/// in production. That matters: the endpoint carries no hand-written
/// <c>.Where(o =&gt; o.OrgId == …)</c>, so an unscoped context would answer from every organisation
/// and a fixture that forgot to arm would prove nothing.</para>
/// </summary>
public sealed class OrderSourceDocumentControllerTests
{
    // ── Bytes that are unambiguously one format, so a content sniff has a real answer ──────────
    private static readonly byte[] PdfBytes =
        Encoding.ASCII.GetBytes("%PDF-1.7\n1 0 obj\n<< /Type /Catalog >>\nendobj\ntrailer\n%%EOF\n");

    /// <summary>
    /// Four columns deliberately: <c>FormatDetectorService.TryDetectCsv</c> requires at least 3
    /// consistent separators across the first two lines, so a 3-column CSV does NOT sniff as CSV.
    /// <see cref="ANarrowCsvThatDoesNotSniff_FallsBackToTheParseTimeFormat"/> pins that case.
    /// </summary>
    private static readonly byte[] CsvBytes =
        Encoding.UTF8.GetBytes("po_number,buyer_code,qty,uom\nPO-1,ACM-BOLT-001,10,EA\nPO-1,ACM-NUT-002,4,EA\n");

    /// <summary>Neither markup, nor a known magic number, nor separator-shaped: sniffs "unknown".</summary>
    private static readonly byte[] OpaqueBytes = [0x00, 0x01, 0x02, 0x03, 0xFF, 0xFE, 0x7F, 0x42];

    private static DbContextOptions<ProcuLinkDbContext> Options(string dbName) =>
        new DbContextOptionsBuilder<ProcuLinkDbContext>().UseInMemoryDatabase(dbName).Options;

    private static Guid SeedOrder(
        ProcuLinkDbContext db,
        Guid orgId,
        string sourceFileKey,
        DateTime? purgedAt = null,
        string? capturedFormat = null)
    {
        var now = DateTime.UtcNow;
        var orderId = Guid.NewGuid();

        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id = orderId,
            OrgId = orgId,
            SupplierId = Guid.NewGuid(),
            PoNumber = $"PO-{orderId.ToString()[..8]}",
            OrderDate = DateOnly.FromDateTime(now),
            Currency = "EUR",
            Status = "ready",
            SourceFileKey = sourceFileKey,
            SourceFilePurgedAt = purgedAt,
            CreatedAt = now,
            UpdatedAt = now,
        });

        if (capturedFormat is not null)
        {
            db.SourceCaptures.Add(new SourceCapture
            {
                Id = Guid.NewGuid(),
                OrderId = orderId,
                OrgId = orgId,
                Format = capturedFormat,
                CapturedAt = now,
            });
        }

        db.SaveChanges();
        return orderId;
    }

    private static OrderSourceDocumentController Build(
        ProcuLinkDbContext db,
        Guid tenantOrgId,
        Mock<IFileStorageService>? storage = null)
    {
        var tenant = new Mock<ICurrentTenantService>();
        tenant.SetupGet(t => t.OrganisationId).Returns(tenantOrgId);

        var controller = new OrderSourceDocumentController(
            db,
            tenant.Object,
            (storage ?? new Mock<IFileStorageService>()).Object,
            // The real detector — the point of these tests is what the BYTES say, so substituting
            // the sniffer would substitute away the behaviour under test.
            new ProcuLink.Infrastructure.Services.Detection.FormatDetectorService(),
            NullLogger<OrderSourceDocumentController>.Instance);

        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller;
    }

    private static Mock<IFileStorageService> StorageServing(string key, byte[] bytes)
    {
        var storage = new Mock<IFileStorageService>();
        storage.Setup(s => s.DownloadAsync(key, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(bytes, writable: false));
        return storage;
    }

    // ── The org boundary ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The defect this endpoint must never have: serving one organisation's purchase order to
    /// another. Two orgs are seeded so "returns nothing" and "returns only mine" are
    /// distinguishable — a single-org fixture would pass either way.
    /// </summary>
    [Fact]
    public async Task AnotherOrganisationsOrder_Is404_AndOwnOrderStillServes()
    {
        var dbName = $"src-doc-{Guid.NewGuid()}";
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();
        const string keyA = "a/o/mine.pdf";
        const string keyB = "b/o/theirs.pdf";

        Guid orderA, orderB;
        await using (var seed = new ProcuLinkDbContext(Options(dbName)))
        {
            orderA = SeedOrder(seed, orgA, keyA);
            orderB = SeedOrder(seed, orgB, keyB);
        }

        var storage = new Mock<IFileStorageService>();
        storage.Setup(s => s.DownloadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(PdfBytes, writable: false));

        await using var db = new ProcuLinkDbContext(Options(dbName));
        db.ScopeToOrganisation(orgA);
        var ctrl = Build(db, orgA, storage);

        // Org B's order: refused, and storage was never asked for its object.
        var refused = await ctrl.GetSource(orderB, CancellationToken.None);
        Assert.IsType<NotFoundResult>(refused);
        storage.Verify(s => s.DownloadAsync(keyB, It.IsAny<CancellationToken>()), Times.Never);

        // Anti-vacuity: the same controller, same fixture, DOES serve org A's own document — so
        // the 404 above is the organisation boundary, not a broken fixture.
        var served = Assert.IsType<FileContentResult>(await ctrl.GetSource(orderA, CancellationToken.None));
        Assert.Equal(PdfBytes, served.FileContents);
    }

    /// <summary>
    /// The endpoint has no organisation predicate of its own, so an unarmed context would read
    /// across every tenant. It must refuse loudly instead.
    /// </summary>
    [Fact]
    public async Task AnUnscopedContext_IsRefused_NotAnsweredFromEveryOrganisation()
    {
        var dbName = $"src-doc-{Guid.NewGuid()}";
        var orgA = Guid.NewGuid();

        Guid orderA;
        await using (var seed = new ProcuLinkDbContext(Options(dbName)))
            orderA = SeedOrder(seed, orgA, "a/o/mine.pdf");

        // Deliberately NOT scoped — this is the state a [CrossOrganisationRead] declaration
        // would leave the request in.
        await using var db = new ProcuLinkDbContext(Options(dbName));
        var ctrl = Build(db, orgA, StorageServing("a/o/mine.pdf", PdfBytes));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ctrl.GetSource(orderA, CancellationToken.None));
    }

    /// <summary>
    /// The scope assertion compares against the CALLING tenant, not merely "armed at all".
    /// A context armed to a different organisation is still a cross-tenant read.
    /// </summary>
    [Fact]
    public async Task AContextArmedToADifferentOrganisation_IsRefused()
    {
        var dbName = $"src-doc-{Guid.NewGuid()}";
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();

        Guid orderA;
        await using (var seed = new ProcuLinkDbContext(Options(dbName)))
            orderA = SeedOrder(seed, orgA, "a/o/mine.pdf");

        await using var db = new ProcuLinkDbContext(Options(dbName));
        db.ScopeToOrganisation(orgB);
        var ctrl = Build(db, orgA, StorageServing("a/o/mine.pdf", PdfBytes));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ctrl.GetSource(orderA, CancellationToken.None));
    }

    /// <summary>
    /// The opt-out that would break the org boundary is a two-word diff. Nothing else in the repo
    /// would catch it: the attribute is read off endpoint metadata by middleware, so a controller
    /// carrying it compiles, runs, and serves other tenants' documents.
    /// </summary>
    [Fact]
    public void TheEndpoint_DoesNotOptOutOfOrganisationScoping()
    {
        var type = typeof(OrderSourceDocumentController);

        Assert.Null(type.GetCustomAttribute<CrossOrganisationReadAttribute>(inherit: true));

        var actions = type
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName) // property accessors are not actions
            .ToList();

        Assert.NotEmpty(actions); // anti-vacuity: there is something to check
        Assert.All(actions, m =>
            Assert.Null(m.GetCustomAttribute<CrossOrganisationReadAttribute>(inherit: true)));
    }

    [Fact]
    public async Task AnUnknownOrderId_Is404()
    {
        var orgA = Guid.NewGuid();
        await using var db = new ProcuLinkDbContext(Options($"src-doc-{Guid.NewGuid()}"));
        db.ScopeToOrganisation(orgA);

        var result = await Build(db, orgA).GetSource(Guid.NewGuid(), CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    // ── Absent document: a normal answer, never a 500 ──────────────────────────────────────────

    [Fact]
    public async Task AnOrderWithNoStoredSourceFile_Is204_NotA500_AndNeverTouchesStorage()
    {
        var dbName = $"src-doc-{Guid.NewGuid()}";
        var orgA = Guid.NewGuid();

        Guid orderId;
        await using (var seed = new ProcuLinkDbContext(Options(dbName)))
            orderId = SeedOrder(seed, orgA, sourceFileKey: string.Empty);

        await using var db = new ProcuLinkDbContext(Options(dbName));
        db.ScopeToOrganisation(orgA);

        // Strict: ANY storage call throws, so "204" cannot be hiding a swallowed storage error.
        var strict = new Mock<IFileStorageService>(MockBehavior.Strict);
        var result = await Build(db, orgA, strict).GetSource(orderId, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
    }

    /// <summary>
    /// The row claims a file and storage disagrees — a real state after a manual bucket edit, a
    /// half-failed ingest, or a restore. The reviewer sees "no document", not a stack trace.
    /// </summary>
    [Fact]
    public async Task AMissingStorageObject_Is204_NotA500()
    {
        var dbName = $"src-doc-{Guid.NewGuid()}";
        var orgA = Guid.NewGuid();
        const string key = "a/o/gone.pdf";

        Guid orderId;
        await using (var seed = new ProcuLinkDbContext(Options(dbName)))
            orderId = SeedOrder(seed, orgA, key);

        await using var db = new ProcuLinkDbContext(Options(dbName));
        db.ScopeToOrganisation(orgA);

        var storage = new Mock<IFileStorageService>();
        storage.Setup(s => s.DownloadAsync(key, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new FileNotFoundException("no such object"));

        var result = await Build(db, orgA, storage).GetSource(orderId, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
    }

    [Fact]
    public async Task AnEmptyStorageObject_Is204()
    {
        var dbName = $"src-doc-{Guid.NewGuid()}";
        var orgA = Guid.NewGuid();
        const string key = "a/o/empty.csv";

        Guid orderId;
        await using (var seed = new ProcuLinkDbContext(Options(dbName)))
            orderId = SeedOrder(seed, orgA, key);

        await using var db = new ProcuLinkDbContext(Options(dbName));
        db.ScopeToOrganisation(orgA);

        var result = await Build(db, orgA, StorageServing(key, []))
            .GetSource(orderId, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
    }

    /// <summary>
    /// A purged blob is a deliberate, explainable state, not an absence — same contract the
    /// artifact download already uses, and storage must not be asked for a blob known to be gone.
    /// </summary>
    [Fact]
    public async Task APurgedSourceBlob_Is410WithThePolicyMessage_WithoutTouchingStorage()
    {
        var dbName = $"src-doc-{Guid.NewGuid()}";
        var orgA = Guid.NewGuid();

        Guid orderId;
        await using (var seed = new ProcuLinkDbContext(Options(dbName)))
            orderId = SeedOrder(seed, orgA, "a/o/purged.pdf", purgedAt: DateTime.UtcNow);

        await using var db = new ProcuLinkDbContext(Options(dbName));
        db.ScopeToOrganisation(orgA);

        var strict = new Mock<IFileStorageService>(MockBehavior.Strict);
        var result = await Build(db, orgA, strict).GetSource(orderId, CancellationToken.None);

        var gone = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status410Gone, gone.StatusCode);
        Assert.Contains(RetentionConstants.BlobPurgedError, gone.Value!.ToString());
    }

    // ── Honest content type ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// The defect this endpoint exists not to repeat, verbatim: the frontend's
    /// <c>sourceTypeFromKey()</c> splits a storage key on '.' and switches on the extension. Here
    /// the key says <c>.csv</c> and the bytes are a PDF. The bytes win.
    /// </summary>
    [Fact]
    public async Task ContentType_ComesFromTheBytes_NotTheStorageKeyExtension()
    {
        var dbName = $"src-doc-{Guid.NewGuid()}";
        var orgA = Guid.NewGuid();
        const string lyingKey = "a/o/order.csv";

        Guid orderId;
        await using (var seed = new ProcuLinkDbContext(Options(dbName)))
            orderId = SeedOrder(seed, orgA, lyingKey);

        await using var db = new ProcuLinkDbContext(Options(dbName));
        db.ScopeToOrganisation(orgA);

        var result = await Build(db, orgA, StorageServing(lyingKey, PdfBytes))
            .GetSource(orderId, CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("application/pdf", file.ContentType);
    }

    /// <summary>The same in the other direction, so the test above is not passing on a constant.</summary>
    [Fact]
    public async Task CsvBytesUnderAPdfKey_AreServedAsCsv()
    {
        var dbName = $"src-doc-{Guid.NewGuid()}";
        var orgA = Guid.NewGuid();
        const string lyingKey = "a/o/scan.pdf";

        Guid orderId;
        await using (var seed = new ProcuLinkDbContext(Options(dbName)))
            orderId = SeedOrder(seed, orgA, lyingKey);

        await using var db = new ProcuLinkDbContext(Options(dbName));
        db.ScopeToOrganisation(orgA);

        var result = await Build(db, orgA, StorageServing(lyingKey, CsvBytes))
            .GetSource(orderId, CancellationToken.None);

        Assert.Equal("text/csv", Assert.IsType<FileContentResult>(result).ContentType);
    }

    /// <summary>
    /// When the bytes sniff to nothing, the parse-time detected format is the next honest answer —
    /// it is what the detector concluded about this same file at ingest. The key still never
    /// participates: it says <c>.pdf</c> here and the capture says xlsx.
    /// </summary>
    [Fact]
    public async Task UnrecognisedBytes_FallBackToTheParseTimeDetectedFormat_NotTheKey()
    {
        var dbName = $"src-doc-{Guid.NewGuid()}";
        var orgA = Guid.NewGuid();
        const string lyingKey = "a/o/thing.pdf";

        Guid orderId;
        await using (var seed = new ProcuLinkDbContext(Options(dbName)))
            orderId = SeedOrder(seed, orgA, lyingKey, capturedFormat: "xlsx");

        await using var db = new ProcuLinkDbContext(Options(dbName));
        db.ScopeToOrganisation(orgA);
        var ctrl = Build(db, orgA, StorageServing(lyingKey, OpaqueBytes));

        var result = await ctrl.GetSource(orderId, CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal(
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            file.ContentType);
        // A type no browser renders is an attachment, never inline.
        Assert.StartsWith("attachment", ctrl.Response.Headers[HeaderNames.ContentDisposition].ToString());
    }

    /// <summary>
    /// A real gap in content sniffing, pinned rather than papered over: the detector needs at
    /// least 3 consistent separators, so a 2- or 3-column CSV sniffs as "unknown". The parse-time
    /// format covers it. Without a capture row such a file serves as opaque octets — an honest
    /// degradation, and still never a type read off the key.
    /// </summary>
    [Fact]
    public async Task ANarrowCsvThatDoesNotSniff_FallsBackToTheParseTimeFormat()
    {
        var dbName = $"src-doc-{Guid.NewGuid()}";
        var orgA = Guid.NewGuid();
        const string key = "a/o/narrow.csv";
        var narrowCsv = Encoding.UTF8.GetBytes("po_number,qty\nPO-1,10\nPO-1,4\n");

        Guid withCapture, withoutCapture;
        await using (var seed = new ProcuLinkDbContext(Options(dbName)))
        {
            withCapture = SeedOrder(seed, orgA, key, capturedFormat: "csv");
            withoutCapture = SeedOrder(seed, orgA, key);
        }

        await using var db = new ProcuLinkDbContext(Options(dbName));
        db.ScopeToOrganisation(orgA);
        var ctrl = Build(db, orgA, StorageServing(key, narrowCsv));

        var served = await ctrl.GetSource(withCapture, CancellationToken.None);
        Assert.Equal("text/csv", Assert.IsType<FileContentResult>(served).ContentType);

        // No capture row and no usable sniff: opaque, never guessed from the ".csv" in the key.
        var opaque = await ctrl.GetSource(withoutCapture, CancellationToken.None);
        Assert.Equal("application/octet-stream", Assert.IsType<FileContentResult>(opaque).ContentType);
    }

    [Fact]
    public async Task BytesAndCaptureBothUnknown_AreServedAsOpaqueOctets_NeverInline()
    {
        var dbName = $"src-doc-{Guid.NewGuid()}";
        var orgA = Guid.NewGuid();
        const string key = "a/o/mystery.csv";

        Guid orderId;
        await using (var seed = new ProcuLinkDbContext(Options(dbName)))
            orderId = SeedOrder(seed, orgA, key);

        await using var db = new ProcuLinkDbContext(Options(dbName));
        db.ScopeToOrganisation(orgA);
        var ctrl = Build(db, orgA, StorageServing(key, OpaqueBytes));

        var result = await ctrl.GetSource(orderId, CancellationToken.None);

        Assert.Equal("application/octet-stream", Assert.IsType<FileContentResult>(result).ContentType);
        Assert.StartsWith("attachment", ctrl.Response.Headers[HeaderNames.ContentDisposition].ToString());
    }

    // ── Headers that make it safe to render ───────────────────────────────────────────────────

    [Fact]
    public async Task ResponseHeaders_AreSafeToRenderInABrowser()
    {
        var dbName = $"src-doc-{Guid.NewGuid()}";
        var orgA = Guid.NewGuid();
        const string key = "a/o/purchase order (final).pdf";

        Guid orderId;
        await using (var seed = new ProcuLinkDbContext(Options(dbName)))
            orderId = SeedOrder(seed, orgA, key);

        await using var db = new ProcuLinkDbContext(Options(dbName));
        db.ScopeToOrganisation(orgA);
        var ctrl = Build(db, orgA, StorageServing(key, PdfBytes));

        await ctrl.GetSource(orderId, CancellationToken.None);

        var headers = ctrl.Response.Headers;
        var disposition = headers[HeaderNames.ContentDisposition].ToString();

        Assert.StartsWith("inline", disposition);
        Assert.Equal("nosniff", headers["X-Content-Type-Options"].ToString());
        // One tenant's purchase order must never sit in a shared cache.
        Assert.Equal("no-store", headers[HeaderNames.CacheControl].ToString());

        // The filename came from the key's last segment, reduced to a conservative ASCII set, so
        // nothing an uploader supplies can shape the header. Spaces and parens are gone; the
        // directory prefix never appears.
        Assert.Contains("purchase_order__final_.pdf", disposition);
        Assert.DoesNotContain("a/o/", disposition);
        Assert.DoesNotContain('\n', disposition);
        Assert.DoesNotContain('\r', disposition);
    }

    /// <summary>
    /// A storage key whose last segment sanitises away entirely must not emit a broken or empty
    /// filename parameter.
    /// </summary>
    [Fact]
    public async Task AKeyWithNoUsableFilename_OmitsTheFilenameParameter()
    {
        var dbName = $"src-doc-{Guid.NewGuid()}";
        var orgA = Guid.NewGuid();
        const string key = "a/o/...";

        Guid orderId;
        await using (var seed = new ProcuLinkDbContext(Options(dbName)))
            orderId = SeedOrder(seed, orgA, key);

        await using var db = new ProcuLinkDbContext(Options(dbName));
        db.ScopeToOrganisation(orgA);
        var ctrl = Build(db, orgA, StorageServing(key, PdfBytes));

        await ctrl.GetSource(orderId, CancellationToken.None);

        var disposition = ctrl.Response.Headers[HeaderNames.ContentDisposition].ToString();
        Assert.Equal("inline", disposition);
    }
}
