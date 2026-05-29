using FluentAssertions;
using ProcuLink.Core.Services.Detection;

namespace ProcuLink.Infrastructure.Tests.Services.Detection;

public class SchemaFingerprintHasherTests
{
    [Fact]
    public void Hash_IsOrderIndependent()
    {
        var a = SchemaFingerprintHasher.ComputeColumnNameHash(new[] { "po_number", "sku", "qty" });
        var b = SchemaFingerprintHasher.ComputeColumnNameHash(new[] { "qty", "po_number", "sku" });

        b.Should().Be(a, "the same columns in a different order must fingerprint identically");
    }

    [Fact]
    public void Hash_IsCaseInsensitive()
    {
        var a = SchemaFingerprintHasher.ComputeColumnNameHash(new[] { "PO_Number", "SKU", "Qty" });
        var b = SchemaFingerprintHasher.ComputeColumnNameHash(new[] { "po_number", "sku", "qty" });

        b.Should().Be(a);
    }

    [Fact]
    public void Hash_TrimsWhitespace()
    {
        var a = SchemaFingerprintHasher.ComputeColumnNameHash(new[] { "  po_number ", "sku", "\tqty" });
        var b = SchemaFingerprintHasher.ComputeColumnNameHash(new[] { "po_number", "sku", "qty" });

        b.Should().Be(a);
    }

    [Fact]
    public void Hash_DiffersForDifferentLayouts()
    {
        var a = SchemaFingerprintHasher.ComputeColumnNameHash(new[] { "po_number", "sku", "qty" });
        var b = SchemaFingerprintHasher.ComputeColumnNameHash(new[] { "order_id", "item", "quantity" });

        b.Should().NotBe(a);
    }

    [Fact]
    public void Hash_IgnoresBlankColumns()
    {
        var a = SchemaFingerprintHasher.ComputeColumnNameHash(new[] { "po_number", "", "  ", "sku", "qty" });
        var b = SchemaFingerprintHasher.ComputeColumnNameHash(new[] { "po_number", "sku", "qty" });

        b.Should().Be(a);
    }

    [Fact]
    public void Hash_ReturnsLowercaseHexSha256()
    {
        var hash = SchemaFingerprintHasher.ComputeColumnNameHash(new[] { "po_number", "sku" });

        hash.Should().NotBeNull();
        hash!.Length.Should().Be(64);
        hash.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void Hash_ReturnsNull_ForNullEmptyOrAllBlankHeaders()
    {
        SchemaFingerprintHasher.ComputeColumnNameHash(null).Should().BeNull();
        SchemaFingerprintHasher.ComputeColumnNameHash(Array.Empty<string>()).Should().BeNull();
        SchemaFingerprintHasher.ComputeColumnNameHash(new[] { "", "  ", "\t" }).Should().BeNull();
    }
}
