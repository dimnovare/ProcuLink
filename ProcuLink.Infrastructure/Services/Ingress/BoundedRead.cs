using ProcuLink.Core.Services.Catalog;

namespace ProcuLink.Infrastructure.Services.Ingress;

/// <summary>
/// H2 — bounded streamed copy. Reads at most <c>maxBytes + 1</c> bytes from the source:
/// the extra byte is only used to DETECT overflow, at which point the copy aborts with
/// <see cref="CatalogFileTooLargeException"/>. A lying server (mis-declared size) or an
/// endless stream can therefore never materialize more than the cap in memory, and no
/// pre-flight SIZE/attribute probe is needed (the server could lie about those anyway).
/// </summary>
public static class BoundedRead
{
    public static async Task<MemoryStream> CopyAsync(Stream source, long maxBytes, CancellationToken ct)
    {
        var destination = new MemoryStream();
        var buffer = new byte[81920];
        long total = 0;

        while (true)
        {
            // Never request beyond cap+1 total — the overflow byte is the detector.
            var want = (int)Math.Min(buffer.Length, maxBytes + 1 - total);
            if (want <= 0)
                throw new CatalogFileTooLargeException(maxBytes);

            var read = await source.ReadAsync(buffer.AsMemory(0, want), ct).ConfigureAwait(false);
            if (read == 0) break;

            total += read;
            if (total > maxBytes)
                throw new CatalogFileTooLargeException(maxBytes);

            destination.Write(buffer, 0, read);
        }

        destination.Position = 0;
        return destination;
    }
}
