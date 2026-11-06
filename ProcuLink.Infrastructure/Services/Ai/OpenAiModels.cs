using System.Text.RegularExpressions;

namespace ProcuLink.Infrastructure.Services.Ai;

public static class OpenAiModels
{
    // The o-series and gpt-5 onward think before they answer and accept reasoning_effort;
    // gpt-4o and gpt-4.1 do not know the field and answer HTTP 400 to it.
    public static bool IsReasoningModel(string model) => Reasoning.IsMatch(model);

    private static readonly Regex Reasoning = new(@"^(o\d|gpt-[5-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
