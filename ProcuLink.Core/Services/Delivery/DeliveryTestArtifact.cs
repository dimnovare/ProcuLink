namespace ProcuLink.Core.Services.Delivery;

/// <summary>
/// The fixed document behind "Send a test now". It is NOT an order: the same bytes, the same name,
/// every time, for every supplier — <c>TestFireAsync</c> builds it with no order in hand at all.
///
/// <para>
/// It lives here, shared, because the file-drop dispatchers have to be able to tell it apart from a
/// purchase order. Their overwrite refusal explains WHY something is already on the remote path,
/// and for an order that explanation is "the path belongs to this order and no other, so what is
/// there is almost certainly this same PO already delivered". Said about a repeat test fire, every
/// word of that is false — there is no order, and what is there is the previous test.
/// </para>
/// <para>
/// Recognition is by name, and the name cannot be produced by an order: a file-drop order name is
/// <c>{sanitised PO}-{8 hex of the order id}.{ext}</c> (<c>DeliveryService.BuildFileName</c>), and
/// no 8-hex qualifier spells <c>test</c>.
/// </para>
/// </summary>
public static class DeliveryTestArtifact
{
    /// <summary>The name the test fire is sent under, on every channel.</summary>
    public const string FileName = "proculink-test.csv";

    /// <summary>The media type the test fire declares.</summary>
    public const string ContentType = "text/csv";

    /// <summary>The two-line CSV body the test fire sends.</summary>
    public const string Body = "test,from\r\nproculink,true\r\n";

    /// <summary>
    /// True when a remote path is the test artifact's rather than an order's. Matches the last
    /// segment only, so it holds for whatever remote directory the operator configured.
    /// </summary>
    public static bool IsAtPath(string? remotePath)
    {
        if (string.IsNullOrWhiteSpace(remotePath)) return false;

        var lastSegment = remotePath[(remotePath.LastIndexOf('/') + 1)..];
        return string.Equals(lastSegment, FileName, StringComparison.OrdinalIgnoreCase);
    }
}
