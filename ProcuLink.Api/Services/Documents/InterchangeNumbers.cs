using System.Diagnostics;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;
using ProcuLink.Transform.Output;

namespace ProcuLink.Api.Services.Documents;

/// <summary>Hands out the interchange control number for one X12 / EDIFACT file that really leaves (a send or a test).</summary>
public static class InterchangeNumbers
{
    /// <summary>Inserts a row and returns its id wrapped into 1…999,999,999. A refused send afterwards leaves a gap, which is harmless.</summary>
    public static async Task<long> NextAsync(ProcuLinkDbContext db, CancellationToken ct)
    {
        // Its SaveChanges must not commit anything else the caller has pending.
        if (db.ChangeTracker.HasChanges()) throw new UnreachableException("Allocate an interchange number before changing anything else.");
        var row = new InterchangeNumber { CreatedAt = DateTime.UtcNow };
        db.Set<InterchangeNumber>().Add(row);
        await db.SaveChangesAsync(ct);
        return InterchangeControl.Wrap(row.Id);
    }
}
