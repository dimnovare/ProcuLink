using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.Extensions.Logging;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Core.Services.Google;
using ProcuLink.Infrastructure.Services.Google;

namespace ProcuLink.Infrastructure.Services.Dispatchers;

/// <summary>
/// Appends a CSV output's rows to a Google Sheet through a linked account (plan 9b, R11). Values go in
/// RAW so document text never becomes a formula; a first row equal to the tab's row 1 is skipped as a
/// header, anything else is appended (never drops data). An append is not idempotent
/// (ResendSafety.Unsafe). With <c>checkOnly</c> (a test send) nothing is written: it validates the CSV
/// and confirms the sheet and tab are reachable. Never throws.
/// </summary>
public sealed class GoogleSheetsDeliveryDispatcher(
    IHttpClientFactory http, ILinkedAccountTokens tokens, ILogger<GoogleSheetsDeliveryDispatcher> logger) : IDeliveryDispatcher
{
    private const string Api = "https://sheets.googleapis.com/v4/spreadsheets";
    private const string Gone = "ProcuLink can no longer open this spreadsheet. Choose it again.";

    public string Protocol => DeliveryProtocolConstants.GoogleSheets;
    public ResendSafety ResendSafety => ResendSafety.Unsafe;
    public bool CapturesSupplierResponseBody => false;

    public async Task<DeliveryResult> DispatchAsync(
        byte[] content, string fileName, string contentType, SupplierDeliveryConfig config,
        string decryptedCredentials, CancellationToken ct, string? idempotencyKey = null, bool isTestFire = false)
    {
        try
        {
            if (!contentType.StartsWith("text/csv", StringComparison.OrdinalIgnoreCase)
                && !fileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                return Permanent("Google Sheets needs a CSV output. Change this output's format to CSV.");

            var cfg = JsonNode.Parse(config.ConfigJson) as JsonObject;
            var sheetId = Text(cfg, "spreadsheetId");
            if (!Guid.TryParse(Text(cfg, "accountId"), out var accountId) || sheetId is null)
                return Permanent("Google Sheets delivery configuration is invalid: a Google account and a spreadsheet are required.");
            var tab = Text(cfg, "sheetName");
            var checkOnly = cfg?["checkOnly"] is JsonValue c && c.TryGetValue<bool>(out var b) && b;

            var rows = ReadCsv(content);

            var token = await tokens.GetGoogleAccessTokenAsync(config.OrgId, accountId, ct);
            if (token.Failure is { } failure) return failure;

            using var client = http.CreateClient(GoogleOAuthClient.HttpClientName);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(SftpDeliveryDispatcher.DefaultTimeoutSeconds));
            var id = Uri.EscapeDataString(sheetId);

            if (checkOnly)
            {
                using var meta = await SendAsync(client, token.Token!, HttpMethod.Get,
                    $"{Api}/{id}?fields=sheets.properties.title", null, deadline.Token);
                if (!meta.IsSuccessStatusCode) return await Refused(meta, tab, deadline.Token);
                if (tab is not null)
                {
                    var titles = JsonNode.Parse(await meta.Content.ReadAsStringAsync(deadline.Token))?["sheets"]?.AsArray()
                        .Select(s => s?["properties"]?["title"]?.GetValue<string>()) ?? [];
                    if (!titles.Contains(tab, StringComparer.Ordinal)) return Permanent(TabMissing(tab));
                }
                return new DeliveryResult(true, null, 200);
            }

            var rowOne = Uri.EscapeDataString(tab is null ? "1:1" : $"{Quote(tab)}!1:1");
            using var head = await SendAsync(client, token.Token!, HttpMethod.Get, $"{Api}/{id}/values/{rowOne}", null, deadline.Token);
            if (!head.IsSuccessStatusCode) return await Refused(head, tab, deadline.Token);
            var existing = JsonNode.Parse(await head.Content.ReadAsStringAsync(deadline.Token))?["values"]?.AsArray().FirstOrDefault()?.AsArray()
                .Select(v => v?.ToString().Trim() ?? "").ToArray() ?? [];
            if (rows.Count > 0 && existing.Length > 0 && TrimTrailingEmpty(rows[0]).SequenceEqual(TrimTrailingEmpty(existing))) rows.RemoveAt(0);
            if (rows.Count == 0) return new DeliveryResult(true, null, 200);

            var range = Uri.EscapeDataString(tab is null ? "A1" : $"{Quote(tab)}!A1");
            var body = new JsonObject
            {
                ["values"] = new JsonArray(rows.Select(r => (JsonNode)new JsonArray(r.Select(Cell).ToArray())).ToArray()),
            };
            using var append = await SendAsync(client, token.Token!, HttpMethod.Post,
                $"{Api}/{id}/values/{range}:append?valueInputOption=RAW&insertDataOption=INSERT_ROWS",
                new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), deadline.Token);
            return append.IsSuccessStatusCode
                ? new DeliveryResult(true, null, (int)append.StatusCode)
                : await Refused(append, tab, deadline.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // A malformed CSV is permanent; timeouts, DNS and connection faults are transient.
            if (ex is CsvHelperException or DecoderFallbackException) return Permanent("The CSV output could not be read.");
            logger.LogWarning(ex, "Google Sheets delivery failed");
            return new DeliveryResult(false, "Google Sheets could not be reached.");
        }
    }

    private static readonly Regex PlainNumber = new(@"^-?(0|[1-9][0-9]*)(\.[0-9]+)?$", RegexOptions.CultureInvariant);

    /// <summary>A plain decimal of at most 15 significant digits goes as a JSON number so columns sum; anything else (leading zeros, long codes, formulas) stays text.</summary>
    private static JsonNode? Cell(string v)
    {
        var t = v.Trim();
        if (!PlainNumber.IsMatch(t) || t.Replace("-", "").Replace(".", "").TrimStart('0').Length > 15)
            return JsonValue.Create(v);
        return JsonValue.Create(decimal.Parse(t, NumberStyles.Float, CultureInfo.InvariantCulture));
    }

    // Sheets omits trailing empty cells from a row, so compare without them.
    private static string[] TrimTrailingEmpty(IEnumerable<string> cells) =>
        cells.Select(v => v.Trim()).Reverse().SkipWhile(v => v.Length == 0).Reverse().ToArray();

    private static List<string[]> ReadCsv(byte[] content)
    {
        using var reader = new StreamReader(new MemoryStream(content), new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
        using var csv = new CsvParser(reader, new CsvConfiguration(CultureInfo.InvariantCulture) { HasHeaderRecord = false });
        var rows = new List<string[]>();
        while (csv.Read()) rows.Add(csv.Record!);
        return rows;
    }

    private static Task<HttpResponseMessage> SendAsync(
        HttpClient client, string accessToken, HttpMethod method, string url, HttpContent? body, CancellationToken ct)
    {
        var request = new HttpRequestMessage(method, url) { Content = body };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client.SendAsync(request, ct);
    }

    private async Task<DeliveryResult> Refused(HttpResponseMessage response, string? tab, CancellationToken ct)
    {
        var status = (int)response.StatusCode;
        string? message = null;
        try
        {
            message = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))?["error"]?["message"]?.GetValue<string>();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            // An unreadable body only loses the detail.
        }
        logger.LogWarning("Google Sheets delivery refused: HTTP {Status}", status);

        if (status is 404 or 403) return Permanent(Gone, status);
        if (status == 400 && message?.Contains("Unable to parse range", StringComparison.OrdinalIgnoreCase) == true)
            return Permanent(TabMissing(tab ?? ""), status);
        if (status is 408 or 429 or >= 500)
            return new DeliveryResult(false, $"Google Sheets is not accepting rows right now (HTTP {status}).", status,
                RetryAfter: RetryAfterHeader.Read(response.Headers, DateTimeOffset.UtcNow));
        return Permanent($"Google Sheets refused the rows (HTTP {status}).", status);
    }

    private static DeliveryResult Permanent(string message, int? status = null) =>
        new(false, message, status, Outcome: DeliveryOutcome.NotRetryable);

    private static string TabMissing(string tab) => $"The tab “{tab}” was not found in this spreadsheet.";

    private static string Quote(string tab) => $"'{tab.Replace("'", "''")}'";

    private static string? Text(JsonObject? o, string key) =>
        o?[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;
}
