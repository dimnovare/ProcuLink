using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ProcuLink.Core.Services.Catalog;

namespace ProcuLink.Infrastructure.Services.Catalog;

/// <summary>
/// Vendor fetcher for Logicom QuickConnect (plan 2026-07-02 D4/6.4). Logicom's 2FA is exotic
/// enough to warrant a dedicated fetcher instead of the generic http auth path.
///
/// Auth, empirically confirmed by the Phase 0 probe (P0.5):
///  • AES-256-CBC, key = the AccessTokenKey as raw UTF-8 (exactly 32 bytes), IV = 16 zero bytes,
///    PKCS7 padding, then Base64.
///  • GenerateAccessToken (GET) headers: CustomerID, Timestamp (epoch seconds),
///    BCode = base64(AES("ConsumerKey;ConsumerSecret")),
///    GenerateSignature = base64(AES("ConsumerKey"+CustomerID+timestamp+";"+"ConsumerSecret")).
///    Returns an access token with a 60-second TTL.
///  • GetProducts (GET ?PreviousItemNo={n}&Currency={ccy}) headers: Authorization = token,
///    Timestamp, CustomerID, Signature = base64(base64(AES(token+timestamp))) — the double Base64
///    is the vendor's v1.2.1 "Signature clarification". Returns 100 items/page; follow NextItemNo.
///
/// The response items nest <c>Price.PriceExclVAT</c> / <c>Price.Currency</c>, which the flat JSON
/// parser can't map, so we PROJECT each item to a flat {SKU, Name, Barcode, Manufacturer,
/// PriceExclVAT, Currency} object and serialize the whole run as one JSON array — the standard
/// JSON parser + per-source column mapping does the rest, and the array is deterministic for
/// identical data so hash-skip works. All requests go through the SSRF-guarded client. Errors are
/// enumerated safe <see cref="CatalogSyncException"/> messages (no token/secret leakage).
/// </summary>
public sealed class LogicomQuickConnectFetcher : ICatalogVendorFetcher
{
    public string Protocol => "logicom";

    /// <summary>Fetched items are flattened to these top-level scalar fields for the JSON parser.</summary>
    private const string OutputFileName = "redacted-fixture";

    /// <summary>Safety cap on pages so a NextItemNo cycle / runaway feed can't loop forever.</summary>
    private const int MaxPages = 5_000; // 100 items/page → 500k items ceiling

    private readonly ILogger<LogicomQuickConnectFetcher> _logger;

    public LogicomQuickConnectFetcher(ILogger<LogicomQuickConnectFetcher> logger) => _logger = logger;

    public async Task<VendorFetchResult> FetchAsync(VendorFetchContext ctx, CancellationToken ct)
    {
        var baseUrl = (ctx.Url ?? string.Empty).TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new CatalogSyncException("The catalog URL is not configured.");

        var cfg = ReadCreds(ctx.Creds);
        var accessTokenKey = Encoding.UTF8.GetBytes(cfg.AccessTokenKey);
        if (accessTokenKey.Length != 32)
            throw new CatalogSyncException("The catalog credentials are invalid — the access-token key must be 32 characters.");

        var ms = new MemoryStream();
        var count = 0;
        long approxBytes = 0;
        var previous = "0";
        var pages = 0;
        WriteAscii(ms, "[");

        try
        {
            while (pages < MaxPages)
            {
                ct.ThrowIfCancellationRequested();

                // Fresh token per page — the 60 s TTL makes per-call the simplest correct choice.
                var token = await GetAccessTokenAsync(ctx.Client, baseUrl, cfg, accessTokenKey, ct);
                var page = await GetProductsPageAsync(ctx.Client, baseUrl, cfg, accessTokenKey, token, previous, ct);
                pages++;

                using var doc = JsonDocument.Parse(page);
                var root = doc.RootElement;
                if (root.TryGetProperty("Message", out var message) && message.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in message.EnumerateArray())
                    {
                        var flat = ProjectItem(item);
                        approxBytes += flat.Length + 1;
                        if (approxBytes > ctx.MaxBytes)
                            throw new CatalogSyncException("The Logicom catalog is larger than the size limit.");
                        if (count > 0) WriteAscii(ms, ",");
                        var bytes = Encoding.UTF8.GetBytes(flat);
                        ms.Write(bytes, 0, bytes.Length);
                        count++;
                    }
                }

                var next = root.TryGetProperty("NextItemNo", out var n) && n.ValueKind == JsonValueKind.String
                    ? n.GetString()
                    : null;
                if (string.IsNullOrWhiteSpace(next)) break;
                previous = next!;
            }
        }
        catch (CatalogSyncException) { throw; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (HttpRequestException) { throw new CatalogSyncException("Could not connect to the Logicom catalog service."); }
        catch (JsonException) { throw new CatalogSyncException("The Logicom catalog service returned an unexpected response."); }
        catch (CryptographicException) { throw new CatalogSyncException("The catalog credentials could not be used to sign the request."); }

        if (pages >= MaxPages)
            _logger.LogWarning("Logicom fetch hit the {MaxPages}-page cap — catalog may be truncated.", MaxPages);

        WriteAscii(ms, "]");
        ms.Position = 0;
        return new VendorFetchResult(ms, OutputFileName, "application/json");
    }

    // ── request builders ───────────────────────────────────────────────────────

    private static async Task<string> GetAccessTokenAsync(
        HttpClient client, string baseUrl, LogicomCreds cfg, byte[] key, CancellationToken ct)
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var bcode = EncryptBase64(key, $"{cfg.ConsumerKey};{cfg.ConsumerSecret}");
        var gensig = EncryptBase64(key, $"{cfg.ConsumerKey}{cfg.CustomerId}{ts};{cfg.ConsumerSecret}");

        using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/GenerateAccessToken");
        req.Headers.TryAddWithoutValidation("CustomerID", cfg.CustomerId);
        req.Headers.TryAddWithoutValidation("Timestamp", ts.ToString());
        req.Headers.TryAddWithoutValidation("BCode", bcode);
        req.Headers.TryAddWithoutValidation("GenerateSignature", gensig);

        using var res = await client.SendAsync(req, ct);
        if (res.StatusCode == HttpStatusCode.Unauthorized || res.StatusCode == HttpStatusCode.Forbidden)
            throw new CatalogSyncException("Logicom rejected the credentials.");
        if (!res.IsSuccessStatusCode)
            throw new CatalogSyncException("The Logicom catalog service returned an error while signing in.");

        var body = (await res.Content.ReadAsStringAsync(ct)).Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(body) || body.StartsWith("{", StringComparison.Ordinal))
            throw new CatalogSyncException("Logicom rejected the credentials.");
        return body;
    }

    private static async Task<string> GetProductsPageAsync(
        HttpClient client, string baseUrl, LogicomCreds cfg, byte[] key, string token, string previous, CancellationToken ct)
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        // Signature = base64(base64(AES(token + timestamp))) — double Base64 per v1.2.1.
        var inner = EncryptBase64(key, $"{token}{ts}");
        var signature = Convert.ToBase64String(Encoding.UTF8.GetBytes(inner));

        var url = $"{baseUrl}/GetProducts/?PreviousItemNo={Uri.EscapeDataString(previous)}&Currency={Uri.EscapeDataString(cfg.Currency)}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("Authorization", token);
        req.Headers.TryAddWithoutValidation("Timestamp", ts.ToString());
        req.Headers.TryAddWithoutValidation("Signature", signature);
        req.Headers.TryAddWithoutValidation("CustomerID", cfg.CustomerId);

        using var res = await client.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode)
            throw new CatalogSyncException("The Logicom catalog service returned an error.");
        return await res.Content.ReadAsStringAsync(ct);
    }

    private static void WriteAscii(Stream s, string text)
    {
        var b = Encoding.ASCII.GetBytes(text);
        s.Write(b, 0, b.Length);
    }

    /// <summary>AES-256-CBC(key, IV=0¹⁶, PKCS7) of the UTF-8 plaintext, Base64-encoded.</summary>
    private static string EncryptBase64(byte[] key, string plaintext)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = new byte[16];
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        using var enc = aes.CreateEncryptor();
        var input = Encoding.UTF8.GetBytes(plaintext);
        var output = enc.TransformFinalBlock(input, 0, input.Length);
        return Convert.ToBase64String(output);
    }

    /// <summary>
    /// Projects a Logicom product to a flat JSON object the shared parser + column mapping can
    /// read: <c>{SKU, Name, Manufacturer, Barcode, PriceExclVAT, Currency}</c>. Nested
    /// <c>Price.*</c> is lifted; arrays (Images/Specifications) are dropped.
    /// </summary>
    private static string ProjectItem(JsonElement item)
    {
        string? Get(string name) =>
            item.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        string? price = null, currency = null;
        if (item.TryGetProperty("Price", out var priceObj) && priceObj.ValueKind == JsonValueKind.Object)
        {
            if (priceObj.TryGetProperty("PriceExclVAT", out var p)) price = p.ValueKind == JsonValueKind.String ? p.GetString() : p.GetRawText();
            if (priceObj.TryGetProperty("Currency", out var c) && c.ValueKind == JsonValueKind.String) currency = c.GetString();
        }

        var flat = new Dictionary<string, string?>
        {
            ["SKU"] = Get("SKU"),
            ["Name"] = Get("Name"),
            ["Manufacturer"] = Get("Manufacturer"),
            ["Barcode"] = Get("Barcode"),
            ["PriceExclVAT"] = price,
            ["Currency"] = currency,
        };
        return JsonSerializer.Serialize(flat);
    }

    private static LogicomCreds ReadCreds(JsonElement creds)
    {
        if (creds.ValueKind != JsonValueKind.Object)
            throw new CatalogSyncException("The catalog credentials are not configured.");

        string? Get(params string[] names)
        {
            foreach (var n in names)
                if (creds.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String)
                    return v.GetString();
            return null;
        }

        var customerId = Get("customerId", "CustomerID");
        var consumerKey = Get("consumerKey", "ConsumerKey");
        var consumerSecret = Get("consumerSecret", "ConsumerSecret");
        var accessTokenKey = Get("accessTokenKey", "AccessTokenKey");
        var currency = Get("currency", "Currency");

        if (string.IsNullOrWhiteSpace(customerId) || string.IsNullOrWhiteSpace(consumerKey)
            || string.IsNullOrWhiteSpace(consumerSecret) || string.IsNullOrWhiteSpace(accessTokenKey))
            throw new CatalogSyncException("The Logicom credentials are incomplete.");

        return new LogicomCreds(customerId!, consumerKey!, consumerSecret!, accessTokenKey!,
                                string.IsNullOrWhiteSpace(currency) ? "EUR" : currency!);
    }

    private sealed record LogicomCreds(
        string CustomerId, string ConsumerKey, string ConsumerSecret, string AccessTokenKey, string Currency);
}
