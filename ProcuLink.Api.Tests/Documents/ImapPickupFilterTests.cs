using FluentAssertions;
using ProcuLink.Api.Services.Documents.Intake;
using Xunit;

namespace ProcuLink.Api.Tests.Documents;

public class ImapPickupFilterTests
{
    [Theory]
    [InlineData(null, null, "x@other.test", "Anything", true)]
    [InlineData("", " ", "x@other.test", null, true)]
    [InlineData("@example.test", null, "ap@example.test", "Order", true)]
    [InlineData("@example.test", null, "x@other.test", "Order", false)]
    [InlineData("nobody@example.test, @example.test", null, "AP@Example.Test", "Order", true)]
    [InlineData("@example.test", "invoice", "ap@example.test", "Order 7", false)]
    [InlineData("@example.test", "invoice, order", "ap@example.test", "Your ORDER 7", true)]
    [InlineData(null, "order", "ap@example.test", null, false)]
    public void Matches_needs_every_set_filter_to_match(string? from, string? subject, string sender, string? title, bool expected) =>
        ImapPickupSource.Matches(from, subject, [sender], title).Should().Be(expected);
}
