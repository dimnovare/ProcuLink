using Hangfire;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ProcuLink.Api.Jobs;
using ProcuLink.Core.Services;
using ProcuLink.Transform.Output;

namespace ProcuLink.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/invoices")]
public sealed class InvoiceController : ControllerBase
{
    private readonly IInvoiceService                  _invoices;
    private readonly ICurrentTenantService            _tenant;
    private readonly IBackgroundJobClient             _jobs;
    private readonly PeppolBisInvoiceTransformService _peppolGenerator;
    private readonly PeppolBisValidator               _peppolValidator;
    private readonly ILogger<InvoiceController>       _logger;

    public InvoiceController(
        IInvoiceService                  invoices,
        ICurrentTenantService            tenant,
        IBackgroundJobClient             jobs,
        PeppolBisValidator               peppolValidator,
        IEnumerable<IInvoiceTransformService> transformers,
        ILogger<InvoiceController>       logger)
    {
        _invoices        = invoices;
        _tenant          = tenant;
        _jobs            = jobs;
        _peppolValidator = peppolValidator;
        // Resolve the Peppol generator from the transformer set so its
        // (optionally configured) party options are honoured rather than
        // newing an empty instance here.
        _peppolGenerator = transformers.OfType<PeppolBisInvoiceTransformService>().Single();
        _logger          = logger;
    }

    // POST /api/invoices/upload
    [HttpPost("upload")]
    [RequestSizeLimit(10 * 1024 * 1024)]
    public async Task<IActionResult> Upload(IFormFile file, [FromQuery] Guid? supplierId, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return BadRequest(new { error = "No file provided." });

        var allowedExtensions = new[] { ".xml", ".edi" };
        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!allowedExtensions.Contains(ext))
            return BadRequest(new { error = $"Unsupported file type '{ext}'. Supported: {string.Join(", ", allowedExtensions)}" });

        var orgId = _tenant.OrganisationId;

        await using var stream = file.OpenReadStream();
        var invoice = await _invoices.CreateStubAsync(orgId, supplierId, stream, file.FileName, file.ContentType, ct);

        ParseInvoiceJob.Enqueue(_jobs, invoice.Id, orgId);

        _logger.LogInformation("Invoice {InvoiceId} stub created, parse job enqueued.", invoice.Id);

        return Ok(new
        {
            invoice.Id,
            invoice.Status,
            invoice.SourceFileName,
            invoice.CreatedAt,
        });
    }

    // GET /api/invoices
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var orgId    = _tenant.OrganisationId;
        var invoices = await _invoices.ListAsync(orgId, ct);
        return Ok(invoices.Select(i => new
        {
            i.Id, i.InvoiceNumber, i.Status, i.IssueDate, i.DueDate,
            i.Currency, i.GrandTotal, i.SourceFileName, i.CreatedAt,
        }));
    }

    // GET /api/invoices/{id}
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
    {
        var orgId   = _tenant.OrganisationId;
        var invoice = await _invoices.GetAsync(orgId, id, ct);
        if (invoice is null) return NotFound();
        return Ok(invoice);
    }

    // POST /api/invoices/{id}/approve
    [HttpPost("{id:guid}/approve")]
    public async Task<IActionResult> Approve(Guid id, CancellationToken ct)
    {
        var orgId = _tenant.OrganisationId;
        try
        {
            var invoice = await _invoices.ApproveAsync(orgId, id, ct);
            return Ok(new { invoice.Id, invoice.Status });
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    // GET /api/invoices/{id}/download?format=csv|xml|json
    [HttpGet("{id:guid}/download")]
    [EnableRateLimiting("signed-url")]
    public async Task<IActionResult> Download(Guid id, [FromQuery] string format = "csv", CancellationToken ct = default)
    {
        var orgId = _tenant.OrganisationId;
        try
        {
            var (bytes, contentType, ext) = await _invoices.ForwardAsync(orgId, id, format, ct);
            return File(bytes, contentType, $"invoice-{id}{ext}");
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    // GET /api/invoices/{id}/validate-peppol
    //
    // Peppol wedge — Track A. Generates the BIS Billing 3.0 UBL for this invoice
    // IN MEMORY (no status mutation, nothing delivered) and runs the lightweight
    // mandatory-field validator over it. Returns whether the document clears the
    // high-value BIS mandatory rules plus the full issue list.
    //
    // HONEST SCOPE: this is NOT full Schematron / EN 16931 conformance — see
    // PeppolBisValidator. AS4 / Access-Point TRANSPORT is out of scope (Track B).
    [HttpGet("{id:guid}/validate-peppol")]
    public async Task<IActionResult> ValidatePeppol(Guid id, CancellationToken ct)
    {
        var orgId   = _tenant.OrganisationId;
        var invoice = await _invoices.GetAsync(orgId, id, ct);
        if (invoice is null) return NotFound();

        var doc    = _peppolGenerator.BuildDocument(invoice, invoice.Lines);
        var result = _peppolValidator.Validate(doc);

        return Ok(new
        {
            invoiceId = invoice.Id,
            customizationId = PeppolBisInvoiceTransformService.CustomizationId,
            profileId       = PeppolBisInvoiceTransformService.ProfileId,
            isValid = result.IsValid,
            errorCount   = result.Errors.Count(),
            warningCount = result.Warnings.Count(),
            issues = result.Issues.Select(i => new
            {
                i.RuleId,
                i.BusinessTerm,
                i.Severity,
                i.Message,
            }),
            note = "Lightweight BIS Billing 3.0 mandatory-field check — not full Schematron/EN 16931 conformance, and excludes AS4/Access-Point transport.",
        });
    }
}
