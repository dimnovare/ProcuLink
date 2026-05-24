using FluentAssertions;
using ProcuLink.Transform.Mapping;
using ProcuLink.Transform.Mapping.Manipulators;

namespace ProcuLink.Transform.Tests.Mapping;

public class ManipulatorTests
{
    // Replace
    [Fact]
    public void Replace_SubstitutesAllOccurrences()
    {
        var m = new ReplaceManipulator(new[] { "/", "-" });
        m.Apply("01/02/2024", row: null!).Should().Be("01-02-2024");
    }

    [Fact]
    public void Replace_WhenFindNotPresent_ReturnsOriginal()
    {
        var m = new ReplaceManipulator(new[] { "X", "Y" });
        m.Apply("hello", row: null!).Should().Be("hello");
    }

    // Trim
    [Fact]
    public void Trim_RemovesLeadingAndTrailingWhitespace()
    {
        var m = new TrimManipulator(Array.Empty<string>());
        m.Apply("  hello  ", row: null!).Should().Be("hello");
    }

    [Fact]
    public void Trim_NullInput_ReturnsEmpty()
    {
        var m = new TrimManipulator(Array.Empty<string>());
        m.Apply(null, row: null!).Should().Be(string.Empty);
    }

    // Registry
    [Fact]
    public void Registry_Resolve_KnownType_ReturnsInstance()
    {
        var m = ManipulatorRegistry.Resolve("Replace", new[] { "a", "b" });
        m.Should().BeOfType<ReplaceManipulator>();
    }

    [Fact]
    public void Registry_Resolve_UnknownType_ThrowsInvalidOperationException()
    {
        var act = () => ManipulatorRegistry.Resolve("NonExistent", Array.Empty<string>());
        act.Should().Throw<InvalidOperationException>().WithMessage("*NonExistent*");
    }
}
