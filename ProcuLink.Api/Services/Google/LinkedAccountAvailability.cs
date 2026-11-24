using Microsoft.Extensions.Options;
using ProcuLink.Api.Services.Documents;
using ProcuLink.Core.Entities;
using ProcuLink.Core.Services.Google;
using ProcuLink.Infrastructure.Services.Google;

namespace ProcuLink.Api.Services.Google;

/// <summary>
/// Whether an org may connect and newly use a provider's accounts: the native-connector flag is on and that provider's
/// client is configured. Blank config keeps the provider invisible. The one rule every caller asks.
/// </summary>
public sealed class LinkedAccountAvailability(DocumentEngineGate gate, IEnumerable<ILinkedAccountOAuth> oauth, IOptions<GoogleOptions> google)
{
    public bool On(Guid orgId, string provider) =>
        gate.NativeConnectorsOn(orgId)
        && oauth.Any(o => o.Provider == provider && o.IsConfigured)
        // The Google destinations pick their target in Google's Picker, which needs both.
        && (provider != LinkedAccountProviders.Google
            || (!string.IsNullOrWhiteSpace(google.Value.PickerApiKey) && !string.IsNullOrWhiteSpace(google.Value.ProjectNumber)));
}
