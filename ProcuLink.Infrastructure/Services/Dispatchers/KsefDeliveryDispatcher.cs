using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using ProcuLink.Core.Constants;
using ProcuLink.Core.Documents;
using Kind = ProcuLink.Core.Documents.FlowDeliveryFailureKind;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Infrastructure.Services.Ksef;
using ProcuLink.Transform.Output.Ksef;

namespace ProcuLink.Infrastructure.Services.Dispatchers;

/// <summary>
/// Issues a KSeF FA(3) file in KSeF with the customer's own token, as the issuer, for the NIP the destination is set up for.
/// Config <c>{"environment": "test"|"demo"|"production", "nip": "…", "checkOnly"?: true}</c>; credentials <c>{"token": "…"}</c>.
/// An unknown or absent environment is refused with no call. A test (checkOnly, or a test fire) only signs in. Once KSeF has
/// answered the invoice POST nothing is ever re-sent: an unconfirmed outcome is a dead letter that names the reference.
/// Never throws; never puts the token, the access token or the session key in a message.
/// </summary>
public sealed class KsefDeliveryDispatcher(KsefClient ksef, ILogger<KsefDeliveryDispatcher> logger) : IDeliveryDispatcher
{
    /// <summary>The whole dispatch stays under DeliverDocumentJob.ReclaimAfter (2 min), so a live send is never reclaimed and re-driven.</summary>
    internal static TimeSpan Deadline = TimeSpan.FromSeconds(100);

    private static readonly Dictionary<string, string> Names = new() { ["test"] = "Test", ["demo"] = "Demo", ["production"] = "Production" };

    public string Protocol => DeliveryProtocolConstants.Ksef;
    public ResendSafety ResendSafety => ResendSafety.Unsafe;
    public bool CapturesSupplierResponseBody => false;

    public async Task<DeliveryResult> DispatchAsync(
        byte[] content, string fileName, string contentType, SupplierDeliveryConfig config,
        string decryptedCredentials, CancellationToken ct, string? idempotencyKey = null, bool isTestFire = false)
    {
        const string misconfigured = "This KSeF destination isn't set up correctly (environment or NIP). Edit it and save again. Nothing was sent.";
        string? environment, nip, token;
        bool checkOnly;
        try
        {
            var cfg = JsonNode.Parse(config.ConfigJson) as JsonObject;
            environment = cfg?["environment"] is JsonValue e && e.TryGetValue<string>(out var es) ? es : null;
            nip = PolishNip.Normalize(cfg?["nip"] is JsonValue n && n.TryGetValue<string>(out var ns) ? ns : null);
            checkOnly = isTestFire || (cfg?["checkOnly"] is JsonValue c && c.TryGetValue<bool>(out var b) && b);
            token = string.IsNullOrEmpty(decryptedCredentials) ? null
                : JsonNode.Parse(decryptedCredentials)?["token"] is JsonValue t && t.TryGetValue<string>(out var ts) ? ts : null;
        }
        catch (System.Text.Json.JsonException) { return Final(misconfigured, Kind.Setup); }
        if (ksef.BaseUrl(environment) is null || !PolishNip.IsValid(nip)) return Final(misconfigured, Kind.Setup);
        if (string.IsNullOrEmpty(token)) return Final(BadToken(nip!), Kind.Setup);

        KsefSummary? file = null;
        if (!checkOnly)
        {
            try { file = Fa3Summary.Read(content); }
            catch (Exception) { return Final("This KSeF destination can only send a KSeF invoice file output. Nothing was sent.", Kind.Setup); }
            if (file.SellerNip != nip)
                return Final($"This invoice's seller NIP is {file.SellerNip}, but this KSeF destination is set up for NIP {nip}. Nothing was sent.", Kind.Setup);
            if (string.CompareOrdinal(file.IssueDate, WarsawToday()) > 0)
                return Final($"The issue date {file.IssueDate} is in the future. KSeF accepts invoices dated today or earlier. Nothing was sent.", Kind.Refused);
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(Deadline);
        KsefSession session;
        try
        {
            session = await ksef.SignInAsync(environment!, nip!, token, deadline.Token);
        }
        catch (KsefRefusedException r) when (r.SignIn && r.Code == 415)
        {
            return Final("This KSeF token doesn't have permission to issue invoices. Make a token with the \"issue invoices\" permission (InvoiceWrite).", Kind.Setup);
        }
        catch (KsefRefusedException r) when (r.SignIn && r.Code is 450 or 425 or 401 or 403)
        {
            return Final(BadToken(nip!), Kind.Setup);
        }
        catch (KsefRefusedException r)
        {
            return Final($"KSeF refused the sign-in: {r.Message}", null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return Transient(ex);
        }

        if (checkOnly) return new DeliveryResult(true, $"Signed in to KSeF {Names[environment!]} for NIP {nip}. No invoice was sent.", 200);

        try
        {
            var r = await ksef.SendAsync(session, content, deadline.Token);
            if (r.StatusCode == 200 && r.KsefNumber is { } number)
                return new DeliveryResult(true, null, 200, RemoteReference: number, RemoteEnvironment: environment, Receipt: r.Upo);
            if (r.StatusCode == 440)
                return Final($"KSeF already has an invoice with this number from this seller (KSeF number {r.OriginalKsefNumber}). Nothing new was issued.", Kind.Refused, r.OriginalKsefNumber);
            if (r.StatusCode is { } code && code != 200)
                return Final($"KSeF rejected the invoice: {r.StatusText}", null);
            // Submitted, outcome unknown (or a 200 without a number): never sent again.
            return Final($"KSeF received the invoice (reference {r.InvoiceReference ?? r.SessionReference}) but we couldn't confirm the result. " +
                "Check it in Aplikacja Podatnika KSeF before sending again; sending again may issue it twice.", null, r.InvoiceReference ?? r.SessionReference);
        }
        catch (KsefRefusedException r)
        {
            return Final($"KSeF rejected the invoice: {r.Message}", null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Opening the session failed, or the invoice POST was answered 429: nothing was submitted, so a later retry is safe.
            return Transient(ex);
        }
    }

    private static string BadToken(string nip) =>
        $"KSeF didn't accept the token. It may have expired or been revoked, or it isn't for NIP {nip}. Make a new token in Aplikacja Podatnika KSeF and save it on this destination.";

    /// <summary>
    /// A final failure. <paramref name="kind"/> says who acts: the customer's setup or this invoice, or null (ours, so it pages:
    /// a rejected invoice, which may be our file; an unexplained sign-in refusal; an unconfirmed outcome). <paramref name="reference"/> is what KSeF may already hold, kept on
    /// the delivery so the document is never sent again.
    /// </summary>
    private static DeliveryResult Final(string message, string? kind, string? reference = null) =>
        new(false, message, Outcome: DeliveryOutcome.NotRetryable, RemoteReference: reference, FailureKind: kind);

    private DeliveryResult Transient(Exception ex)
    {
        logger.LogWarning("KSeF delivery could not reach KSeF ({Error})", ex.GetType().Name);
        return new DeliveryResult(false, "KSeF couldn't be reached. Nothing was sent; it will be tried again.",
            RetryAfter: (ex as KsefUnavailableException)?.RetryAfter);
    }

    // KSeF refuses a future P_1; "today" is Poland's.
    private static string WarsawToday()
    {
        TimeZoneInfo zone;
        try { zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw"); }
        catch (TimeZoneNotFoundException) { zone = TimeZoneInfo.FindSystemTimeZoneById("Central European Standard Time"); }
        return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }
}
