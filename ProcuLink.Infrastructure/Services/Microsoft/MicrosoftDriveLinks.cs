using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Delivery;
using ProcuLink.Core.Services.Google;
using ProcuLink.Core.Services.Microsoft;

namespace ProcuLink.Infrastructure.Services.Microsoft;

/// <summary>
/// Redeems a pasted sharing link through Graph <c>/shares</c> (plan 30 Part B). Only https links on OneDrive and
/// SharePoint hosts are encoded; nothing is ever requested from the pasted host, and the link is not logged.
/// </summary>
public sealed class MicrosoftDriveLinks(IHttpClientFactory http, ILinkedAccountTokens tokens) : IMicrosoftDriveLinks
{
    private const string WrongHost = "Paste a OneDrive or SharePoint link. In OneDrive or SharePoint, choose Share, then Copy link.";
    private const string Unopenable = "ProcuLink couldn't open that link with the connected Microsoft account. Check that the link works for that account.";

    public async Task<DriveItemRef> ResolveAsync(Guid orgId, Guid accountId, string link, CancellationToken ct)
    {
        link = link.Trim();
        // Sovereign clouds (.sharepoint.us, .de, .cn) are out of reach of /common + graph.microsoft.com, so they are refused too.
        if (link.Length > 2048 || !Uri.TryCreate(link, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || uri.Host.ToLowerInvariant() is not ("1drv.ms" or "onedrive.live.com") && !uri.Host.EndsWith(".sharepoint.com", StringComparison.OrdinalIgnoreCase))
            throw new DriveLinkException(WrongHost);

        var token = await tokens.GetAccessTokenAsync(orgId, accountId, LinkedAccountProviders.Microsoft, ct);
        if (token.Failure is { } failure)
            throw new DriveLinkException(failure.ErrorMessage ?? "", unavailable: failure.Outcome != DeliveryOutcome.NotRetryable);

        // Graph shares-get: "u!" + unpadded base64url of the link.
        var shareId = "u!" + Convert.ToBase64String(Encoding.UTF8.GetBytes(link)).TrimEnd('=').Replace('/', '_').Replace('+', '-');
        var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://graph.microsoft.com/v1.0/shares/{shareId}/driveItem?$select=id,name,folder,file,parentReference");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        request.Headers.TryAddWithoutValidation("Prefer", "redeemSharingLink");

        JsonNode? item;
        try
        {
            using var response = await http.CreateClient(MicrosoftOAuthClient.HttpClientName).SendAsync(request, ct);
            if ((int)response.StatusCode is 408 or 429 or >= 500) throw new DriveLinkException("", unavailable: true);
            if (!response.IsSuccessStatusCode) throw new DriveLinkException(Unopenable);
            item = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            throw new DriveLinkException("", unavailable: true);
        }

        if (item?["folder"] is null) throw new DriveLinkException("That link opens a file. Paste the link to a folder.");
        var driveId = item["parentReference"]?["driveId"]?.GetValue<string>();
        var itemId = item["id"]?.GetValue<string>();
        if (string.IsNullOrEmpty(driveId) || string.IsNullOrEmpty(itemId)) throw new DriveLinkException(Unopenable);
        return new DriveItemRef(driveId, itemId, item["name"]?.GetValue<string>() ?? "");
    }
}
