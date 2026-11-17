using System.Text;
using FluentAssertions;
using ProcuLink.Core.Services;
using ProcuLink.Infrastructure.Storage;

namespace ProcuLink.Infrastructure.Tests.Storage;

public class LocalFileStorageListTests
{
    [Fact]
    public async Task ListKeysAsync_ReturnsOnlyKeysUnderThePrefix()
    {
        IFileStorageService storage = new LocalFileStorageService();
        var root = $"list-test-{Guid.NewGuid():N}";
        string[] keys = [$"{root}/documents/d1/x.pdf", $"{root}/documents/d1/outputs/y.csv", $"{root}/documents/d2/z.pdf"];
        try
        {
            foreach (var key in keys)
                await storage.UploadAsync(new MemoryStream(Encoding.UTF8.GetBytes("x")), key, "text/plain", default);

            var listed = await storage.ListKeysAsync($"{root}/documents/d1/", default);

            listed.Should().BeEquivalentTo(keys[0], keys[1]);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(LocalFileStorageService.GetFullPath($"{root}/x"))!, recursive: true);
        }
    }
}
