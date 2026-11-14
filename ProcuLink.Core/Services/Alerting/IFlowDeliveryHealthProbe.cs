namespace ProcuLink.Core.Services.Alerting;

/// <summary>Counts, across all organisations, flow deliveries that dead-lettered or are stuck.</summary>
public interface IFlowDeliveryHealthProbe
{
    Task<FlowDeliveryHealthSignal> GetSignalAsync(CancellationToken ct);
}

/// <summary>Dead letters inside the trailing window, and deliveries still unfinished after the stuck age.</summary>
public sealed record FlowDeliveryHealthSignal(int DeadLettered, int WindowMinutes, int Stuck, int StuckAfterMinutes);
