using Hangfire;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure;
using ProcuLink.Transform.Parsing;

namespace ProcuLink.Api.Jobs;

/// <summary>
/// Hangfire background job: parses the source file for a newly created invoice stub.
/// Idempotent — skips if status != "parsing".
/// </summary>
public class ParseInvoiceJob
{
    private readonly IInvoiceService            _invoices;
    private readonly IFileStorageService        _storage;
    private readonly InvoiceParserFactory       _parserFactory;
    private readonly ProcuLinkDbContext         _db;
    private readonly ILogger<ParseInvoiceJob>   _logger;

    public ParseInvoiceJob(
        IInvoiceService          invoices,
        IFileStorageService      storage,
        InvoiceParserFactory     parserFactory,
        ProcuLinkDbContext       db,
        ILogger<ParseInvoiceJob> logger)
    {
        _invoices      = invoices;
        _storage       = storage;
        _parserFactory = parserFactory;
        _db            = db;
        _logger        = logger;
    }

    [Queue("critical")]
    [AutomaticRetry(Attempts = 3, DelaysInSeconds = new[] { 5, 30, 120 })]
    public async Task ExecuteAsync(Guid invoiceId, Guid organisationId, CancellationToken ct)
    {
        _logger.LogInformation("ParseInvoiceJob starting for invoice {InvoiceId}", invoiceId);

        var invoice = await _invoices.GetAsync(organisationId, invoiceId, ct);
        if (invoice is null)
        {
            _logger.LogWarning("ParseInvoiceJob: invoice {InvoiceId} not found.", invoiceId);
            return;
        }
        if (invoice.Status != "parsing")
        {
            _logger.LogInformation("ParseInvoiceJob: invoice {InvoiceId} is not in 'parsing' status — skipping.", invoiceId);
            return;
        }
        if (string.IsNullOrEmpty(invoice.SourceFileKey) || string.IsNullOrEmpty(invoice.SourceFileName))
        {
            _logger.LogError("ParseInvoiceJob: invoice {InvoiceId} has no source file key.", invoiceId);
            return;
        }

        await using var stream = await _storage.DownloadAsync(invoice.SourceFileKey, ct);
        var ext    = Path.GetExtension(invoice.SourceFileName);
        var parser = _parserFactory.GetParser(ext, stream);
        var parsed = await parser.ParseAsync(stream, ct);

        var data = new ParsedInvoiceData(
            InvoiceNumber: parsed.InvoiceNumber,
            IssueDate:     parsed.IssueDate,
            DueDate:       parsed.DueDate,
            Currency:      parsed.Currency,
            BuyerRef:      parsed.BuyerRef,
            SupplierRef:   parsed.SupplierRef,
            PaymentTerms:  parsed.PaymentTerms,
            SubTotal:      parsed.SubTotal,
            TaxTotal:      parsed.TaxTotal,
            GrandTotal:    parsed.GrandTotal,
            Lines: parsed.Lines.Select(l => new ParsedInvoiceLineData(
                LineNumber:       l.LineNumber,
                Description:      l.Description,
                Quantity:         l.Quantity,
                UnitCode:         l.UnitCode,
                UnitPrice:        l.UnitPrice,
                TaxRate:          l.TaxRate,
                LineTotal:        l.LineTotal,
                BuyerItemCode:    l.BuyerItemCode,
                SupplierItemCode: l.SupplierItemCode
            )).ToList()
        );

        await _invoices.PersistParsedAsync(organisationId, invoiceId, data, ct);

        _logger.LogInformation(
            "ParseInvoiceJob completed for invoice {InvoiceId}, lines={Count}",
            invoiceId, parsed.Lines.Count);
    }

    public static void Enqueue(IBackgroundJobClient jobs, Guid invoiceId, Guid organisationId)
    {
        jobs.Enqueue<ParseInvoiceJob>(
            j => j.ExecuteAsync(invoiceId, organisationId, CancellationToken.None));
    }
}
