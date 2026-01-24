using System.Globalization;
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
    private readonly ILogger<PurchaseOrdersController> _logger;

    public PurchaseOrdersController(IOrderRepository orderRepository, ILogger<PurchaseOrdersController> logger)
    {
        _orderRepository = orderRepository;
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

            // Set CreatedAt timestamp
            po.CreatedAt = DateTime.UtcNow;

            // Validate
            var validationErrors = ValidatePurchaseOrder(po);
            if (validationErrors.Count > 0)
                return BadRequest(new { Errors = validationErrors });

            // Determine automation status and collect validation messages
            var validationMessages = new List<string>();
            DetermineAutomationStatus(po, validationMessages);

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
        var summaries = orders.Select(po => new PurchaseOrderSummaryDto
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
            Currency = po.Currency
        }).ToList();
        return Ok(summaries);
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

        // Build header map (case-insensitive)
        var headerRow = rows[0];
        var headerMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var cell in headerRow.Cells())
        {
            var headerName = cell.GetString().Trim();
            if (!string.IsNullOrEmpty(headerName))
                headerMap[headerName] = cell.Address.ColumnNumber;
        }

        // Parse data rows
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
        var po = new PurchaseOrder
        {
            Id = Guid.NewGuid()
        };

        // Extract header fields from first non-empty values
        po.PoNumber = rawLines.Select(l => l.PoNumber).FirstOrDefault(v => !string.IsNullOrEmpty(v))
                      ?? $"PO-{DateTime.UtcNow:yyyyMMddHHmmss}";

        var orderDateStr = rawLines.Select(l => l.OrderDate).FirstOrDefault(v => !string.IsNullOrEmpty(v));
        po.OrderDate = ParseDateTime(orderDateStr) ?? DateTime.UtcNow;

        po.Currency = currencyParam
                      ?? rawLines.Select(l => l.Currency).FirstOrDefault(v => !string.IsNullOrEmpty(v))
                      ?? "EUR";

        po.SupplierName = !string.IsNullOrEmpty(supplierNameParam)
            ? supplierNameParam
            : rawLines.Select(l => l.SupplierName).FirstOrDefault(v => !string.IsNullOrEmpty(v)) ?? string.Empty;

        po.BuyerName = !string.IsNullOrEmpty(buyerNameParam)
            ? buyerNameParam
            : rawLines.Select(l => l.BuyerName).FirstOrDefault(v => !string.IsNullOrEmpty(v)) ?? string.Empty;

        // Build line items
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

    private static void DetermineAutomationStatus(PurchaseOrder po, List<string> validationMessages)
    {
        var linesWithMissingCodes = po.Lines
            .Where(l => string.IsNullOrWhiteSpace(l.SupplierItemCode))
            .Select(l => l.LineNumber)
            .ToList();

        if (linesWithMissingCodes.Count > 0)
        {
            po.AutomationStatus = AutomationStatus.NeedsClarification;
            var lineNumbers = string.Join(",", linesWithMissingCodes);
            po.AutomationReason = $"Missing supplier item codes for {linesWithMissingCodes.Count} line item(s): lines {lineNumbers}. Supplier requires all item codes for automated processing.";
            validationMessages.Add(po.AutomationReason);
        }
        else
        {
            po.AutomationStatus = AutomationStatus.Automatable;
            po.AutomationReason = null;
            validationMessages.Add("All validation checks passed. Order is ready for automated processing.");
        }
    }

    // Internal class for raw CSV/XLSX parsing
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
