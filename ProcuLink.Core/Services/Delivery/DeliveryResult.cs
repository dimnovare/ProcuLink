namespace ProcuLink.Core.Services.Delivery;

/// <summary>Result of a single delivery dispatch attempt.</summary>
/// <param name="Success">True when the remote endpoint accepted the payload.</param>
/// <param name="ErrorMessage">Human-readable error; null on success.</param>
/// <param name="ResponseCode">HTTP response code for HTTP dispatches; null for SFTP/FTP.</param>
public record DeliveryResult(bool Success, string? ErrorMessage, int? ResponseCode = null);
