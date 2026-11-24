namespace ProcuLink.Infrastructure.Services.Microsoft;

/// <summary>Section <c>Microsoft</c> (env <c>Microsoft__ClientId</c>, <c>Microsoft__ClientSecret</c>). Blank = the Microsoft features stay invisible.</summary>
public sealed class MicrosoftOptions
{
    public const string Section = "Microsoft";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
}
