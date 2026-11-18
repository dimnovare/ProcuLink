using System.Net;
using System.Text.RegularExpressions;
using Lib.Net.Http.WebPush;
using Lib.Net.Http.WebPush.Authentication;
using ProcuLink.Core.Entities;

namespace ProcuLink.Api.Services.PhoneAlerts;

public enum PushResult { Sent, Gone, Failed }

public interface IPhoneAlertSender
{
    bool CanSend { get; }
    Task<PushResult> SendAsync(PushDevice device, string json, string topic, CancellationToken ct);
}

/// <summary>Web Push with VAPID keys from <c>WebPush:PublicKey</c> / <c>WebPush:PrivateKey</c>; off while either is blank. Never logs the push address.</summary>
public sealed class WebPushAlertSender(IHttpClientFactory http, IConfiguration config, ILogger<WebPushAlertSender> log) : IPhoneAlertSender
{
    public const string HttpClientName = "web-push";

    public bool CanSend => !string.IsNullOrWhiteSpace(config["WebPush:PublicKey"]) && !string.IsNullOrWhiteSpace(config["WebPush:PrivateKey"]);

    public async Task<PushResult> SendAsync(PushDevice device, string json, string topic, CancellationToken ct)
    {
        var subscription = new PushSubscription { Endpoint = device.Endpoint };
        subscription.SetKey(PushEncryptionKeyName.P256DH, device.P256dh);
        subscription.SetKey(PushEncryptionKeyName.Auth, device.Auth);
        var message = new PushMessage(json) { Topic = topic, TimeToLive = 43200, Urgency = PushMessageUrgency.Normal };
        try
        {
            // Inside the try: a malformed key is this send's Failed, not an exception for the whole org.
            using var vapid = new VapidAuthentication(config["WebPush:PublicKey"]!, config["WebPush:PrivateKey"]!)
            {
                Subject = config["WebPush:Subject"] ?? "mailto:support@proculink.eu",
            };
            // No in-process retry on 429: the next run (5 minutes) is the retry.
            await new PushServiceClient(http.CreateClient(HttpClientName)) { AutoRetryAfter = false }
                .RequestPushMessageDeliveryAsync(subscription, message, vapid, VapidAuthenticationScheme.Vapid, ct);
            return PushResult.Sent;
        }
        catch (PushServiceClientException ex) when ((int)ex.StatusCode is >= 400 and < 500 && ex.StatusCode != HttpStatusCode.TooManyRequests)
        {
            // A rotated or mismatched VAPID key removes every device at once: say so, by id only, with the push service's reason and the device's age.
            log.LogInformation("Phone alert device {DeviceId} removed: {Status} \"{Reason}\", saved {Minutes} min before",
                device.Id, ex.StatusCode, Reason(ex.Body), (int)(DateTime.UtcNow - device.CreatedAt).TotalMinutes);
            return PushResult.Gone;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            var refusal = ex as PushServiceClientException;
            log.LogWarning("Phone alert to device {DeviceId} failed: {Status} \"{Reason}\"", device.Id,
                refusal?.StatusCode.ToString() ?? ex.GetType().Name, Reason(refusal?.Body));
            return PushResult.Failed;
        }
    }

    /// <summary>A push service's refusal text for the log: one line, at most 120 characters, any address replaced (the push address must never be logged).</summary>
    public static string Reason(string? body)
    {
        var text = Regex.Replace(Regex.Replace(body ?? "", @"\S*://\S*", "[address]"), @"\s+", " ").Trim();
        return text.Length > 120 ? text[..120] : text;
    }
}
