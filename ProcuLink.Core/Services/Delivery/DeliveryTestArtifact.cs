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
///
/// <para><b>Why a test fire sets an operator's subject/body template aside.</b> The email channels
/// let an operator configure <c>subjectTemplate</c> / <c>bodyTemplate</c>, and on a test fire those
/// are deliberately NOT rendered — <see cref="EmailSubject"/> and <see cref="EmailBody"/> are sent
/// instead. Both alternatives are worse. Rendering the template with test values reproduces the
/// exact defect this exists to end: the operator's own wording says a purchase order is attached,
/// and no marker bolted onto it can unsay their sentence — <c>"PO {poNumber} from Acme"</c> merely
/// becomes <c>"PO proculink-test from Acme"</c>, which still reads as an order at the supplier's
/// intake desk. Nor is the alternative a real loss: a template is a function of an ORDER, and a test
/// has none, so <c>{poNumber}</c> could only ever be filled with a fake. Rendering it would not
/// preview what a supplier actually receives for a real order, so it proves nothing while risking
/// everything. Setting it aside silently would be its own failure, so it is not silent: the delivery
/// UI states that the test does not use the configured subject and body.
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
    /// The subject an email/SMTP test fire sends, in place of the channel's ordinary
    /// <c>"Purchase Order {name}"</c> default.
    ///
    /// <para>
    /// The default was written for the only thing that used to travel this path — an actual order —
    /// and the test fire inherited it. The result landed at the supplier's real order-intake address
    /// as <c>"Purchase Order proculink-test"</c>: a human reads it as a PO with a mangled number, and
    /// an intake rule keyed on that prefix files it as one. The subject has to disqualify itself in
    /// the part of the message a recipient sees before opening anything, which is why the denial is
    /// in the subject and not only in the body.
    /// </para>
    /// </summary>
    public const string EmailSubject = "ProcuLink connection test — this is not a purchase order";

    /// <summary>
    /// The body an email/SMTP test fire sends. Says what the message is, what the attachment is, and
    /// what the recipient should do (nothing) — the supplier did not ask for this mail and has no
    /// context for it, so it must carry its own explanation.
    /// </summary>
    public const string EmailBody =
        "This is an automated connection test from ProcuLink. It is not a purchase order, and there "
        + "is no order behind it.\r\n\r\n"
        + "A buyer is setting up or checking the delivery connection that sends their purchase orders "
        + "to this address. The attached file (" + FileName + ") is a fixed two-line sample with no "
        + "business content in it.\r\n\r\n"
        + "Nothing is needed from you and you can delete this message. If you were not expecting it, "
        + "the buyer who sent it can tell you why.";

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
