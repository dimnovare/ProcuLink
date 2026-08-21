using System.Collections.Concurrent;
using ProcuLink.Core.Services.Alerting;

namespace ProcuLink.Infrastructure.Services;

/// <summary>
/// Process-local <see cref="IWorkerHealthAlertStateStore"/>. This is the pre-fix behaviour, kept
/// only as the default for unit tests and for any container that has no database: it survives
/// sweeps and does not survive a restart, which is precisely the defect the database-backed store
/// exists to fix. Production must resolve <see cref="DbWorkerHealthAlertStateStore"/>.
/// </summary>
public sealed class InMemoryWorkerHealthAlertStateStore : IWorkerHealthAlertStateStore
{
    private readonly ConcurrentDictionary<string, WorkerHealthAlertConditionState> _rows =
        new(StringComparer.Ordinal);

    public Task<IReadOnlyList<WorkerHealthAlertConditionState>> LoadAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<WorkerHealthAlertConditionState>>(_rows.Values.ToList());

    public Task SaveAsync(IReadOnlyCollection<WorkerHealthAlertConditionState> states, CancellationToken ct)
    {
        foreach (var state in states)
            _rows[state.AlertKey] = state;

        return Task.CompletedTask;
    }
}
