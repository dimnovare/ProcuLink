namespace ProcuLink.Core.Services.Alerting;

/// <summary>Counts, across all organisations, flow deliveries that dead-lettered or are stuck.</summary>
public interface IFlowDeliveryHealthProbe
{
    Task<FlowDeliveryHealthSignal> GetSignalAsync(CancellationToken ct);
}

/// <summary>
/// Dead letters inside the trailing window, counted per failure kind and organisation, with the latest few for the operator
/// message; and deliveries still unfinished after the stuck age.
/// </summary>
public sealed record FlowDeliveryHealthSignal(
    IReadOnlyList<FlowDeadLetterCount> DeadLetters, IReadOnlyList<FlowDeadLetterRow> Latest, int WindowMinutes, int Stuck, int StuckAfterMinutes);

/// <summary>Dead letters of one failure kind (null: unrecorded) in one organisation.</summary>
public sealed record FlowDeadLetterCount(string? Kind, Guid OrgId, int Count);

/// <summary>One recent dead letter, as the operator message names it.</summary>
public sealed record FlowDeadLetterRow(string OrgSlug, string Protocol, string? Kind, string? LastError);
