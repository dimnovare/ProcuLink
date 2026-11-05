using FluentAssertions;
using ProcuLink.Core.Documents;
using ProcuLink.Transform.Documents;

namespace ProcuLink.Transform.Tests.Documents;

public class ValueLocatorTests
{
    [Fact]
    public void Locate_spans_multiword_value_and_matches_number_formats()
    {
        var words = new List<PageWord>
        {
            new("Acme", 1, new Box(0.10, 0.10, 0.10, 0.02)),
            new("Components", 1, new Box(0.22, 0.10, 0.20, 0.02)),
            new("Ltd", 1, new Box(0.44, 0.10, 0.05, 0.02)),
            new("2,380.00", 1, new Box(0.60, 0.50, 0.12, 0.02)),
        };

        var name = ValueLocator.Locate("Acme Components Ltd", words)!;
        name.Page.Should().Be(1);
        name.Box!.X.Should().BeApproximately(0.10, 1e-9);
        name.Box.Width.Should().BeApproximately(0.39, 1e-9);

        ValueLocator.Locate("2380.00", words)!.Box!.Y.Should().BeApproximately(0.50, 1e-9);
        ValueLocator.Locate("Nowhere Inc", words).Should().BeNull();
    }
}
