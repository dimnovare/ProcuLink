using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Api.Services;
using ProcuLink.Api.Tests.Services;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services;
using ProcuLink.Core.Services.Ai;
using ProcuLink.Core.Services.Mapping;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Infrastructure.Services.Detection;
using ProcuLink.Transform.Output;
using ProcuLink.Transform.Parsing;
using Xunit;

namespace ProcuLink.Api.Tests.Integration;

/// <summary>
/// The round-trip proof for CSV, XLSX and PDF — the three upload formats the product's wedge
/// actually runs on, and the three the June ship-to work never reached.
///
/// <para><c>CxmlShipToSurvivesParseToEmitPostgresTests</c> proved the path for the structured
/// formats. It could not speak for these: <c>CsvOrderParser</c>, <c>XlsxOrderParser</c> and
/// <c>PdfOrderParser</c> filled only the core four header fields, so <c>ParsedOrder.Parties</c>
/// was null, the denormalisation in <c>OrderIngestionService</c> found no <c>shipTo</c> party,
/// all sixteen ShipTo*/BillTo* columns were written NULL, and <c>CxmlTransformService.BuildShipTo</c>
/// — which gates on <c>ShipToName</c> — appended nothing. A buyer uploaded a spreadsheet naming a
/// delivery address and the supplier received an order without one.</para>
///
/// <para>A parser-level assertion would not catch that, because the defect is that the content
/// dies between the parse and the emit. So each test here spans the whole path: authored bytes →
/// <c>ParseStoredFileAsync</c> → real Postgres → <b>a fresh DbContext</b> → <c>CxmlTransformService</c>
/// → emitted document.</para>
///
/// <para>The fresh context is load-bearing. <c>ParseStoredFileAsync</c> reflects every persisted
/// value back onto the tracked entity after commit, and EF identity resolution would hand that
/// same instance back to a re-read on the same context — so a shared-context assertion would pass
/// off the in-memory reflection and prove nothing about what reached the database. Real Postgres
/// is mandatory for the same reason it is there: the persist block opens an explicit transaction
/// and writes through <c>ExecuteUpdateAsync</c>, neither of which EF InMemory can translate.
/// Docker-gated; skips where absent.</para>
///
/// <para><b>Fixture provenance.</b> Unlike the cXML round trip, every document below is AUTHORED —
/// there is no captured customer CSV, workbook or PDF in this repository to run against. These
/// prove the chain carries a ship-to end to end when the document names one, and (via the paired
/// negative controls) that it invents none when the document does not. They prove nothing about
/// how real buyers spell these headers, how their spreadsheets are laid out, or how their PDFs
/// print an address block — the author and the reader were the same person.</para>
/// </summary>
[Collection("postgres-container")]
public sealed class FlatFormatShipToSurvivesParseToEmitPostgresTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private const string ShipToName    = "Contoso Warehouse OY";
    private const string ShipToStreet  = "2 Example Road";
    private const string ShipToCity    = "Example Town";
    private const string ShipToPostal  = "00001";
    private const string ShipToCountry = "EE";

    // A DIFFERENT street and city from the ship-to, so a parser or emitter that echoed one
    // block into both would fail here rather than pass twice.
    private const string BillToName   = "Contoso Finance OY";
    private const string BillToStreet = "4 Example Lane";
    private const string BillToCity   = "Example Borough";

    private string? _databaseConnectionString;
    private string? _connectionString;

    public async Task InitializeAsync()
    {
        if (DockerProbe.UnavailableReason is not null)
            return;

        _databaseConnectionString = await postgres.CreateDatabaseAsync("proculink_flatshipto");

        _connectionString = new Npgsql.NpgsqlConnectionStringBuilder(_databaseConnectionString)
        {
            Pooling = false,
        }.ConnectionString;
    }

    public async Task DisposeAsync()
    {
        await postgres.DropDatabaseAsync(_databaseConnectionString);
    }

    // ── CSV ────────────────────────────────────────────────────────────────────

    [DockerRequiredFact]
    public async Task CsvShipTo_survivesParsePersistAndTransform_intoTheEmittedDocument()
    {
        var order = await ParseAndReadBackAsync(".csv", CsvWithParties(), new CsvOrderParser());

        AssertShipToColumns(order);
        Assert.Equal(BillToName,   order.BillToName);
        Assert.Equal(BillToStreet, order.BillToStreet);
        Assert.Equal(BillToCity,   order.BillToCity);

        await AssertPartiesPersistedAsync(order.Id);
        AssertEmittedCxmlCarriesTheAddress(await EmitCxmlAsync(order), expectBillTo: true);
    }

    /// <summary>
    /// The negative control for the CSV round trip. A CSV whose header names no delivery address
    /// must reach the supplier with no ShipTo — otherwise the test above would pass on an emitter
    /// that writes an address block unconditionally, and would prove nothing.
    /// </summary>
    [DockerRequiredFact]
    public async Task CsvWithoutPartyColumns_emitsNoShipTo_soThePositiveTestIsNotVacuous()
    {
        var order = await ParseAndReadBackAsync(".csv", CsvWithoutParties(), new CsvOrderParser());

        Assert.Null(order.ShipToName);
        Assert.Null(order.ShipToStreet);
        await AssertNoPartiesPersistedAsync(order.Id);

        var emitted = await EmitCxmlAsync(order);
        Assert.DoesNotContain("<ShipTo>", emitted);
        Assert.DoesNotContain("<BillTo>", emitted);
    }

    // ── XLSX ───────────────────────────────────────────────────────────────────

    [DockerRequiredFact]
    public async Task XlsxShipTo_survivesParsePersistAndTransform_intoTheEmittedDocument()
    {
        var order = await ParseAndReadBackAsync(".xlsx", XlsxWithParties(), new XlsxOrderParser());

        AssertShipToColumns(order);
        Assert.Equal(BillToName, order.BillToName);
        Assert.Equal(BillToCity, order.BillToCity);

        await AssertPartiesPersistedAsync(order.Id);
        AssertEmittedCxmlCarriesTheAddress(await EmitCxmlAsync(order), expectBillTo: true);
    }

    [DockerRequiredFact]
    public async Task XlsxWithoutPartyColumns_emitsNoShipTo_soThePositiveTestIsNotVacuous()
    {
        var order = await ParseAndReadBackAsync(".xlsx", XlsxWithoutParties(), new XlsxOrderParser());

        Assert.Null(order.ShipToName);
        await AssertNoPartiesPersistedAsync(order.Id);

        var emitted = await EmitCxmlAsync(order);
        Assert.DoesNotContain("<ShipTo>", emitted);
    }

    // ── PDF ────────────────────────────────────────────────────────────────────

    [DockerRequiredFact]
    public async Task PdfShipTo_survivesParsePersistAndTransform_intoTheEmittedDocument()
    {
        var order = await ParseAndReadBackAsync(".pdf", PdfWithInlineShipToLabels(), new PdfOrderParser());

        AssertShipToColumns(order);

        // No bill-to: this parser already reads "Bill To:" as the BUYER name, so it builds no
        // bill-to party. The column stays NULL and the emitter appends no <BillTo>.
        Assert.Null(order.BillToName);

        await AssertPartiesPersistedAsync(order.Id, expectBillTo: false);

        var emitted = await EmitCxmlAsync(order);
        AssertEmittedCxmlCarriesTheAddress(emitted, expectBillTo: false);
    }

    /// <summary>
    /// The negative control that also pins the judgement call: a PDF printing "Ship To" as a bare
    /// block label, with the address on the lines beneath it, is deliberately NOT read. Deciding
    /// which continuation line is the street and which is the city can only be done by counting
    /// lines, and a delivery address guessed from layout is worse than an absent one — once
    /// persisted, nothing downstream can tell it apart from one the buyer actually stated.
    /// </summary>
    [DockerRequiredFact]
    public async Task PdfWithBlockShipToLabel_emitsNoShipTo_becauseTheLayoutIsNotAStatement()
    {
        var order = await ParseAndReadBackAsync(".pdf", PdfWithBlockShipToLabel(), new PdfOrderParser());

        Assert.Null(order.ShipToName);
        Assert.Null(order.ShipToStreet);
        await AssertNoPartiesPersistedAsync(order.Id);

        var emitted = await EmitCxmlAsync(order);
        Assert.DoesNotContain("<ShipTo>", emitted);
    }

    // ── Shared assertions ──────────────────────────────────────────────────────

    private static void AssertShipToColumns(PurchaseOrderEntity order)
    {
        Assert.Equal(ShipToName,    order.ShipToName);
        Assert.Equal(ShipToStreet,  order.ShipToStreet);
        Assert.Equal(ShipToCity,    order.ShipToCity);
        Assert.Equal(ShipToPostal,  order.ShipToPostalCode);
        Assert.Equal(ShipToCountry, order.ShipToCountry);
    }

    private static void AssertEmittedCxmlCarriesTheAddress(string emitted, bool expectBillTo)
    {
        Assert.Contains("<ShipTo>", emitted);
        Assert.Contains(ShipToName, emitted);
        Assert.Contains(ShipToStreet, emitted);
        Assert.Contains(ShipToCity, emitted);
        Assert.Contains(ShipToPostal, emitted);

        if (expectBillTo)
        {
            Assert.Contains("<BillTo>", emitted);
            Assert.Contains(BillToName, emitted);
        }
        else
        {
            Assert.DoesNotContain("<BillTo>", emitted);
        }
    }

    /// <summary>The lossless order_parties rows, read from Postgres on the verification context.</summary>
    private async Task AssertPartiesPersistedAsync(Guid orderId, bool expectBillTo = true)
    {
        await using var verify = new ProcuLinkDbContext(Options());
        var parties = await verify.OrderParties.AsNoTracking()
            .Where(p => p.OrderId == orderId)
            .ToListAsync();

        Assert.Contains(parties, p => p.Role == "shipTo" && p.Name == ShipToName && p.City == ShipToCity);
        if (expectBillTo)
            Assert.Contains(parties, p => p.Role == "billTo" && p.Name == BillToName);
        else
            Assert.DoesNotContain(parties, p => p.Role == "billTo");
    }

    private async Task AssertNoPartiesPersistedAsync(Guid orderId)
    {
        await using var verify = new ProcuLinkDbContext(Options());
        Assert.Empty(await verify.OrderParties.AsNoTracking().Where(p => p.OrderId == orderId).ToListAsync());
    }

    // ── Authored documents ─────────────────────────────────────────────────────

    private static byte[] CsvWithParties() => System.Text.Encoding.UTF8.GetBytes(
        "PoNumber,BuyerName,Currency,LineNumber,BuyerItemCode,Description,Quantity,Unit,UnitPrice," +
        "Ship To Name,Ship To Street,Ship To City,Ship To Postal Code,Ship To Country," +
        "Bill To Name,Bill To Street,Bill To City\n" +
        "PO-FLAT-CSV,Contoso Buying OY,EUR,1,BUY-A-0001,Widget A,2,EA,4.50," +
        $"{ShipToName},{ShipToStreet},{ShipToCity},{ShipToPostal},{ShipToCountry}," +
        $"{BillToName},{BillToStreet},{BillToCity}\n");

    private static byte[] CsvWithoutParties() => System.Text.Encoding.UTF8.GetBytes(
        "PoNumber,BuyerName,Currency,LineNumber,BuyerItemCode,Description,Quantity,Unit,UnitPrice\n" +
        "PO-FLAT-CSV-BARE,Contoso Buying OY,EUR,1,BUY-A-0001,Widget A,2,EA,4.50\n");

    private static byte[] XlsxWithParties() => BuildXlsx(
        new[]
        {
            "PoNumber", "BuyerName", "Currency", "LineNumber", "BuyerItemCode", "Description",
            "Quantity", "Unit", "UnitPrice",
            "Ship To Name", "Ship To Street", "Ship To City", "Ship To Postal Code", "Ship To Country",
            "Bill To Name", "Bill To City",
        },
        new string?[]
        {
            "PO-FLAT-XLSX", "Contoso Buying OY", "EUR", "1", "BUY-A-0001", "Widget A",
            "2", "EA", "4.50",
            ShipToName, ShipToStreet, ShipToCity, ShipToPostal, ShipToCountry,
            BillToName, BillToCity,
        });

    private static byte[] XlsxWithoutParties() => BuildXlsx(
        new[]
        {
            "PoNumber", "BuyerName", "Currency", "LineNumber", "BuyerItemCode", "Description",
            "Quantity", "Unit", "UnitPrice",
        },
        new string?[] { "PO-FLAT-XLSX-BARE", "Contoso Buying OY", "EUR", "1", "BUY-A-0001", "Widget A", "2", "EA", "4.50" });

    private static byte[] PdfWithInlineShipToLabels() => OrderServicePdfRoutingTests.CreatePdf(
        "PO Number: PO-FLAT-PDF",
        "Order Date: 2026-05-20",
        "Buyer: Contoso Buying OY",
        "Currency: EUR",
        $"Ship To: {ShipToName}",
        $"Ship To Address: {ShipToStreet}",
        $"Ship To City: {ShipToCity}",
        $"Ship To Postal Code: {ShipToPostal}",
        $"Ship To Country: {ShipToCountry}",
        "Line BuyerItemCode Description Quantity Unit UnitPrice",
        "1 BUY-A-0001 Widget A 2 PCS 4.50");

    private static byte[] PdfWithBlockShipToLabel() => OrderServicePdfRoutingTests.CreatePdf(
        "PO Number: PO-FLAT-PDF-BLOCK",
        "Order Date: 2026-05-20",
        "Buyer: Contoso Buying OY",
        "Currency: EUR",
        "Ship To",
        ShipToName,
        ShipToStreet,
        $"{ShipToCity} {ShipToPostal}",
        "Line BuyerItemCode Description Quantity Unit UnitPrice",
        "1 BUY-A-0001 Widget A 2 PCS 4.50");

    private static byte[] BuildXlsx(string[] header, string?[] row)
    {
        using var ms = new MemoryStream();
        using (var wb = new XLWorkbook())
        {
            var ws = wb.Worksheets.Add("Sheet1");
            for (var c = 0; c < header.Length; c++)
                ws.Cell(1, c + 1).Value = header[c];
            for (var c = 0; c < row.Length; c++)
                if (row[c] is not null)
                    ws.Cell(2, c + 1).Value = row[c];
            wb.SaveAs(ms);
        }
        return ms.ToArray();
    }

    // ── Harness ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Seeds a parsing order, runs the real ingestion over <paramref name="fixtureBytes"/>, then
    /// re-reads the row on a FRESH context — never the one the parse ran on.
    /// </summary>
    private async Task<PurchaseOrderEntity> ParseAndReadBackAsync(
        string extension, byte[] fixtureBytes, IPurchaseOrderParser parser)
    {
        var (orgId, orderId) = await SeedParsingOrderAsync(extension, fixtureBytes);

        await using (var parseDb = new ProcuLinkDbContext(Options()))
        {
            var result = await BuildIngestion(parseDb, parser).ParseStoredFileAsync(orgId, orderId, CancellationToken.None);
            Assert.True(result.IsSuccess, result.Error);
        }

        await using var verify = new ProcuLinkDbContext(Options());
        return await verify.PurchaseOrders
            .Include(o => o.Lines)
            .Include(o => o.Supplier)
            .AsNoTracking()
            .SingleAsync(o => o.Id == orderId);
    }

    private static async Task<string> EmitCxmlAsync(PurchaseOrderEntity order)
    {
        var result = await new CxmlTransformService()
            .TransformAsync(order, OutputFormat.CXml, CancellationToken.None);

        result.Content.Position = 0;
        using var reader = new StreamReader(result.Content);
        return await reader.ReadToEndAsync();
    }

    private DbContextOptions<ProcuLinkDbContext> Options() =>
        new DbContextOptionsBuilder<ProcuLinkDbContext>().UseNpgsql(_connectionString!).Options;

    private byte[] _fixtureBytes = Array.Empty<byte>();

    private async Task<(Guid orgId, Guid orderId)> SeedParsingOrderAsync(string extension, byte[] fixtureBytes)
    {
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        await using var db = new ProcuLinkDbContext(Options());
        db.Organisations.Add(new Organisation
        {
            Id = orgId,
            ClerkOrgId = $"org_flatshipto_{orgId:N}",
            Name = "Flat Format Round Trip Org",
            Slug = $"flatshipto-{orgId:N}",
            Plan = "operations",
            AccountStatus = "active",
            CreatedAt = now,
        });
        db.Suppliers.Add(new Supplier { Id = supplierId, OrgId = orgId, Name = "Round Trip Supplier", CreatedAt = now });
        db.PurchaseOrders.Add(new PurchaseOrderEntity
        {
            Id = orderId,
            OrgId = orgId,
            SupplierId = supplierId,
            PoNumber = $"PO-FLAT-{orderId:N}"[..20],
            OrderDate = DateOnly.FromDateTime(now),
            Currency = "EUR",
            Status = OrderStatusConstants.Parsing,
            // The extension drives parser selection.
            SourceFileKey = $"{orgId}/{orderId}/order{extension}",
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();

        _fixtureBytes = fixtureBytes;
        return (orgId, orderId);
    }

    private OrderIngestionService BuildIngestion(ProcuLinkDbContext db, IPurchaseOrderParser parser)
    {
        var storage = new Mock<IFileStorageService>();
        // Fresh stream per download so a retry can re-read it.
        storage
            .Setup(s => s.DownloadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(_fixtureBytes));

        var itemMappings = new Mock<IItemMappingService>();
        // Resolve every buyer code so the order reaches "ready" rather than parking in review.
        itemMappings
            .Setup(s => s.ResolveManyAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, Guid _, IEnumerable<string> codes, CancellationToken _) =>
                (IReadOnlyDictionary<string, string?>)codes
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(c => c, c => (string?)$"SUP-{c}", StringComparer.OrdinalIgnoreCase));

        var poMappings = new Mock<IPoMappingService>();
        poMappings
            .Setup(s => s.GetAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PoMappingConfig?)null);

        var aiMappings = new Mock<IAiMappingService>();
        aiMappings
            .Setup(s => s.SuggestSupplierItemCodesAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<AiMappingLineContext>>(),
                It.IsAny<IReadOnlyList<AiMappingCandidate>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyDictionary<int, AiMappingSuggestion>)new Dictionary<int, AiMappingSuggestion>());

        return new OrderIngestionService(
            db,
            storage.Object,
            new OrderParserFactory(new[] { parser }),
            itemMappings.Object,
            poMappings.Object,
            aiMappings.Object,
            NullLogger<OrderService>.Instance,
            new Mock<IIntegrationTriggerService>().Object,
            new FormatDetectorService(),
            new ProcuLink.Transform.Tokenizing.SourceTokenizer(),
            structuredExtractor: null,
            new OrderServiceShared(db, new OrderExceptionService(db), NullLogger<OrderService>.Instance),
            catalogRetrieval: null,
            effectiveConfig: null);
    }
}
