namespace ProcuLink.Core.Documents;

public static class FlowAutomation
{
    /// <summary>A document with nothing marked is sent by itself; one with a mark waits for a person. New flows start here.</summary>
    public const string Marked = "marked";
    public const string Review = "review";
    public const string Auto = "auto";

    public static readonly IReadOnlyList<string> All = [Marked, Review, Auto];

    /// <summary>True when the flow sends some documents without a person: <see cref="Auto"/> and <see cref="Marked"/>.</summary>
    public static bool SendsByItself(string automation) => automation is Auto or Marked;
}
