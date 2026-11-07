using System.Text.Json;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using ProcuLink.Api.Services.Documents;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Security;
using ProcuLink.Core.Services.Security;
using ProcuLink.Infrastructure;
using ProcuLink.Infrastructure.Services;
using ProcuLink.Infrastructure.Services.Security;

namespace ProcuLink.Api.Jobs;

/// <summary>
/// Posts one document event to a flow's webhook. The event id is made once at enqueue, so every retry carries the same
/// <c>X-ProcuLink-Event-Id</c> and the receiver can dedupe. A failure throws, and Hangfire retries.
/// </summary>
public class FlowWebhookJob(
    ProcuLinkDbContext db, IHttpClientFactory http, DeliveryEncryptionService encryption, OutboundRequestGuard guard,
    DocumentEngineGate gate, ILogger<FlowWebhookJob> logger)
{
    private sealed record Body(Guid Id, string Type, DateTime CreatedAt, object Document);

    [Queue("background")]
    [AutomaticRetry(Attempts = 3, DelaysInSeconds = new[] { 30, 120, 600 })]
    public async Task ExecuteAsync(Guid organisationId, Guid documentId, string eventType, Guid eventId, CancellationToken ct)
    {
        if (!gate.IsEnabled(organisationId)) return;
        var doc = await db.FlowDocuments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == documentId && d.OrgId == organisationId, ct);
        if (doc is null) return;
        var intake = await db.FlowIntakes.AsNoTracking().FirstOrDefaultAsync(
            i => i.OrgId == organisationId && i.FlowId == doc.FlowId && i.Kind == IntakeKind.Api && i.IsEnabled, ct);
        if (intake is null || string.IsNullOrWhiteSpace(FlowWebhook.UrlOf(intake))) return;

        var (success, message, _) = await PostAsync(intake, eventType, eventId, doc, test: false, ct);
        if (!success) throw new InvalidOperationException($"Flow webhook {eventType} for document {documentId} failed: {message}");
    }

    /// <summary>Sends <c>document.test</c> now. <c>Message</c> is null on success.</summary>
    public async Task<(bool Success, string? Message, int? ResponseCode)> TestAsync(Guid organisationId, Guid flowId, Guid intakeId, CancellationToken ct)
    {
        var intake = await db.FlowIntakes.AsNoTracking().FirstOrDefaultAsync(
            i => i.OrgId == organisationId && i.FlowId == flowId && i.Id == intakeId, ct) ?? throw new KeyNotFoundException();
        if (intake.Kind != IntakeKind.Api || string.IsNullOrWhiteSpace(FlowWebhook.UrlOf(intake)))
            throw new IntakeException("invalid_intake", "Enter a webhook URL first.");
        return await PostAsync(intake, FlowWebhookEvent.Test, Guid.NewGuid(), null, test: true, ct);
    }

    private async Task<(bool Success, string? Message, int? ResponseCode)> PostAsync(
        FlowIntake intake, string eventType, Guid eventId, FlowDocument? doc, bool test, CancellationToken ct)
    {
        var url = FlowWebhook.UrlOf(intake)!;
        var documentId = doc?.Id ?? Guid.Empty;
        var body = JsonSerializer.SerializeToUtf8Bytes(new Body(eventId, eventType, DateTime.UtcNow, new
        {
            id = documentId, flowId = intake.FlowId, fileName = doc?.FileName ?? "", status = doc?.Status ?? "",
            statusReason = doc?.StatusReason, url = $"/api/v1/documents/{documentId}",
        }), DocumentJson.Options);

        string? signature = null;
        if (!string.IsNullOrEmpty(intake.EncryptedCredentials))
        {
            try
            {
                using var credentials = JsonDocument.Parse(encryption.Decrypt(intake.EncryptedCredentials,
                    new CredentialScope(intake.OrgId, CredentialPurpose.FlowIntakeCredentials, intake.Id)));
                if (credentials.RootElement.TryGetProperty("webhookSecret", out var s) && s.ValueKind == JsonValueKind.String
                    && s.GetString() is { Length: > 0 } secret)
                    signature = WebhookSignature.Header(body, secret);
            }
            catch (CredentialUnbindableException ex)
            {
                // A saved secret means the receiver expects a signature: never send it unsigned.
                logger.LogError(ex, "Flow webhook secret for intake {IntakeId} could not be decrypted ({Reason}) - not sending.", intake.Id, ex.Reason);
                return (false, "The webhook secret could not be decrypted.", null);
            }
        }

        // The URL passed the policy at save; it can resolve somewhere private later.
        var allowed = await guard.ValidateAsync(url, ct);
        if (!allowed.Allowed) return (false, allowed.Reason ?? "The webhook URL is not allowed.", null);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(body) };
            request.Content.Headers.ContentType = new("application/json") { CharSet = "utf-8" };
            request.Headers.TryAddWithoutValidation("X-ProcuLink-Event", eventType);
            request.Headers.TryAddWithoutValidation("X-ProcuLink-Event-Id", eventId.ToString());
            if (signature is not null) request.Headers.TryAddWithoutValidation("X-ProcuLink-Signature", signature);
            if (test) request.Headers.TryAddWithoutValidation("X-ProcuLink-Test", "true");

            using var response = await http.CreateClient("delivery").SendAsync(request, ct);
            var code = (int)response.StatusCode;
            return response.IsSuccessStatusCode ? (true, null, code) : (false, $"The webhook answered HTTP {code}.", code);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return (false, "The webhook could not be reached.", null);
        }
    }
}
