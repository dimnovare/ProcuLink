// ProcuLink.Api/Contracts/OrderActionResponses.cs
namespace ProcuLink.Api.Contracts;

// Typed response records for the small OrdersController success payloads that the
// frontend maps by property. These replace inline `Ok(new { ... })` anonymous
// objects with named contracts. The wire shape is unchanged: the default MVC
// System.Text.Json serializer emits camelCase property names, so these serialize
// to byte-identical JSON (e.g. { "status": "..." }, { "accepted": N }, { "count": N }).
//
// Behaviour-preserving maintainability win only — no new fields, no shape change.

/// <summary>
/// Lightweight status payload for <c>GET /api/orders/{id}/status</c>.
/// Serializes to <c>{ "status": "parsing" }</c> — consumed by the frontend status poll.
/// </summary>
public record OrderStatusResponse(string Status);

/// <summary>
/// Result of <c>POST /api/orders/{id}/accept-ai-suggestions</c>.
/// Serializes to <c>{ "accepted": N }</c> — <c>Accepted</c> is the count of lines accepted.
/// </summary>
public record AcceptAiSuggestionsResponse(int Accepted);

/// <summary>
/// Ops metric for <c>GET /api/orders/dead-letter-count</c>.
/// Serializes to <c>{ "count": N }</c> — number of orders in the delivery dead-letter state.
/// </summary>
public record DeadLetterCountResponse(int Count);
