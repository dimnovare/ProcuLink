using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProcuLink.Api.Services;
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
/// The round-trip proof for the parser read gap: a REAL cXML purchase order carrying a ship-to
/// block goes in, and the same ship-to comes back out of the emitted cXML.
///
/// <para>The emitters, the entity columns and the migrations for ship-to / bill-to / contact all
/// shipped in June 2026, and <c>CxmlAddressBlockPersistencePostgresTests</c> already proves the 16
/// flat columns round-trip through Postgres when something writes them. Nothing did:
/// <c>CxmlOrderParser</c> never read <c>&lt;ShipTo&gt;</c>, so <c>ParsedOrder.Parties</c> was always
/// null, the denormalisation at <c>OrderIngestionService</c> found no <c>shipTo</c> party, all 16
/// columns were written NULL, and <c>CxmlTransformService.BuildShipTo</c> — which gates on
/// <c>ShipToName</c> — emitted nothing. A cXML order in produced a cXML order out with the
/// customer's delivery address silently deleted.</para>
///
/// <para>A parser-level assertion (<c>ParsedOrder.Parties is not null</c>) would NOT have caught
/// this, because the defect is that the content dies between the parse and the emit. So this test
/// deliberately spans the whole path: fixture bytes → <c>ParseStoredFileAsync</c> → real Postgres →
/// <b>a fresh DbContext</b> → <c>CxmlTransformService</c> → emitted document.</para>
///
/// <para>The fresh context is load-bearing. <c>ParseStoredFileAsync</c> reflects every persisted
/// value back onto the tracked entity after commit, and EF identity resolution would hand that same
/// instance back to a re-read on the same context — so a shared-context assertion would pass off
/// the in-memory reflection and prove nothing about what reached the database.</para>
///
/// <para>Real Postgres is mandatory, not a preference: the persist block opens an explicit
/// transaction and writes through <c>ExecuteUpdateAsync</c>, neither of which EF InMemory can
/// translate — it throws before the columns are ever written. Docker-gated; skips where absent.</para>
/// </summary>
[Collection("postgres-container")]
public sealed class CxmlShipToSurvivesParseToEmitPostgresTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    // A real Ariba punchout order, de-identified in #179/#184. Carries ShipTo + BillTo blocks
    // and an ItemOut@requestedDeliveryDate. Linked into this project by the `real-*.xml` glob
    // in ProcuLink.Api.Tests.csproj — one copy, sanitised once.
    private const string FixtureName = "real-cxml-1.2-ariba-punchout-mpn-differs.xml";

    // Verbatim from the fixture's <ShipTo> block.
    private const string ShipToName   = "Buyer Service GmbH";
    private const string ShipToStreet = "Grünenbeispiel 104-107";
    private const string ShipToCity   = "Example Stadt";
    private const string ShipToPostal = "00000";

    // Verbatim from the fixture's <BillTo> block (a DIFFERENT street/city from the ship-to,
    // so an emitter that echoed one block into both would fail here).
    private const string BillToStreet = "Beispielwestring 7-Tor 2-WE 2";
    private const string BillToCity   = "Example Dorf";

    private string? _databaseConnectionString;
    private string? _connectionString;

    public async Task InitializeAsync()
    {
        if (DockerProbe.UnavailableReason is not null)
            return;

        _databaseConnectionString = await postgres.CreateDatabaseAsync("proculink_cxmlshipto");

        _connectionString = new Npgsql.NpgsqlConnectionStringBuilder(_databaseConnectionString)
        {
            Pooling = false,
        }.ConnectionString;
    }

    public async Task DisposeAsync()
    {
        await postgres.DropDatabaseAsync(_databaseConnectionString);
    }

    // ── The round trip ─────────────────────────────────────────────────────────

    [DockerRequiredFact]
    public async Task CxmlShipTo_survivesParsePersistAndTransform_intoTheEmittedDocument()
    {
        var (orgId, orderId) = await SeedParsingOrderAsync();

        await using (var parseDb = new ProcuLinkDbContext(Options()))
        {
            var result = await BuildIngestion(parseDb).ParseStoredFileAsync(orgId, orderId, CancellationToken.None);
            Assert.True(result.IsSuccess, result.Error);
        }

        // Fresh context: read what actually landed in Postgres, not the post-commit reflection.
        await using var verify = new ProcuLinkDbContext(Options());

        var order = await verify.PurchaseOrders
            .Include(o => o.Lines)
            .Include(o => o.Supplier)
            .AsNoTracking()
            .SingleAsync(o => o.Id == orderId);

        // 1. The denormalised columns the emitters read are populated.
        Assert.Equal(ShipToName,   order.ShipToName);
        Assert.Equal(ShipToStreet, order.ShipToStreet);
        Assert.Equal(ShipToCity,   order.ShipToCity);
        Assert.Equal(ShipToPostal, order.ShipToPostalCode);
        Assert.Equal("DE",         order.ShipToCountry);
        Assert.Equal(BillToStreet, order.BillToStreet);
        Assert.Equal(BillToCity,   order.BillToCity);

        // 2. The lossless OrderParty rows are written alongside them.
        var parties = await verify.OrderParties.AsNoTracking()
            .Where(p => p.OrderId == orderId)
            .ToListAsync();
        Assert.Contains(parties, p => p.Role == "shipTo" && p.Name == ShipToName);
        Assert.Contains(parties, p => p.Role == "billTo" && p.City == BillToCity);

        // 3. The stated header total and the per-line requested delivery date survived too —
        //    both were read off the document and discarded before this change.
        Assert.Equal(164.1m, order.GrandTotal);
        Assert.Equal(new DateOnly(2026, 7, 17), order.RequestedDeliveryDate);

        // 4. THE POINT: the ship-to reappears in the emitted cXML.
        var emitted = await EmitCxmlAsync(order);

        Assert.Contains("<ShipTo>", emitted);
        Assert.Contains(ShipToName, emitted);
        Assert.Contains(ShipToStreet, emitted);
        Assert.Contains(ShipToCity, emitted);
        Assert.Contains(ShipToPostal, emitted);

        // And the bill-to, which is a distinct address in this document.
        Assert.Contains("<BillTo>", emitted);
        Assert.Contains(BillToStreet, emitted);
        Assert.Contains(BillToCity, emitted);
    }

    /// <summary>
    /// The negative control for the test above. An order whose source document carries no address
    /// block must still emit NO ShipTo/BillTo — otherwise the assertions above would pass on an
    /// emitter that writes an address block unconditionally, and would prove nothing.
    /// </summary>
    [DockerRequiredFact]
    public async Task CxmlWithoutAddressBlocks_emitsNoShipTo_soThePositiveTestIsNotVacuous()
    {
        var (orgId, orderId) = await SeedParsingOrderAsync(fixtureBytes: MinimalCxmlWithoutAddresses());

        await using (var parseDb = new ProcuLinkDbContext(Options()))
        {
            var result = await BuildIngestion(parseDb).ParseStoredFileAsync(orgId, orderId, CancellationToken.None);
            Assert.True(result.IsSuccess, result.Error);
        }

        await using var verify = new ProcuLinkDbContext(Options());
        var order = await verify.PurchaseOrders
            .Include(o => o.Lines)
            .Include(o => o.Supplier)
            .AsNoTracking()
            .SingleAsync(o => o.Id == orderId);

        Assert.Null(order.ShipToName);
        Assert.Empty(await verify.OrderParties.AsNoTracking().Where(p => p.OrderId == orderId).ToListAsync());

        var emitted = await EmitCxmlAsync(order);
        Assert.DoesNotContain("<ShipTo>", emitted);
        Assert.DoesNotContain("<BillTo>", emitted);
    }

    // ── Harness ────────────────────────────────────────────────────────────────

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

    private static byte[] ReadFixture()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", FixtureName);
        Assert.True(File.Exists(path), $"Fixture not copied to the test output: {path}");
        return File.ReadAllBytes(path);
    }

    /// <summary>A conformant cXML order with no ShipTo/BillTo/Total — the negative control.</summary>
    private static byte[] MinimalCxmlWithoutAddresses() =>
        System.Text.Encoding.UTF8.GetBytes(
            """
            <cXML payloadID="no-address@example.invalid" timestamp="2026-07-13T10:00:00-00:00">
              <Header>
                <From><Credential domain="NetworkId"><Identity>TestBuyer</Identity></Credential></From>
                <To><Credential domain="NetworkId"><Identity>TestSupplier</Identity></Credential></To>
              </Header>
              <Request deploymentMode="production">
                <OrderRequest>
                  <OrderRequestHeader orderID="PO-NO-ADDRESS" orderDate="2026-07-13" type="new" />
                  <ItemOut quantity="2" lineNumber="1">
                    <ItemID><SupplierPartID>BUY-1</SupplierPartID></ItemID>
                    <ItemDetail>
                      <UnitPrice><Money currency="EUR">10.00</Money></UnitPrice>
                      <Description xml:lang="en">Widget</Description>
                      <UnitOfMeasure>EA</UnitOfMeasure>
                    </ItemDetail>
                  </ItemOut>
                </OrderRequest>
              </Request>
            </cXML>
            """);

    private async Task<(Guid orgId, Guid orderId)> SeedParsingOrderAsync(byte[]? fixtureBytes = null)
    {
        var orgId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        await using var db = new ProcuLinkDbContext(Options());
        db.Organisations.Add(new Organisation
        {
            Id = orgId,
            ClerkOrgId = $"org_shipto_{orgId:N}",
            Name = "Ship-To Round Trip Org",
            Slug = $"shipto-{orgId:N}",
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
            PoNumber = "PO-SHIPTO-1",
            OrderDate = DateOnly.FromDateTime(now),
            Currency = "EUR",
            Status = OrderStatusConstants.Parsing,
            // The extension drives parser selection.
            SourceFileKey = $"{orgId}/{orderId}/order.xml",
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();

        _fixtureBytes = fixtureBytes ?? ReadFixture();
        return (orgId, orderId);
    }

    private byte[] _fixtureBytes = Array.Empty<byte>();

    private OrderIngestionService BuildIngestion(ProcuLinkDbContext db)
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
            new OrderParserFactory(new IPurchaseOrderParser[] { new CxmlOrderParser() }),
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
