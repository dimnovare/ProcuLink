using System.Linq.Expressions;
using ProcuLink.Core.Documents;
using ProcuLink.Core.Entities;

namespace ProcuLink.Api.Services.Documents;

/// <summary>The one text match for documents, shared by the documents list and the activity list.</summary>
public static class DocumentSearch
{
    public const int MaxTermLength = 200;

    /// <summary>Null for a blank term. Otherwise: the file name contains the term (any case), or the document's number
    /// contains it (spaces and case ignored), or, for 3 characters or more, the sender's name contains it (any case).
    /// A dedup key that starts with the file-hash prefix is never a number.</summary>
    public static Expression<Func<FlowDocument, bool>>? Matches(string? term)
    {
        term = term?.Trim();
        if (string.IsNullOrEmpty(term)) return null;
        if (term.Length > MaxTermLength) term = term[..MaxTermLength];
        // ToLower().Contains on a parameter translates on Npgsql (% and _ stay plain characters) and runs on EF InMemory alike.
        var name = term.ToLowerInvariant();
        var number = DocumentChecks.NormalizeNumber(term);
        var sender = term.Length >= 3;
        return d => d.FileName.ToLower().Contains(name)
            || (d.DedupKey != null && !d.DedupKey.StartsWith(DocumentChecks.FileHashKeyPrefix) && d.DedupKey.Contains(number))
            || (sender && SenderIdentity.SenderNames(d.ReadJson).ToLower().Contains(name));
    }
}
