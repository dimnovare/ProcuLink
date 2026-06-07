using FluentAssertions;
using ProcuLink.Infrastructure.Storage;

namespace ProcuLink.Infrastructure.Tests.Storage;

/// <summary>
/// Path-traversal containment tests for <see cref="LocalFileStorageService.GetFullPath"/>.
///
/// Background (P2-1, dev-only): GetFullPath used to do
/// Path.GetFullPath(Path.Combine(BasePath, key)) with no check that the resolved
/// path stayed under BasePath, so a key like "../../../windows/win.ini" escaped
/// the storage root. The dev-only DevFilesController served whatever path came
/// back, turning the passthrough into an arbitrary-file-read. GetFullPath now
/// rejects keys that resolve outside the storage root.
/// </summary>
public class LocalFileStorageServiceTests
{
    private static readonly string BasePath =
        Path.Combine(Path.GetTempPath(), "proculink-dev");

    [Theory]
    [InlineData("../../../windows/win.ini")]
    [InlineData("..\\..\\..\\windows\\win.ini")]
    [InlineData("org/order/../../../../../../etc/passwd")]
    [InlineData("../proculink-dev-evil/secret.txt")] // sibling dir sharing the prefix
    public void GetFullPath_RejectsTraversalKeys(string key)
    {
        var act = () => LocalFileStorageService.GetFullPath(key);

        act.Should().Throw<ArgumentException>(
            because: $"key '{key}' resolves outside the storage root");
    }

    /// <summary>
    /// A case-variant sibling (e.g. ".../PROCULINK-DEV/secret.txt") is a DIFFERENT
    /// directory on a case-sensitive filesystem (the Linux/Debian deploy target),
    /// so it must be rejected. Under the old OrdinalIgnoreCase check it slipped
    /// through; the containment check is now Ordinal (case-sensitive) to match the
    /// real filesystem semantics.
    /// </summary>
    [Fact]
    public void GetFullPath_RejectsCaseVariantSiblingOfBasePath()
    {
        // BasePath is "{temp}/proculink-dev"; ".." escapes to "{temp}", then into
        // an upper-cased sibling "PROCULINK-DEV" that differs only by case.
        const string key = "../PROCULINK-DEV/secret.txt";

        var act = () => LocalFileStorageService.GetFullPath(key);

        act.Should().Throw<ArgumentException>(
            because: "a case-variant sibling resolves outside BasePath on a "
                   + "case-sensitive filesystem and must not satisfy the Ordinal "
                   + "containment check");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void GetFullPath_RejectsEmptyKeys(string key)
    {
        var act = () => LocalFileStorageService.GetFullPath(key);

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("org/order/artifact.xml")]
    [InlineData("a1b2/c3d4/artifacts/e5f6.csv")]
    [InlineData("simple.pdf")]
    public void GetFullPath_ResolvesNormalKeysUnderBasePath(string key)
    {
        var fullPath = LocalFileStorageService.GetFullPath(key);

        var basePrefix = BasePath.TrimEnd(Path.DirectorySeparatorChar)
                         + Path.DirectorySeparatorChar;

        fullPath.Should().StartWith(basePrefix,
            because: "a normal key must resolve to a path inside the storage root");
    }

    [Fact]
    public void GetFullPath_TranslatesForwardSlashesToPlatformSeparators()
    {
        var fullPath = LocalFileStorageService.GetFullPath("org/order/file.xml");

        var expectedTail = Path.Combine("org", "order", "file.xml");
        fullPath.Should().EndWith(expectedTail);
    }
}
