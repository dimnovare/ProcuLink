using FluentAssertions;
using ProcuLink.Transform.Parsing;

namespace ProcuLink.Transform.Tests.Parsing;

public class TextRepairTests
{
    [Theory]
    [InlineData("fÃ¼r", "für")]
    [InlineData("widerstandsfÃ¤hig", "widerstandsfähig")]
    [InlineData("â‚¬ 12", "€ 12")]
    [InlineData("SÃO PAULO", "SÃO PAULO")]
    [InlineData("Â 5", "Â 5")]
    [InlineData("für", "für")]
    [InlineData("Ã", "Ã")]
    [InlineData("CAFÉ®", "CAFÉ®")]
    [InlineData("Spaß“", "Spaß“")]
    [InlineData("2×½", "2×½")]
    public void Repairs_utf8_read_as_windows_1252_and_leaves_real_text_alone(string input, string expected) =>
        TextRepair.RepairMojibake(input).Should().Be(expected);
}
