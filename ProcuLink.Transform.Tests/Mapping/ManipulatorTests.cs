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

    // DateFormat
    [Fact]
    public void DateFormat_ConvertsFromInputFormatToOutput()
    {
        var m = new DateFormatManipulator(new[] { "dd/MM/yyyy", "yyyy-MM-dd" });
        m.Apply("24/05/2026", row: null!).Should().Be("2026-05-24");
    }

    [Fact]
    public void DateFormat_InvalidDate_ReturnsOriginal()
    {
        var m = new DateFormatManipulator(new[] { "dd/MM/yyyy", "yyyy-MM-dd" });
        m.Apply("not-a-date", row: null!).Should().Be("not-a-date");
    }

    [Fact]
    public void DateFormat_NullInput_ReturnsNull()
    {
        var m = new DateFormatManipulator(new[] { "dd/MM/yyyy", "yyyy-MM-dd" });
        m.Apply(null, row: null!).Should().BeNull();
    }

    // NumberFormat
    [Fact]
    public void NumberFormat_InvariantGrouping_TwoDecimals()
    {
        var m = new NumberFormatManipulator(new[] { "N2" });
        m.Apply("1234.5", row: null!).Should().Be("1,234.50");
    }

    [Fact]
    public void NumberFormat_WithCulture_UsesThatGroupingAndDecimal()
    {
        var m = new NumberFormatManipulator(new[] { "N2", "de-DE" });
        m.Apply("1234.5", row: null!).Should().Be("1.234,50");   // EU grouping
    }

    [Fact]
    public void NumberFormat_CurrencyFormat_WithCulture()
    {
        var m = new NumberFormatManipulator(new[] { "C2", "en-US" });
        m.Apply("1234.5", row: null!).Should().Be("$1,234.50");
    }

    [Fact]
    public void NumberFormat_NonNumericInput_ReturnedUnchanged()
    {
        var m = new NumberFormatManipulator(new[] { "N2" });
        m.Apply("not-a-number", row: null!).Should().Be("not-a-number");
    }

    [Fact]
    public void NumberFormat_BlankInput_ReturnedUnchanged()
    {
        var m = new NumberFormatManipulator(new[] { "N2" });
        m.Apply("", row: null!).Should().Be("");
        m.Apply(null, row: null!).Should().BeNull();
    }

    [Fact]
    public void NumberFormat_UnknownCulture_DoesNotThrow()
    {
        // .NET (ICU) may synthesise a culture for an unknown name rather than throwing; either way the
        // manipulator must never throw and must still produce a numeric string.
        var m = new NumberFormatManipulator(new[] { "N2", "zz-ZZ" });
        var act = () => m.Apply("1234.5", row: null!);
        act.Should().NotThrow();
        m.Apply("1234.5", row: null!).Should().Contain("234");
    }

    [Fact]
    public void NumberFormat_RequiresOneOrTwoParams()
    {
        var act = () => new NumberFormatManipulator(Array.Empty<string>());
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Registry_Resolve_NumberFormat_ReturnsInstance()
    {
        var m = ManipulatorRegistry.Resolve("NumberFormat", new[] { "N2" });
        m.Should().BeOfType<NumberFormatManipulator>();
    }

    // Concat
    [Fact]
    public void Concat_JoinsColumnsWithSeparator()
    {
        var row = new Dictionary<string, string> { ["first"] = "Hello", ["second"] = "World" };
        var m = new ConcatManipulator(new[] { " ", "first", "second" });
        m.Apply(null, row).Should().Be("Hello World");
    }

    [Fact]
    public void Concat_MissingColumn_TreatedAsEmpty()
    {
        var row = new Dictionary<string, string> { ["first"] = "A" };
        var m = new ConcatManipulator(new[] { "-", "first", "missing" });
        m.Apply(null, row).Should().Be("A-");
    }

    // Fallback
    [Fact]
    public void Fallback_ReturnsFirstNonEmptyColumnValue()
    {
        var row = new Dictionary<string, string> { ["a"] = "", ["b"] = "found", ["c"] = "other" };
        var m = new FallbackManipulator(new[] { "a", "b", "c" });
        m.Apply(null, row).Should().Be("found");
    }

    [Fact]
    public void Fallback_AllEmpty_ReturnsNull()
    {
        var row = new Dictionary<string, string> { ["a"] = "", ["b"] = "" };
        var m = new FallbackManipulator(new[] { "a", "b" });
        m.Apply(null, row).Should().BeNull();
    }

    // Split
    [Fact]
    public void Split_ReturnsTokenAtIndex()
    {
        var m = new SplitManipulator(new[] { "/", "2" });
        m.Apply("01/02/2024", row: null!).Should().Be("2024");
    }

    [Fact]
    public void Split_IndexOutOfRange_ReturnsOriginal()
    {
        var m = new SplitManipulator(new[] { "/", "9" });
        m.Apply("a/b", row: null!).Should().Be("a/b");
    }

    // Multiply
    [Fact]
    public void Multiply_ScalesNumericValue()
    {
        var m = new MultiplyManipulator(new[] { "1.21" });
        m.Apply("100", row: null!).Should().Be("121");
    }

    [Fact]
    public void Multiply_NonNumericInput_ReturnsOriginal()
    {
        var m = new MultiplyManipulator(new[] { "2" });
        m.Apply("abc", row: null!).Should().Be("abc");
    }

    // Divide
    [Fact]
    public void Divide_ScalesNumericValueDown()
    {
        var m = new DivideManipulator(new[] { "100" });
        m.Apply("1000", row: null!).Should().Be("10");
    }

    [Fact]
    public void Divide_DivideByZero_ReturnsOriginal()
    {
        var m = new DivideManipulator(new[] { "0" });
        m.Apply("100", row: null!).Should().Be("100");
    }
}
