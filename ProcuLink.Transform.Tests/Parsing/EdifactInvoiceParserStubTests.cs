using FluentAssertions;
using ProcuLink.Transform.Parsing;

namespace ProcuLink.Transform.Tests.Parsing;

public class EdifactInvoiceParserStubTests
{
    [Fact]
    public void CanParse_EdiExtension_ReturnsTrue()
        => new EdifactInvoiceParser().CanParse(".edi").Should().BeTrue();

    [Fact]
    public async Task ParseAsync_AlwaysThrowsNotImplemented()
    {
        var parser = new EdifactInvoiceParser();
        using var stream = new MemoryStream();

        var act = () => parser.ParseAsync(stream, default);
        await act.Should().ThrowAsync<NotImplementedException>()
                 .WithMessage("*EdiFabric*");
    }
}
