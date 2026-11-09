namespace ProcuLink.Core.Documents;

/// <summary>Where a person showed a flow's field to be, on a document from one sender. <see cref="SenderKey"/> and <see cref="SenderLabel"/> are "" for any sender.</summary>
public sealed record FieldHint(
    string FieldKey, string SenderKey, string SenderLabel, string? Near, string Example,
    int? Page, Box? Box, string? Address, DateTime TaughtAt);

public static class FieldHints
{
    public const int MaxPerFlow = 200;

    /// <summary>Replaces the hint for the same field and sender, adds the new one last, and keeps the newest <see cref="MaxPerFlow"/>.</summary>
    public static IReadOnlyList<FieldHint> Upsert(IReadOnlyList<FieldHint> hints, FieldHint hint) =>
        [.. hints.Where(h => h.FieldKey != hint.FieldKey || h.SenderKey != hint.SenderKey).Append(hint).TakeLast(MaxPerFlow)];
}
