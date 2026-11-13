namespace ProcuLink.Infrastructure.Services.Google;

/// <summary>Section <c>Google</c> (env <c>Google__ClientId</c> etc.). Blank = the Google destinations stay invisible.</summary>
public sealed class GoogleOptions
{
    public const string Section = "Google";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string PickerApiKey { get; set; } = "";
    public string ProjectNumber { get; set; } = "";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
}
