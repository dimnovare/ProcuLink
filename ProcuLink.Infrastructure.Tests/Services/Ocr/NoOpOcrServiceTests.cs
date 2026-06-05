using System.Text;
using FluentAssertions;
using ProcuLink.Infrastructure.Services.Ocr;

namespace ProcuLink.Infrastructure.Tests.Services.Ocr;

/// <summary>
/// Tests for <see cref="NoOpOcrService"/> — the default OCR seam now that the paid
/// Azure Document Intelligence provider has been removed. PDF parsing's primary path
/// is text → LLM structured extraction; this seam is kept for the planned self-hosted
/// no-egress OCR engine and is always a safe no-op until one is wired.
/// </summary>
public class NoOpOcrServiceTests
{
    [Fact]
    public async Task NoOpOcrService_IsAvailableFalse_AndAlwaysReturnsEmpty()
    {
        var noop = new NoOpOcrService();

        noop.IsAvailable.Should().BeFalse();

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("anything"));
        var result = await noop.ExtractTextAsync(stream, "application/pdf", CancellationToken.None);

        result.Should().BeEmpty();
    }
}
