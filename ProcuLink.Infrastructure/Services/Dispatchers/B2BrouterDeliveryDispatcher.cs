using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Delivery;

namespace ProcuLink.Infrastructure.Services.Dispatchers;

/// <summary>
/// Hands an e-invoice file to the customer's own B2Brouter account (plan 35), which delivers it over Peppol or a national network as
/// the sender of record. Config <c>{"account": "…", "sandbox": true, "checkOnly"?: true}</c>; credentials <c>{"apiKey": "…"}</c>.
/// A test (checkOnly, or a test fire) only reads the account. Once the import may have reached B2Brouter nothing is re-sent: a second
/// import could duplicate a legal invoice, so an unconfirmed outcome is a dead letter. Never throws; never puts the key in a message or log.
/// </summary>
public sealed class B2BrouterDeliveryDispatcher(IHttpClientFactory http, ILogger<B2BrouterDeliveryDispatcher> logger) : IDeliveryDispatcher
{
    public const string BaseUrl = "https://api.b2brouter.net";
    public const string ApiVersion = "2026-06-26";
    private const string Unconfirmed = "B2Brouter didn't confirm it received the file. Check your B2Brouter account before sending again, so the invoice isn't sent twice.";
    private static readonly HashSet<string> Failed = ["error", "refused", "invalid"];
    private static readonly HashSet<string> Pending = ["new", "sending"];
    private static readonly HashSet<string> EInvoiceTypes = ["application/xml", "text/xml", "application/pdf"];

    /// <summary>The pause between status reads after an import; tests set it to zero.</summary>
    internal TimeSpan SettleDelay { get; init; } = TimeSpan.FromSeconds(2);

    public string Protocol => DeliveryProtocolConstants.B2Brouter;
    public ResendSafety ResendSafety => ResendSafety.Unsafe;
    public bool CapturesSupplierResponseBody => true;

    public async Task<DeliveryResult> DispatchAsync(
        byte[] content, string fileName, string contentType, SupplierDeliveryConfig config,
        string decryptedCredentials, CancellationToken ct, string? idempotencyKey = null, bool isTestFire = false)
    {
        string? account, apiKey;
        bool checkOnly;
        try
        {
            var cfg = JsonNode.Parse(config.ConfigJson) as JsonObject;
            account = Text(cfg, "account");
            checkOnly = isTestFire || (cfg?["checkOnly"] is JsonValue c && c.TryGetValue<bool>(out var b) && b);
            apiKey = string.IsNullOrEmpty(decryptedCredentials) ? null : Text(JsonNode.Parse(decryptedCredentials) as JsonObject, "apiKey");
        }
        catch (System.Text.Json.JsonException) { account = apiKey = null; checkOnly = false; }
        if (account is null || apiKey is null) return Final("B2Brouter delivery isn't set up: the account and the API key are required.");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(SftpDeliveryDispatcher.DefaultTimeoutSeconds));
        using var client = http.CreateClient("delivery");
        var accountPath = $"{BaseUrl}/accounts/{Uri.EscapeDataString(account)}";
        HttpRequestMessage Request(HttpMethod method, string url)
        {
            var r = new HttpRequestMessage(method, url);
            r.Headers.Add("X-B2B-API-Key", apiKey);
            r.Headers.Add("X-B2B-API-Version", ApiVersion);
            return r;
        }

        if (checkOnly)
        {
            try
            {
                using var response = await client.SendAsync(Request(HttpMethod.Get, accountPath), deadline.Token);
                return (int)response.StatusCode switch
                {
                    >= 200 and < 300 => new DeliveryResult(true, $"B2Brouter accepted the API key for account {account}. No invoice was sent.", (int)response.StatusCode),
                    401 or 403 => Final("B2Brouter refused the API key. Create a new key in B2Brouter under Developers and save it here.", (int)response.StatusCode),
                    404 => Final($"B2Brouter has no account {account} for this key. Copy the account ID from B2Brouter's Developers page.", 404),
                    var code => new DeliveryResult(false, "B2Brouter isn't answering right now.", code),
                };
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                return Transient(ex);
            }
        }

        var type = contentType.Split(';')[0].Trim().ToLowerInvariant();
        if (!EInvoiceTypes.Contains(type))
            return Final("This destination takes e-invoice files only: Peppol BIS, EN 16931 CII, XRechnung or Factur-X. Choose an e-invoice file for it.");

        HttpResponseMessage imported;
        try
        {
            var post = Request(HttpMethod.Post, $"{accountPath}/invoices/import?send_after_import=true");
            post.Content = new ByteArrayContent(content);
            post.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            imported = await client.SendAsync(post, deadline.Token);
        }
        catch (HttpRequestException ex) when (ex.InnerException is SocketException)
        {
            return Transient(ex); // the connection never opened: nothing reached B2Brouter
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning("B2Brouter import outcome unknown ({Error})", ex.GetType().Name);
            return Final(Unconfirmed);
        }

        using (imported)
        {
            var code = (int)imported.StatusCode;
            var body = await ReadAsync(imported, deadline.Token);
            if (code is 409 or 429)
                return new DeliveryResult(false, "B2Brouter asked us to wait. It will be tried again.", code, Truncate(body),
                    RetryAfter: imported.Headers.RetryAfter?.Delta);
            if (code is 400 or 401 or 403 or 404 or 406 or 422)
                return new DeliveryResult(false, $"B2Brouter refused the file: {ErrorMessage(body) ?? $"HTTP {code}"}", code, Truncate(body),
                    DeliveryOutcome.NotRetryable, SupplierReasonObservable: true);
            var invoice = code is 200 or 201 ? Invoice(body) : null;
            if (invoice?["id"] is not JsonValue idValue || idValue.ToString() is not { Length: > 0 } id) return Final(Unconfirmed, code, body);

            var number = Text(invoice, "number") ?? id;
            var (latest, last) = (invoice, body);
            // Settle: a sandbox refusal arrives "within seconds". A failed read leaves the import standing as sent.
            for (var i = 0; i < 3 && Pending.Contains(Text(latest, "state") ?? ""); i++)
            {
                try
                {
                    await Task.Delay(SettleDelay, deadline.Token);
                    using var status = await client.SendAsync(Request(HttpMethod.Get, $"{BaseUrl}/invoices/{Uri.EscapeDataString(id)}"), deadline.Token);
                    if (!status.IsSuccessStatusCode || Invoice(await ReadAsync(status, deadline.Token)) is not { } now) break;
                    (latest, last) = (now, now.Parent!.ToJsonString());
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { break; }
            }
            var state = Text(latest, "state") ?? "";
            if (Failed.Contains(state))
                return new DeliveryResult(false, $"B2Brouter couldn't deliver invoice {number}: {Text(latest, "error_code") ?? state}.", code, Truncate(last),
                    DeliveryOutcome.NotRetryable, SupplierReasonObservable: true, RemoteReference: id);
            return new DeliveryResult(true, null, code, $"b2brouter invoice {id}: {state}", RemoteReference: id);
        }
    }

    private static async Task<string> ReadAsync(HttpResponseMessage r, CancellationToken ct)
    {
        try { return await r.Content.ReadAsStringAsync(ct); } catch (Exception) { return ""; }
    }

    private static JsonObject? Invoice(string body)
    {
        try { return (JsonNode.Parse(body) as JsonObject)?["invoice"] as JsonObject; } catch (Exception) { return null; }
    }

    private static string? ErrorMessage(string body)
    {
        try { return Text((JsonNode.Parse(body) as JsonObject)?["error"] as JsonObject, "message"); } catch (Exception) { return null; }
    }

    private static string? Text(JsonObject? json, string key) =>
        json?[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;

    private static string Truncate(string body) => body.Length <= 2000 ? body : body[..2000];

    private static DeliveryResult Final(string message, int? code = null, string? body = null) =>
        new(false, message, code, body is null ? null : Truncate(body), DeliveryOutcome.NotRetryable);

    private DeliveryResult Transient(Exception ex)
    {
        logger.LogWarning("B2Brouter could not be reached ({Error})", ex.GetType().Name);
        return new DeliveryResult(false, "B2Brouter isn't answering right now.");
    }
}
