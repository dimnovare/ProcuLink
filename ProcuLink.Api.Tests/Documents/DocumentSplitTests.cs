using FluentAssertions;
using ProcuLink.Core.Documents;
using Xunit;

namespace ProcuLink.Api.Tests.Documents;

public sealed class DocumentSplitTests
{
    [Fact]
    public void Four_numbers_make_four_one_page_documents()
    {
        DocumentSplit.Ranges(
            [new(1, "INV-1001", false), new(2, "INV-1002", false), new(3, "INV-1003", false), new(4, "INV-1004", false)], 4)
            .Should().Equal(new PageRange(1, 1), new PageRange(2, 2), new PageRange(3, 3), new PageRange(4, 4));
    }

    [Fact]
    public void Copies_keep_the_first_copy()
    {
        DocumentSplit.Ranges([new(1, "PO-77", false), new(2, "po-77", true), new(3, "PO-77", true)], 3)
            .Should().Equal(new PageRange(1, 1));
    }

    [Fact]
    public void Same_number_and_part_start_continue_one_document()
    {
        DocumentSplit.Ranges(
            [new(1, "A", false), new(2, "A", false), new(11, null, false), new(0, "X", false), new(99, "Y", false)], 12)
            .Should().Equal(new PageRange(1, 12));
    }
}
