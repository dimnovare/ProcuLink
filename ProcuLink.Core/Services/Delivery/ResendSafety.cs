namespace ProcuLink.Core.Services.Delivery;

/// <summary>
/// Whether re-sending the SAME artifact after an UNKNOWN outcome can duplicate at the
/// counterparty. Consulted only on a crash-recovery re-drive (a re-adopted in-flight
/// <c>dispatching</c> attempt row) — never on a first send, and never on a send whose
/// outcome was actually observed.
/// </summary>
public enum ResendSafety
{
    /// <summary>
    /// Re-sending cannot duplicate: the channel writes to a path that is a deterministic function of
    /// the order (SFTP/FTPS), so a re-send targets its own file and never opens a second one.
    ///
    /// <para>
    /// It does NOT say the re-send succeeds. When the operator has turned <c>overwriteExisting</c>
    /// off for that supplier, a re-send whose predecessor already wrote REFUSES — no duplicate, but
    /// no repair either, and the order would be reported failed while the supplier may already hold
    /// the document. <c>DeliveryService</c> therefore parks that specific combination (file-drop
    /// channel + overwrite off + crash-recovery re-drive) for a human instead of re-driving it.
    /// </para>
    /// </summary>
    Safe,

    /// <summary>
    /// A dedupe signal IS transmitted, but honouring it is the counterparty's choice (HTTP
    /// <c>Idempotency-Key</c>). Re-drive; the residual is documented, not silently assumed away.
    /// </summary>
    BestEffort,

    /// <summary>
    /// No dedupe signal reaches the counterparty (ERP endpoints ignore the key; a caller-supplied
    /// email <c>Message-ID</c> is rarely honoured by a receiving MTA). A re-send after an unknown
    /// outcome duplicates the PO, so DeliveryService parks the order for a human decision instead.
    /// The interface default — a channel that has not thought about this parks rather than duplicates.
    /// </summary>
    Unsafe,
}
