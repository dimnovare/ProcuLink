using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace ProcuLink.Infrastructure.Services.Ksef;

/// <summary>KSeF API 2.0 bases per environment (official values from srodowiska.md and each environment's OpenAPI <c>servers</c>), and poll budgets.</summary>
public sealed class KsefOptions
{
    public const string Section = "Ksef";
    public const string HttpClientName = "ksef";

    /// <summary>
    /// test: self-signed certificates, data shared between integrators, fictitious only. demo: production configuration, no legal effect.
    /// production: legal effect. Only a local rehearsal ever overrides production (to the TEST URL); never set on Railway.
    /// </summary>
    public Dictionary<string, string> BaseUrls { get; set; } = new()
    {
        ["test"] = "https://api-test.ksef.mf.gov.pl/v2",
        ["demo"] = "https://api-demo.ksef.mf.gov.pl/v2",
        ["production"] = "https://api.ksef.mf.gov.pl/v2",
    };

    public TimeSpan AuthPollInterval { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan AuthPollBudget { get; set; } = TimeSpan.FromSeconds(20);
    public TimeSpan StatusPollInterval { get; set; } = TimeSpan.FromSeconds(2);
    public TimeSpan StatusPollBudget { get; set; } = TimeSpan.FromSeconds(45);
    public TimeSpan UpoBudget { get; set; } = TimeSpan.FromSeconds(15);
}

public sealed record KsefSession(string BaseUrl, string AccessToken, X509Certificate2 SymmetricKeyCertificate, string? SymmetricKeyId);

/// <summary>
/// The outcome of one invoice: <see cref="Submitted"/> once KSeF answered 202 to the invoice POST (from then on it must never be re-sent).
/// <see cref="StatusCode"/> is KSeF's final invoice status (200 issued, 440 duplicate, other rejected), or null when it was not known in time.
/// </summary>
public sealed record KsefSendResult(bool Submitted, string? KsefNumber, int? StatusCode, string? StatusText, string? InvoiceReference,
    string SessionReference, byte[]? Upo, string? OriginalKsefNumber = null);

/// <summary>KSeF refused, finally: a sign-in status (450 bad token, 415 no permission, …) or an HTTP 4xx, with KSeF's own text.</summary>
public sealed class KsefRefusedException(int code, string text, bool signIn) : Exception(text)
{
    public int Code { get; } = code;
    public bool SignIn { get; } = signIn;
}

/// <summary>A 429 or 5xx before anything was submitted: safe to try again later.</summary>
public sealed class KsefUnavailableException(TimeSpan? retryAfter, int? status = null) : Exception("KSeF is unavailable.")
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
    public int? Status { get; } = status;
}

/// <summary>
/// A thin KSeF 2.0 client on HttpClient and BCL crypto only (no KSeF.Client package: it is published only on GitHub Packages and pulls
/// SkiaSharp and Microsoft.Extensions 10.x). Flow per uwierzytelnianie.md and sesja-interaktywna.md in CIRFMF/ksef-docs, checked against
/// the OpenAPI of all three environments (2026-11-25: TEST 2.10.1, DEMO and PROD 2.9.0; the auth status path used is /auth/{ref},
/// the one all three expose). Never logs or returns the KSeF token, the access token or the session key.
/// </summary>
public sealed class KsefClient(IHttpClientFactory http, IOptions<KsefOptions> options)
{
    private KsefOptions O => options.Value;

    /// <summary>The base URL of a known environment; null for anything else, which callers refuse without a call.</summary>
    public string? BaseUrl(string? environment) =>
        environment is "test" or "demo" or "production" && O.BaseUrls.TryGetValue(environment, out var url) ? url.TrimEnd('/') : null;

    public async Task<KsefSession> SignInAsync(string environment, string nip, string token, CancellationToken ct)
    {
        var baseUrl = BaseUrl(environment) ?? throw new ArgumentException("Unknown KSeF environment.", nameof(environment));
        using var client = http.CreateClient(KsefOptions.HttpClientName);
        var challenge = await SendJson(client, HttpMethod.Post, $"{baseUrl}/auth/challenge", null, null, true, ct);
        var certs = (await SendJson(client, HttpMethod.Get, $"{baseUrl}/security/public-key-certificates", null, null, true, ct)).AsArray();
        var (tokenCert, tokenKeyId) = Certificate(certs, "KsefTokenEncryption");
        var (keyCert, keyId) = Certificate(certs, "SymmetricKeyEncryption");

        using var rsa = tokenCert.GetRSAPublicKey()!;
        var encrypted = rsa.Encrypt(Encoding.UTF8.GetBytes($"{token}|{challenge["timestampMs"]}"), RSAEncryptionPadding.OaepSHA256);
        var init = await SendJson(client, HttpMethod.Post, $"{baseUrl}/auth/ksef-token", null, new JsonObject
        {
            ["challenge"] = challenge["challenge"]?.GetValue<string>(),
            ["contextIdentifier"] = new JsonObject { ["type"] = "Nip", ["value"] = nip },
            ["encryptedToken"] = Convert.ToBase64String(encrypted),
            ["publicKeyId"] = tokenKeyId,
        }, true, ct);
        var reference = init["referenceNumber"]!.GetValue<string>();
        var authToken = init["authenticationToken"]!["token"]!.GetValue<string>();

        var until = DateTime.UtcNow + O.AuthPollBudget;
        while (true)
        {
            var status = (await SendJson(client, HttpMethod.Get, $"{baseUrl}/auth/{Uri.EscapeDataString(reference)}", authToken, null, true, ct))["status"];
            var code = status?["code"]?.GetValue<int>() ?? 0;
            if (code == 200) break;
            if (code != 100) throw new KsefRefusedException(code, Text(status), signIn: true);
            if (DateTime.UtcNow >= until) throw new KsefUnavailableException(null);
            await Task.Delay(O.AuthPollInterval, ct);
        }
        var tokens = await SendJson(client, HttpMethod.Post, $"{baseUrl}/auth/token/redeem", authToken, null, true, ct);
        return new KsefSession(baseUrl, tokens["accessToken"]!["token"]!.GetValue<string>(), keyCert, keyId);
    }

    /// <summary>
    /// Opens an online session, sends one invoice, waits for its status, closes the session and tries the UPO. Before the invoice POST is
    /// answered 202 a failure throws (<see cref="KsefUnavailableException"/> is safe to retry); after it, nothing throws: the result says
    /// Submitted, and a null status means the outcome is unknown.
    /// </summary>
    public async Task<KsefSendResult> SendAsync(KsefSession s, byte[] xml, CancellationToken ct)
    {
        using var client = http.CreateClient(KsefOptions.HttpClientName);
        var key = RandomNumberGenerator.GetBytes(32);
        var iv = RandomNumberGenerator.GetBytes(16);
        using var rsa = s.SymmetricKeyCertificate.GetRSAPublicKey()!;
        var open = await SendJson(client, HttpMethod.Post, $"{s.BaseUrl}/sessions/online", s.AccessToken, new JsonObject
        {
            ["formCode"] = new JsonObject { ["systemCode"] = "FA (3)", ["schemaVersion"] = "1-0E", ["value"] = "FA" },
            ["encryption"] = new JsonObject
            {
                ["encryptedSymmetricKey"] = Convert.ToBase64String(rsa.Encrypt(key, RSAEncryptionPadding.OaepSHA256)),
                ["initializationVector"] = Convert.ToBase64String(iv),
                ["publicKeyId"] = s.SymmetricKeyId,
            },
        }, false, ct);
        var session = open["referenceNumber"]!.GetValue<string>();
        string? invoiceRef = null;
        KsefSendResult result;
        try
        {
            using var aes = Aes.Create();
            aes.Key = key;
            var encrypted = aes.EncryptCbc(xml, iv, PaddingMode.PKCS7);
            var body = new JsonObject
            {
                ["invoiceHash"] = Convert.ToBase64String(SHA256.HashData(xml)), ["invoiceSize"] = xml.Length,
                ["encryptedInvoiceHash"] = Convert.ToBase64String(SHA256.HashData(encrypted)), ["encryptedInvoiceSize"] = encrypted.Length,
                ["encryptedInvoiceContent"] = Convert.ToBase64String(encrypted),
            };
            JsonNode sent;
            try
            {
                sent = await SendJson(client, HttpMethod.Post, $"{s.BaseUrl}/sessions/online/{Uri.EscapeDataString(session)}/invoices", s.AccessToken, body, false, ct);
            }
            catch (Exception ex) when (ex is not (KsefRefusedException or KsefUnavailableException { Status: 429 }))
            {
                // No answer (timeout, connection reset) or a 5xx, which a gateway may give after KSeF took it: unknown, and never retried.
                // Only a 4xx refusal or a 429 is certain not to have been taken.
                return new KsefSendResult(true, null, null, null, null, session, null);
            }
            // Taken: from here nothing may throw into a retry. An answer with no usable reference is still taken.
            if (sent?["referenceNumber"] is not JsonValue v || !v.TryGetValue<string>(out var r) || string.IsNullOrEmpty(r))
                return new KsefSendResult(true, null, null, null, null, session, null);
            invoiceRef = r;
            result = await StatusAsync(client, s, session, invoiceRef, ct);
        }
        catch (Exception) when (invoiceRef is not null)
        {
            return new KsefSendResult(true, null, null, null, invoiceRef, session, null);
        }
        finally
        {
            await CloseQuietly(client, s, session);
        }
        return result.StatusCode == 200
            ? result with { Upo = await UpoQuietly(client, $"{s.BaseUrl}/sessions/{Uri.EscapeDataString(session)}/invoices/{Uri.EscapeDataString(invoiceRef)}/upo", s.AccessToken, ct) }
            : result;
    }

    // After the 202: poll until a final status (100/150 = working) or the budget ends. Never throws for a KSeF answer.
    private async Task<KsefSendResult> StatusAsync(HttpClient client, KsefSession s, string session, string invoiceRef, CancellationToken ct)
    {
        var statusUrl = $"{s.BaseUrl}/sessions/{Uri.EscapeDataString(session)}/invoices/{Uri.EscapeDataString(invoiceRef)}";
        var until = DateTime.UtcNow + O.StatusPollBudget;
        while (true)
        {
            try
            {
                var answer = await SendJson(client, HttpMethod.Get, statusUrl, s.AccessToken, null, true, ct);
                var status = answer["status"];
                if (status?["code"]?.GetValue<int>() is { } c and not (100 or 150))
                    return c == 200
                        ? new KsefSendResult(true, answer["ksefNumber"]?.GetValue<string>(), 200, null, invoiceRef, session, null)
                        : new KsefSendResult(true, null, c, Text(status), invoiceRef, session, null, status?["extensions"]?["originalKsefNumber"]?.ToString());
            }
            catch (Exception) when (!ct.IsCancellationRequested) { /* keep asking within the budget */ }
            if (DateTime.UtcNow >= until) return new KsefSendResult(true, null, null, null, invoiceRef, session, null);
            await Task.Delay(O.StatusPollInterval, ct);
        }
    }

    // Whether the per-invoice UPO is ready right after close is unverified (settled in the live test): try it within a short budget.
    private async Task<byte[]?> UpoQuietly(HttpClient client, string url, string accessToken, CancellationToken ct)
    {
        try { return await UpoAsync(client, url, accessToken, ct); }
        catch (Exception) { return null; }   // the invoice is issued; a missing receipt never fails it
    }

    private async Task<byte[]?> UpoAsync(HttpClient client, string url, string accessToken, CancellationToken ct)
    {
        var until = DateTime.UtcNow + O.UpoBudget;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                using var res = await client.SendAsync(req, ct);
                if (res.IsSuccessStatusCode) return await res.Content.ReadAsByteArrayAsync(ct);
            }
            catch (Exception) when (!ct.IsCancellationRequested) { }
            if (DateTime.UtcNow >= until) return null;
            await Task.Delay(O.StatusPollInterval, ct);
        }
        return null;
    }

    private static async Task CloseQuietly(HttpClient client, KsefSession s, string session)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{s.BaseUrl}/sessions/online/{Uri.EscapeDataString(session)}/close");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", s.AccessToken);
            using var _ = await client.SendAsync(req, cts.Token);
        }
        catch (Exception) { /* best effort; KSeF closes an idle session itself */ }
    }

    // The certificate for a purpose that is valid now, the latest validFrom first.
    private static (X509Certificate2 Cert, string? Id) Certificate(JsonArray certs, string usage)
    {
        var now = DateTimeOffset.UtcNow;
        var pick = certs.Where(c => c?["usage"]?.AsArray().Any(u => u?.GetValue<string>() == usage) == true
                && DateTimeOffset.Parse(c!["validFrom"]!.GetValue<string>()) <= now && now <= DateTimeOffset.Parse(c["validTo"]!.GetValue<string>()))
            .OrderByDescending(c => DateTimeOffset.Parse(c!["validFrom"]!.GetValue<string>())).FirstOrDefault()
            ?? throw new KsefUnavailableException(null);
        return (new X509Certificate2(Convert.FromBase64String(pick["certificate"]!.GetValue<string>())), pick["publicKeyId"]?.GetValue<string>());
    }

    // KSeF's own words: the status description and its details, verbatim, at most 500 characters.
    private static string Text(JsonNode? status)
    {
        var details = status?["details"] is JsonArray a ? string.Join("; ", a.Select(d => d?.ToString())) : "";
        var text = string.Join(" ", new[] { status?["description"]?.ToString(), details }.Where(t => !string.IsNullOrWhiteSpace(t)));
        return text.Length > 500 ? text[..500] : text;
    }

    /// <summary>One JSON call. 429 and 5xx are <see cref="KsefUnavailableException"/>; any other non-2xx is a final refusal with KSeF's body.</summary>
    private static async Task<JsonNode> SendJson(HttpClient client, HttpMethod method, string url, string? bearer, JsonObject? body, bool signIn, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, url);
        if (bearer is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (body is not null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var res = await client.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (res.StatusCode == HttpStatusCode.TooManyRequests || (int)res.StatusCode >= 500)
            throw new KsefUnavailableException(res.Headers.RetryAfter?.Delta
                ?? (res.Headers.RetryAfter?.Date is { } at ? at - DateTimeOffset.UtcNow : null), (int)res.StatusCode);
        if (!res.IsSuccessStatusCode)
            throw new KsefRefusedException((int)res.StatusCode, text.Length > 500 ? text[..500] : text, signIn);
        return JsonNode.Parse(text) ?? new JsonObject();
    }
}
