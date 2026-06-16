using System.Text;
using FluentAssertions;
using ProcuLink.Transform.Tokenizing;

namespace ProcuLink.Transform.Tests.SourceMap;

/// <summary>
/// T1d — the JSON source tokenizer. Pushed / API-ingress structured orders (the most common
/// "no draggable fields" case) must expose EVERY leaf value as an addressable, draggable token.
/// </summary>
public class SourceTokenizerJsonTests
{
    private static async Task<IReadOnlyList<SourceToken>> Tok(string json) =>
        await new SourceTokenizer().TokenizeAsync(Encoding.UTF8.GetBytes(json), ".json");

    [Fact]
    public async Task EmitsEveryLeaf_WithPointerIds_ValuesAndGroups()
    {
        const string json = """
        {"poNumber":"PO-1","currency":"EUR","total":828.20,
         "lines":[{"sku":"S-1","qty":2},{"sku":"S-2","qty":3}]}
        """;
        var tokens = await Tok(json);

        // Every leaf present: 3 header scalars + 2 lines × 2 = 7 tokens.
        tokens.Should().HaveCount(7);

        var po = tokens.Single(t => t.Id == "json:/poNumber");
        po.Value.Should().Be("PO-1");
        po.Group.Should().Be("header");

        tokens.Single(t => t.Id == "json:/currency").Value.Should().Be("EUR");
        tokens.Single(t => t.Id == "json:/total").Value.Should().Be("828.20");   // numbers via raw text

        var line0Sku = tokens.Single(t => t.Id == "json:/lines/0/sku");
        line0Sku.Value.Should().Be("S-1");
        line0Sku.Group.Should().Be("line");                                       // under an array → line
        line0Sku.Label.Should().Contain("sku");

        tokens.Single(t => t.Id == "json:/lines/1/qty").Value.Should().Be("3");
    }

    [Fact]
    public async Task ArrayRoot_IsWalked()
    {
        var tokens = await Tok("""[{"sku":"A"},{"sku":"B"}]""");
        tokens.Should().HaveCount(2);
        tokens.Select(t => t.Id).Should().Contain(new[] { "json:/0/sku", "json:/1/sku" });
        tokens.Should().OnlyContain(t => t.Group == "line");
    }

    [Fact]
    public async Task MalformedJson_ReturnsEmpty_NeverThrows()
    {
        (await Tok("{ not json")).Should().BeEmpty();
    }
}
