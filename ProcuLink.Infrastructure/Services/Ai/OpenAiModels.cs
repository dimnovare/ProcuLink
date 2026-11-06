using System.Text.RegularExpressions;
using OpenAI.Chat;

namespace ProcuLink.Infrastructure.Services.Ai;

public static class OpenAiModels
{
    // The o-series and gpt-5 onward think before they answer and accept reasoning_effort;
    // gpt-4o and gpt-4.1 do not know the field and answer HTTP 400 to it.
    public static bool IsReasoningModel(string model) => Reasoning.IsMatch(model);

    /// <summary>The default model for every AI call: OpenAI's named replacement for gpt-4o-mini,
    /// which shuts down on 14 Jan 2027.</summary>
    public const string Default = "gpt-6-luna";

    /// <summary>
    /// Every call here extracts or maps; none needs the model to think first. A reasoning model left
    /// at its default effort ("medium") bills the thinking as output and spends the output cap on it.
    /// gpt-5.1 and later accept "none"; earlier reasoning models stop at "low". Never assigns null:
    /// the SDK setter throws on it.
    /// </summary>
    public static void UseLeastReasoning(ChatCompletionOptions options, string model)
    {
        if (!IsReasoningModel(model)) return;
#pragma warning disable OPENAI001 // reasoning effort is marked evaluation-only in the SDK
        options.ReasoningEffortLevel = AcceptsNone.IsMatch(model) ? new ChatReasoningEffortLevel("none") : ChatReasoningEffortLevel.Low;
#pragma warning restore OPENAI001
    }

    private static readonly Regex Reasoning = new(@"^(o\d|gpt-[5-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex AcceptsNone = new(@"^gpt-(5\.[1-9]|[6-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
