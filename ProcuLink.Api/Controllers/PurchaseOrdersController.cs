using System.Globalization;
using System.Text;
using System.Xml.Linq;
using ClosedXML.Excel;
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.AspNetCore.Mvc;
using ProcuLink.Api.Contracts;
using ProcuLink.Core.Canonical;
using ProcuLink.Infrastructure.Repositories;

namespace ProcuLink.Api.Controllers;

[ApiController]
[Route("api/purchase-orders")]
public class PurchaseOrdersController : ControllerBase
{
    private readonly IOrderRepository _orderRepository;
    private readonly ISupplierProfileRepository _supplierProfileRepository;
    private readonly IOutboundRepository _outboundRepository;
    private readonly IItemMappingRepository _itemMappingRepository;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PurchaseOrdersController> _logger;

    public PurchaseOrdersController(
        IOrderRepository orderRepository,
        ISupplierProfileRepository supplierProfileRepository,
        IOutboundRepository outboundRepository,
        IItemMappingRepository itemMappingRepository,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<PurchaseOrdersController> logger)
    {
        _orderRepository = orderRepository;
        _supplierProfileRepository = supplierProfileRepository;
        _outboundRepository = outboundRepository;
        _itemMappingRepository = itemMappingRepository;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Upload a CSV or XLSX purchase order file
    /// </summary>
    [HttpPost("upload")]
    [Consumes("multipart/form-data")]
    [ProducesResponseType(typeof(UploadResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Upload(
        IFormFile file,
        [FromForm] string supplierName,
        [FromForm] string? buyerName = null,
        [FromForm] string? currency = null,
        CancellationToken ct = default)
    {
        if (file == null || file.Length == 0)
            return BadRequest("File is required.");

        if (string.IsNullOrWhiteSpace(supplierName))
            return BadRequest("Supplier name is required.");

        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (extension != ".csv" && extension != ".xlsx")
            return BadRequest("Only CSV and XLSX files are supported.");

        try
        {
            List<RawOrderLine> rawLines;

            await using var stream = file.OpenReadStream();
            if (extension == ".csv")
            {
                rawLines = ParseCsv(stream);
            }
            else
            {
                rawLines = ParseXlsx(stream);
            }

            if (rawLines.Count == 0)
                return BadRequest("File must contain at least one line item.");

            var po = BuildPurchaseOrder(rawLines, supplierName, buyerName, currency);
            po.CreatedAt = DateTime.UtcNow;

            // Validate basic structure
            var validationErrors = ValidatePurchaseOrder(po);
            if (validationErrors.Count > 0)
                return BadRequest(new { Errors = validationErrors });

            // Apply saved mappings to fill in missing supplier item codes
            var validationMessages = new List<string>();
            var autoFilledLines = await ApplyMappingsAsync(po, ct);
            if (autoFilledLines.Count > 0)
            {
                var lineNumbers = string.Join(",", autoFilledLines);
                validationMessages.Add($"Auto-filled SupplierItemCode for line(s): {lineNumbers} using saved mappings.");
            }

            // Load supplier profile and determine automation status
            var profile = await _supplierProfileRepository.GetByNameAsync(supplierName, ct);
            DetermineAutomationStatus(po, profile, validationMessages);

            // Persist
            await _orderRepository.SaveAsync(po, ct);

            _logger.LogInformation("Purchase order {PoNumber} uploaded with ID {Id}, status: {Status}",
                po.PoNumber, po.Id, po.AutomationStatus);

            return Ok(new UploadResultDto
            {
                Order = po,
                ValidationMessages = validationMessages
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing upload");
            return BadRequest($"Error processing file: {ex.Message}");
        }
    }

    /// <summary>
    /// Get a purchase order by ID
    /// </summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(PurchaseOrder), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var po = await _orderRepository.GetAsync(id, ct);
        if (po == null)
            return NotFound();

        return Ok(po);
    }

    /// <summary>
    /// List all purchase orders (summary view)
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<PurchaseOrderSummaryDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var orders = await _orderRepository.ListAsync(ct);
        var summaries = new List<PurchaseOrderSummaryDto>();

        foreach (var po in orders)
        {
            var artifact = await _outboundRepository.GetArtifactAsync(po.Id, ct);
            var delivery = await _outboundRepository.GetDeliveryRecordAsync(po.Id, ct);

            summaries.Add(new PurchaseOrderSummaryDto
            {
                Id = po.Id,
                PoNumber = po.PoNumber,
                SupplierName = po.SupplierName,
                BuyerName = po.BuyerName,
                OrderDate = po.OrderDate,
                AutomationStatus = po.AutomationStatus,
                CreatedAt = po.CreatedAt,
                LineCount = po.Lines.Count,
                TotalValue = po.Lines.Sum(l => l.Quantity * l.UnitPrice),
                Currency = po.Currency,
                HasOutboundArtifact = artifact != null,
                LastDeliveryStatus = delivery?.Status
            });
        }

        return Ok(summaries);
    }

    /// <summary>
    /// Resolve missing supplier item codes on a purchase order
    /// </summary>
    [HttpPost("{id:guid}/resolve")]
    [ProducesResponseType(typeof(UploadResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Resolve(Guid id, [FromBody] ResolveRequest request, CancellationToken ct)
    {
        var po = await _orderRepository.GetAsync(id, ct);
        if (po == null)
            return NotFound();

        // Validate request
        if (request.LineResolutions == null || request.LineResolutions.Count == 0)
            return BadRequest("At least one line resolution is required.");

        foreach (var resolution in request.LineResolutions)
        {
            if (string.IsNullOrWhiteSpace(resolution.SupplierItemCode))
                return BadRequest($"SupplierItemCode is required for line {resolution.LineNumber}.");

            var line = po.Lines.FirstOrDefault(l => l.LineNumber == resolution.LineNumber);
            if (line == null)
                return BadRequest($"Line number {resolution.LineNumber} does not exist in this order.");
        }

        // Apply resolutions
        foreach (var resolution in request.LineResolutions)
        {
            var line = po.Lines.First(l => l.LineNumber == resolution.LineNumber);
            line.SupplierItemCode = resolution.SupplierItemCode;

            // Save mapping if requested
            if (request.SaveMappings && !string.IsNullOrWhiteSpace(line.BuyerItemCode))
            {
                await _itemMappingRepository.UpsertAsync(
                    po.SupplierName,
                    line.BuyerItemCode,
                    resolution.SupplierItemCode,
                    ct);
            }
        }

        // Re-run validation
        var validationMessages = new List<string>();
        var profile = await _supplierProfileRepository.GetByNameAsync(po.SupplierName, ct);
        DetermineAutomationStatus(po, profile, validationMessages);

        // Save updated order
        await _orderRepository.SaveAsync(po, ct);

        _logger.LogInformation("Purchase order {Id} resolved, new status: {Status}", id, po.AutomationStatus);

        return Ok(new UploadResultDto
        {
            Order = po,
            ValidationMessages = validationMessages
        });
    }

    /// <summary>
    /// Get lines missing required fields (SupplierItemCode) for a purchase order
    /// </summary>
    [HttpGet("{id:guid}/missing")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetMissing(Guid id, CancellationToken ct)
    {
        var po = await _orderRepository.GetAsync(id, ct);
        if (po == null)
            return NotFound();

        var profile = await _supplierProfileRepository.GetByNameAsync(po.SupplierName, ct);

        var missingSupplierItemCodeLines = new List<int>();

        // Only check for missing codes if supplier requires them
        if (profile?.RequiresSupplierItemCode == true)
        {
            missingSupplierItemCodeLines = po.Lines
                .Where(l => string.IsNullOrWhiteSpace(l.SupplierItemCode))
                .Select(l => l.LineNumber)
                .ToList();
        }

        return Ok(new
        {
            OrderId = id,
            MissingSupplierItemCodeLines = missingSupplierItemCodeLines
        });
    }

    /// <summary>
    /// Transform a purchase order to outbound format
    /// </summary>
    [HttpPost("{id:guid}/transform")]
    [ProducesResponseType(typeof(OutboundArtifact), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Transform(Guid id, [FromQuery] string format = "xml", CancellationToken ct = default)
    {
        var po = await _orderRepository.GetAsync(id, ct);
        if (po == null)
            return NotFound();

        if (po.AutomationStatus != AutomationStatus.Automatable)
        {
            return BadRequest(new
            {
                Message = "Order is not automatable. Resolve clarification issues before transforming.",
                AutomationReason = po.AutomationReason,
                Order = po
            });
        }

        format = format.ToLowerInvariant();
        if (format != "xml" && format != "csv")
            return BadRequest("Format must be 'xml' or 'csv'.");

        byte[] content;
        string fileName;

        if (format == "xml")
        {
            content = GenerateXml(po);
            fileName = $"{po.SupplierName.Replace(" ", "_")}.xml";
        }
        else
        {
            content = GenerateCsv(po);
            fileName = $"{po.SupplierName.Replace(" ", "_")}.csv";
        }

        var artifact = new OutboundArtifact
        {
            OrderId = po.Id,
            SupplierName = po.SupplierName,
            Format = format,
            FileName = fileName,
            OutputPath = $"data/outbound/{po.Id}/{fileName}",
            SizeBytes = content.Length,
            CreatedAtUtc = DateTime.UtcNow
        };

        await _outboundRepository.SaveArtifactAsync(artifact, content, ct);

        _logger.LogInformation("Generated {Format} outbound for order {OrderId}", format, id);

        return Ok(artifact);
    }

    /// <summary>
    /// Get outbound artifact metadata
    /// </summary>
    [HttpGet("{id:guid}/outbound")]
    [ProducesResponseType(typeof(OutboundArtifact), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetOutbound(Guid id, CancellationToken ct)
    {
        var artifact = await _outboundRepository.GetArtifactAsync(id, ct);
        if (artifact == null)
            return NotFound();

        return Ok(artifact);
    }

    /// <summary>
    /// Download the outbound artifact file
    /// </summary>
    [HttpGet("{id:guid}/outbound/download")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DownloadOutbound(Guid id, CancellationToken ct)
    {
        var artifact = await _outboundRepository.GetArtifactAsync(id, ct);
        if (artifact == null)
            return NotFound();

        var content = await _outboundRepository.GetArtifactContentAsync(id, ct);
        if (content == null)
            return NotFound();

        var contentType = artifact.Format == "xml" ? "application/xml" : "text/csv";
        return File(content, contentType, artifact.FileName);
    }

    /// <summary>
    /// Send order to supplier via webhook
    /// </summary>
    [HttpPost("{id:guid}/send")]
    [ProducesResponseType(typeof(DeliveryRecord), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Send(Guid id, CancellationToken ct)
    {
        var po = await _orderRepository.GetAsync(id, ct);
        if (po == null)
            return NotFound();

        var artifact = await _outboundRepository.GetArtifactAsync(id, ct);
        if (artifact == null)
            return BadRequest("Transform required before send. No outbound artifact exists.");

        var webhookUrl = _configuration["Delivery:WebhookUrl"];
        if (string.IsNullOrWhiteSpace(webhookUrl))
            return BadRequest("Webhook URL is not configured. Set Delivery:WebhookUrl in appsettings.");

        var content = await _outboundRepository.GetArtifactContentAsync(id, ct);
        if (content == null)
            return BadRequest("Outbound artifact file not found.");

        var record = new DeliveryRecord
        {
            OrderId = id,
            AttemptedAtUtc = DateTime.UtcNow,
            WebhookUrl = webhookUrl
        };

        try
        {
            using var client = _httpClientFactory.CreateClient();
            using var formData = new MultipartFormDataContent();

            formData.Add(new StringContent(id.ToString()), "orderId");
            formData.Add(new StringContent(po.SupplierName), "supplierName");
            formData.Add(new StringContent(po.PoNumber), "poNumber");

            var fileContent = new ByteArrayContent(content);
            fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
                artifact.Format == "xml" ? "application/xml" : "text/csv");
            formData.Add(fileContent, "file", artifact.FileName);

            var response = await client.PostAsync(webhookUrl, formData, ct);

            record.HttpStatusCode = (int)response.StatusCode;
            record.Status = response.IsSuccessStatusCode ? "Sent" : "Failed";

            var responseBody = await response.Content.ReadAsStringAsync(ct);
            record.ResponseSnippet = responseBody.Length > 2000 ? responseBody[..2000] : responseBody;

            if (!response.IsSuccessStatusCode)
            {
                record.ErrorMessage = $"HTTP {record.HttpStatusCode}: {response.ReasonPhrase}";
            }
        }
        catch (Exception ex)
        {
            record.Status = "Failed";
            record.ErrorMessage = ex.Message;
            _logger.LogError(ex, "Failed to send order {OrderId} to webhook", id);
        }

        await _outboundRepository.SaveDeliveryRecordAsync(record, ct);

        _logger.LogInformation("Delivery attempt for order {OrderId}: {Status}", id, record.Status);

        return Ok(record);
    }

    /// <summary>
    /// Apply saved mappings to fill in missing supplier item codes
    /// Returns list of line numbers that were auto-filled
    /// </summary>
    private async Task<List<int>> ApplyMappingsAsync(PurchaseOrder po, CancellationToken ct)
    {
        var autoFilledLines = new List<int>();

        foreach (var line in po.Lines)
        {
            if (string.IsNullOrWhiteSpace(line.SupplierItemCode) && !string.IsNullOrWhiteSpace(line.BuyerItemCode))
            {
                var mappedCode = await _itemMappingRepository.TryGetSupplierItemCodeAsync(
                    po.SupplierName,
                    line.BuyerItemCode,
                    ct);

                if (!string.IsNullOrWhiteSpace(mappedCode))
                {
                    line.SupplierItemCode = mappedCode;
                    autoFilledLines.Add(line.LineNumber);
                    _logger.LogDebug("Applied mapping for line {LineNumber}: {BuyerItemCode} -> {SupplierItemCode}",
                        line.LineNumber, line.BuyerItemCode, mappedCode);
                }
            }
        }

        return autoFilledLines;
    }

    private static byte[] GenerateXml(PurchaseOrder po)
    {
        var doc = new XDocument(
            new XElement("PurchaseOrder",
                new XElement("Header",
                    new XElement("BuyerName", po.BuyerName),
                    new XElement("SupplierName", po.SupplierName),
                    new XElement("PoNumber", po.PoNumber),
                    new XElement("OrderDate", po.OrderDate.ToString("yyyy-MM-dd")),
                    new XElement("Currency", po.Currency)
                ),
                new XElement("Lines",
                    po.Lines.Select(line =>
                        new XElement("Line",
                            new XElement("LineNumber", line.LineNumber),
                            new XElement("BuyerItemCode", line.BuyerItemCode),
                            new XElement("SupplierItemCode", line.SupplierItemCode ?? ""),
                            new XElement("Description", line.Description),
                            new XElement("Quantity", line.Quantity),
                            new XElement("UnitPrice", line.UnitPrice)
                        )
                    )
                )
            )
        );

        return Encoding.UTF8.GetBytes(doc.ToString());
    }

    private static byte[] GenerateCsv(PurchaseOrder po)
    {
        var sb = new StringBuilder();
        sb.AppendLine("LineNumber,BuyerItemCode,SupplierItemCode,Description,Quantity,UnitPrice");

        foreach (var line in po.Lines)
        {
            sb.AppendLine($"{line.LineNumber},{EscapeCsv(line.BuyerItemCode)},{EscapeCsv(line.SupplierItemCode ?? "")},{EscapeCsv(line.Description)},{line.Quantity},{line.UnitPrice}");
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static string EscapeCsv(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
        {
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }
        return value;
    }

    private static List<RawOrderLine> ParseCsv(Stream stream)
    {
        using var reader = new StreamReader(stream);
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HeaderValidated = null!,
            MissingFieldFound = null!,
            PrepareHeaderForMatch = args => args.Header?.ToLowerInvariant().Trim() ?? string.Empty
        };

        using var csv = new CsvReader(reader, config);
        csv.Context.RegisterClassMap<RawOrderLineMap>();
        return csv.GetRecords<RawOrderLine>().ToList();
    }

    private static List<RawOrderLine> ParseXlsx(Stream stream)
    {
        var lines = new List<RawOrderLine>();

        using var workbook = new XLWorkbook(stream);
        var worksheet = workbook.Worksheets.First();
        var rows = worksheet.RangeUsed()?.RowsUsed().ToList();

        if (rows == null || rows.Count < 2)
            return lines;

        var headerRow = rows[0];
        var headerMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var cell in headerRow.Cells())
        {
            var headerName = cell.GetString().Trim();
            if (!string.IsNullOrEmpty(headerName))
                headerMap[headerName] = cell.Address.ColumnNumber;
        }

        for (int i = 1; i < rows.Count; i++)
        {
            var row = rows[i];
            var line = new RawOrderLine
            {
                PoNumber = GetCellValue(row, headerMap, "PoNumber"),
                OrderDate = GetCellValue(row, headerMap, "OrderDate"),
                Currency = GetCellValue(row, headerMap, "Currency"),
                BuyerName = GetCellValue(row, headerMap, "BuyerName"),
                SupplierName = GetCellValue(row, headerMap, "SupplierName"),
                LineNumber = GetCellValue(row, headerMap, "LineNumber"),
                BuyerItemCode = GetCellValue(row, headerMap, "BuyerItemCode"),
                SupplierItemCode = GetCellValue(row, headerMap, "SupplierItemCode"),
                Description = GetCellValue(row, headerMap, "Description"),
                Quantity = GetCellValue(row, headerMap, "Quantity"),
                UnitPrice = GetCellValue(row, headerMap, "UnitPrice")
            };
            lines.Add(line);
        }

        return lines;
    }

    private static string? GetCellValue(IXLRangeRow row, Dictionary<string, int> headerMap, string columnName)
    {
        if (!headerMap.TryGetValue(columnName, out var colIndex))
            return null;

        var cell = row.Cell(colIndex - row.FirstCell().Address.ColumnNumber + 1);
        var value = cell.GetString().Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static PurchaseOrder BuildPurchaseOrder(
        List<RawOrderLine> rawLines,
        string supplierNameParam,
        string? buyerNameParam,
        string? currencyParam)
    {
        var po = new PurchaseOrder { Id = Guid.NewGuid() };

        po.PoNumber = rawLines.Select(l => l.PoNumber).FirstOrDefault(v => !string.IsNullOrEmpty(v))
                      ?? $"PO-{DateTime.UtcNow:yyyyMMddHHmmss}";

        var orderDateStr = rawLines.Select(l => l.OrderDate).FirstOrDefault(v => !string.IsNullOrEmpty(v));
        po.OrderDate = ParseDateTime(orderDateStr) ?? DateTime.UtcNow;

        po.Currency = currencyParam
                      ?? rawLines.Select(l => l.Currency).FirstOrDefault(v => !string.IsNullOrEmpty(v))
                      ?? "EUR";

        po.SupplierName = supplierNameParam;

        po.BuyerName = !string.IsNullOrEmpty(buyerNameParam)
            ? buyerNameParam
            : rawLines.Select(l => l.BuyerName).FirstOrDefault(v => !string.IsNullOrEmpty(v)) ?? string.Empty;

        int lineNum = 1;
        foreach (var raw in rawLines)
        {
            var line = new PurchaseOrderLine
            {
                LineNumber = int.TryParse(raw.LineNumber, out var ln) ? ln : lineNum++,
                BuyerItemCode = raw.BuyerItemCode ?? string.Empty,
                SupplierItemCode = string.IsNullOrWhiteSpace(raw.SupplierItemCode) ? null : raw.SupplierItemCode,
                Description = raw.Description ?? string.Empty,
                Quantity = decimal.TryParse(raw.Quantity, NumberStyles.Any, CultureInfo.InvariantCulture, out var qty) ? qty : 0,
                UnitPrice = decimal.TryParse(raw.UnitPrice, NumberStyles.Any, CultureInfo.InvariantCulture, out var price) ? price : 0
            };
            po.Lines.Add(line);
        }

        return po;
    }

    private static DateTime? ParseDateTime(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return null;

        string[] formats = { "yyyy-MM-dd", "MM/dd/yyyy", "dd/MM/yyyy", "yyyy-MM-ddTHH:mm:ss", "M/d/yyyy" };
        if (DateTime.TryParseExact(value, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            return dt;

        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out dt))
            return dt;

        return null;
    }

    private static List<string> ValidatePurchaseOrder(PurchaseOrder po)
    {
        var errors = new List<string>();

        if (po.Lines.Count == 0)
            errors.Add("Purchase order must have at least one line item.");

        foreach (var line in po.Lines)
        {
            if (line.Quantity < 0)
                errors.Add($"Line {line.LineNumber}: Quantity must be >= 0.");
            if (line.UnitPrice < 0)
                errors.Add($"Line {line.LineNumber}: UnitPrice must be >= 0.");
        }

        return errors;
    }

    private static void DetermineAutomationStatus(PurchaseOrder po, SupplierProfile? profile, List<string> validationMessages)
    {
        // If no profile configured, mark as needs clarification
        if (profile == null)
        {
            po.AutomationStatus = AutomationStatus.NeedsClarification;
            po.AutomationReason = $"No supplier profile configured for supplier '{po.SupplierName}'.";
            validationMessages.Add(po.AutomationReason);
            return;
        }

        // Check if supplier requires item codes
        if (profile.RequiresSupplierItemCode)
        {
            var linesWithMissingCodes = po.Lines
                .Where(l => string.IsNullOrWhiteSpace(l.SupplierItemCode))
                .Select(l => l.LineNumber)
                .ToList();

            if (linesWithMissingCodes.Count > 0)
            {
                po.AutomationStatus = AutomationStatus.NeedsClarification;
                var lineNumbers = string.Join(",", linesWithMissingCodes);
                po.AutomationReason = $"Supplier requires supplier item codes. Missing on line(s): {lineNumbers}";
                validationMessages.Add($"Missing SupplierItemCode on line(s): {lineNumbers}. Supplier requires this field for automated processing.");
                return;
            }
        }

        // All checks passed
        po.AutomationStatus = AutomationStatus.Automatable;
        po.AutomationReason = null;
        validationMessages.Add("All validation checks passed. Order is ready for automated processing.");
    }

    private class RawOrderLine
    {
        public string? PoNumber { get; set; }
        public string? OrderDate { get; set; }
        public string? Currency { get; set; }
        public string? BuyerName { get; set; }
        public string? SupplierName { get; set; }
        public string? LineNumber { get; set; }
        public string? BuyerItemCode { get; set; }
        public string? SupplierItemCode { get; set; }
        public string? Description { get; set; }
        public string? Quantity { get; set; }
        public string? UnitPrice { get; set; }
    }

    private sealed class RawOrderLineMap : ClassMap<RawOrderLine>
    {
        public RawOrderLineMap()
        {
            Map(m => m.PoNumber).Name("ponumber");
            Map(m => m.OrderDate).Name("orderdate");
            Map(m => m.Currency).Name("currency");
            Map(m => m.BuyerName).Name("buyername");
            Map(m => m.SupplierName).Name("suppliername");
            Map(m => m.LineNumber).Name("linenumber");
            Map(m => m.BuyerItemCode).Name("buyeritemcode");
            Map(m => m.SupplierItemCode).Name("supplieritemcode");
            Map(m => m.Description).Name("description");
            Map(m => m.Quantity).Name("quantity");
            Map(m => m.UnitPrice).Name("unitprice");
        }
    }
}
