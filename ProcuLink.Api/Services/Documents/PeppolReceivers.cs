using System.Net;
using System.Xml.Linq;
using DnsClient;
using DnsClient.Protocol;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using ProcuLink.Core.Documents;

namespace ProcuLink.Api.Services.Documents;

public sealed class PeppolLookupOptions
{
    public string SmlZone { get; set; } = "participant.sml.prod.tech.peppol.org";
}

public enum SmpLocationKind { Found, NotRegistered, Unknown }

public sealed record SmpLocation(SmpLocationKind Kind, string? SmpUrl);

/// <summary>Where a participant's SMP lives, from the SML's NAPTR record (SML 1.3.0: service <c>Meta:SMP</c>, an https URL).</summary>
public interface IPeppolSmpLocator
{
    Task<SmpLocation> LocateAsync(string host, CancellationToken ct);
}

/// <summary>NXDOMAIN is the only "not registered"; any other failure, or a record that is not an https URL, is unknown.</summary>
public sealed class DnsSmpLocator : IPeppolSmpLocator
{
    private const string ZoneMarker = ".iso6523-actorid-upis.";
    private readonly LookupClient _dns = new(new LookupClientOptions { Timeout = TimeSpan.FromSeconds(3), Retries = 1, UseCache = true });

    public async Task<SmpLocation> LocateAsync(string host, CancellationToken ct)
    {
        try
        {
            var answer = await _dns.QueryAsync(host, QueryType.NAPTR, cancellationToken: ct);
            // NXDOMAIN from the SML zone itself (its SOA in the answer) is "not registered"; from above it the zone is wrong or gone: unknown.
            if (answer.Header.ResponseCode == DnsHeaderResponseCode.NotExistentDomain)
                return answer.Authorities.OfType<SoaRecord>().Any(s => host.EndsWith(ZoneMarker + s.DomainName.Value.TrimEnd('.'), StringComparison.OrdinalIgnoreCase))
                    ? new(SmpLocationKind.NotRegistered, null) : new(SmpLocationKind.Unknown, null);
            if (answer.HasError) return new(SmpLocationKind.Unknown, null);
            var regexp = answer.Answers.OfType<NAPtrRecord>()
                .FirstOrDefault(r => string.Equals(r.Services, "Meta:SMP", StringComparison.OrdinalIgnoreCase))?.RegularExpression;
            // "!pattern!replacement!": the first character is the delimiter.
            var parts = string.IsNullOrEmpty(regexp) ? [] : regexp.Split(regexp[0]);
            return parts.Length >= 3 && Uri.TryCreate(parts[2], UriKind.Absolute, out var url) && url.Scheme == Uri.UriSchemeHttps
                ? new(SmpLocationKind.Found, url.AbsoluteUri) : new(SmpLocationKind.Unknown, null);
        }
        catch (Exception)
        {
            return new(SmpLocationKind.Unknown, null);
        }
    }
}

public static class PeppolReceiverStatus
{
    public const string Found = "found";
    public const string NotFound = "not_found";
    public const string Unknown = "unknown";
    public const string NoId = "no_id";
    public const string InvalidId = "invalid_id";
}

public sealed record PeppolReceiver(string Status, string? Participant, bool Derived, bool? Invoice, bool? CreditNote);

/// <summary>
/// Can this participant receive Peppol BIS Billing 3 invoices / credit notes: SML, then its SMP's ServiceGroup. Decides only from
/// those two (never the Directory). Only a definite answer is cached; an unknown is never cached and never reads as "not found".
/// Display only: never part of a document's checks, status or send.
/// </summary>
public sealed class PeppolReceiverLookup(IPeppolSmpLocator locator, IHttpClientFactory http,
    IOptions<PeppolLookupOptions> options, ILogger<PeppolReceiverLookup> log) : IDisposable
{
    // Its own bounded cache (a singleton), so no other IMemoryCache user is held to entry sizes.
    private readonly MemoryCache cache = new(new MemoryCacheOptions { SizeLimit = 10_000 });

    public void Dispose() => cache.Dispose();

    public async Task<PeppolReceiver> LookupAsync(PeppolParticipant participant, bool derived, CancellationToken ct)
    {
        var zone = options.Value.SmlZone;
        var key = $"peppol:{zone}:{participant.ToString().ToLowerInvariant()}";
        if (cache.TryGetValue(key, out PeppolReceiver? hit) && hit is not null) return hit with { Derived = derived };

        var result = await FindAsync(participant, zone, ct) with { Derived = derived };
        log.LogInformation("Peppol receiver lookup for scheme {Scheme}: {Status}", participant.Scheme, result.Status);
        if (result.Status is PeppolReceiverStatus.Found or PeppolReceiverStatus.NotFound)
            cache.Set(key, result, new MemoryCacheEntryOptions
            {
                Size = 1,
                AbsoluteExpirationRelativeToNow = result.Status == PeppolReceiverStatus.Found ? TimeSpan.FromHours(12) : TimeSpan.FromHours(1),
            });
        return result;
    }

    private async Task<PeppolReceiver> FindAsync(PeppolParticipant participant, string zone, CancellationToken ct)
    {
        var id = participant.ToString();
        PeppolReceiver As(string status, bool? invoice = null, bool? creditNote = null) => new(status, id, false, invoice, creditNote);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var smp = await locator.LocateAsync(participant.SmlHost(zone), deadline.Token);
            if (smp.Kind == SmpLocationKind.NotRegistered) return As(PeppolReceiverStatus.NotFound);
            if (smp.Kind != SmpLocationKind.Found || smp.SmpUrl is null) return As(PeppolReceiverStatus.Unknown);

            // The SMP URL comes from DNS: the guarded "delivery" client checks the address at connect and refuses redirects.
            var url = $"{smp.SmpUrl.TrimEnd('/')}/iso6523-actorid-upis%3A%3A{Uri.EscapeDataString(id.ToLowerInvariant())}";
            using var response = await http.CreateClient("delivery").GetAsync(url, deadline.Token);
            if (response.StatusCode == HttpStatusCode.NotFound) return As(PeppolReceiverStatus.NotFound);
            if (!response.IsSuccessStatusCode) return As(PeppolReceiverStatus.Unknown);

            var refs = XDocument.Parse(await response.Content.ReadAsStringAsync(deadline.Token)).Descendants()
                .Where(e => e.Name.LocalName == "ServiceMetadataReference")
                .Select(e => Uri.UnescapeDataString((string?)e.Attribute("href") ?? "")).ToList();
            return As(PeppolReceiverStatus.Found,
                refs.Any(r => r.Contains(PeppolParticipant.InvoiceDocType, StringComparison.Ordinal)),
                refs.Any(r => r.Contains(PeppolParticipant.CreditNoteDocType, StringComparison.Ordinal)));
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return As(PeppolReceiverStatus.Unknown);
        }
    }
}
