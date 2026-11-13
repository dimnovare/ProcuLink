using ProcuLink.Core.Documents;
using ProcuLink.Transform.Documents;

namespace ProcuLink.Api.Services.Documents;

/// <summary>
/// Official-rule checks for a flow's Peppol outputs. A broken rule is a visible check, never a block: an unreachable checker,
/// a render failure (the send reports it as output_invalid) and a pass all add nothing.
/// </summary>
public static class OutputRuleChecks
{
    /// <summary>
    /// One batch of rechecks: after the checker is found unreachable, or the batch has used its time budget, the rest of the batch does not ask again.
    /// </summary>
    public sealed class Batch(TimeSpan? budget = null)
    {
        private readonly long _start = Environment.TickCount64;
        private readonly TimeSpan _budget = budget ?? TimeSpan.FromSeconds(4);
        private bool _down;

        public bool CheckerDown
        {
            get => _down || Environment.TickCount64 - _start >= _budget.TotalMilliseconds;
            set => _down = value;
        }
    }

    private const int Listed = 3;

    public static async Task<IReadOnlyList<DocumentCheck>> RunAsync(
        DocumentEngineGate gate, IStandardValidator validator, Guid orgId, ReadResult read, FlowConfig config, CancellationToken ct, Batch? batch = null)
    {
        var failed = new List<DocumentCheck>();
        if (!gate.PeppolOn(orgId) || batch is { CheckerDown: true }) return failed;

        var header = DocumentRows.Build(read, null).Header;
        foreach (var o in config.Outputs)
        {
            if (StandardOutputs.Find(o.Standard) is not { Vesid: { } vesid } standard) continue;
            // The same outputs a send would render.
            if (!SendWhenOps.Matches(o.SendWhenJson is null ? null : DocumentJson.Deserialize<SendWhen>(o.SendWhenJson), header)) continue;

            var render = await StandardOutputs.RenderAsync(standard.Id, read, config.Fields, o.TableKey, orgId, ct);
            if (!render.Ok) continue;
            using var buffer = new MemoryStream();
            await render.Result!.Content.CopyToAsync(buffer, ct);

            var outcome = await validator.ValidateAsync(vesid, buffer.ToArray(), ct);
            if (outcome.Status == ValidationStatus.Unavailable)
            {
                if (batch is not null) batch.CheckerDown = true;
                break;
            }
            if (outcome.Status != ValidationStatus.Failed) continue;
            var errors = outcome.Issues.Where(i => i.Severity == "error").ToList();
            if (errors.Count == 0) continue;
            var message = $"{o.Name} doesn't meet {errors.Count} {standard.RuleSet} rule(s): " + string.Join("; ", errors.Take(Listed).Select(i => $"{i.RuleId} {i.Message}"))
                + (errors.Count > Listed ? $" and {errors.Count - Listed} more" : "");
            failed.Add(new DocumentCheck(DocumentChecks.OutputRules, false, null, message));
        }
        return failed;
    }
}
