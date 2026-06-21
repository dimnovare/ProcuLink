using ProcuLink.Transform.Output;
using ProcuLink.Transform.Tokenizing;

namespace ProcuLink.Api.Contracts;

/// <summary>
/// Wire shape for <c>GET /api/orders/{id}/source-tokens</c>. Mirrors <see cref="SourceToken"/>
/// (the tokenizer / persisted-capture record) and adds <see cref="RelativeId"/> — the per-line
/// RELATIVE alias key (F-1 Phase 4) for a line-scoped token, or <c>null</c> for a header / no-ordinal
/// token. The FE output picker offers a line column as a "per line" binding by writing
/// <see cref="RelativeId"/> as the rule's <c>sourceToken</c> (so ONE rule emits each line's own value),
/// with the absolute <see cref="Id"/> available as the "exact cell" advanced option.
///
/// <para><see cref="RelativeId"/> is computed via the SAME <see cref="SourceTokenLineIndexer.RelativeLineKey"/>
/// helper the override injector uses, so the FE-offered key and the BE-resolved key can never drift.</para>
/// </summary>
public record SourceTokenDto(string Id, string Label, string Value, string? Group, string? RelativeId)
{
    public static SourceTokenDto From(SourceToken t) =>
        new(t.Id, t.Label, t.Value, t.Group, SourceTokenLineIndexer.RelativeLineKey(t.Id));

    public static IReadOnlyList<SourceTokenDto> FromMany(IReadOnlyList<SourceToken> tokens)
    {
        var result = new List<SourceTokenDto>(tokens.Count);
        foreach (var t in tokens)
            result.Add(From(t));
        return result;
    }
}
