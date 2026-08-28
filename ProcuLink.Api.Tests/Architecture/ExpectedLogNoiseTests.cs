using Microsoft.EntityFrameworkCore;
using ProcuLink.Api.Telemetry;
using ProcuLink.Core.Entities;
using ProcuLink.Infrastructure;
using Sentry;

namespace ProcuLink.Api.Tests.Architecture;

/// <summary>
/// The handled signup race must stop being filed as a Sentry error — and nothing else may stop
/// with it.
///
/// <para>The positive case is one line; the negative controls are the test. A filter that drops
/// too much is worse than the noise it removes, because what it removes is invisible: the four
/// events this targets were themselves only found by reading Sentry directly, not by anything
/// failing.</para>
/// </summary>
public class ExpectedLogNoiseTests
{
    /// The real message, from production issue 123401779. Parameters arrive already redacted to
    /// '?' by EF's own logging, which is why this is safe to keep verbatim.
    private const string RealMessage =
        "Failed executing DbCommand (26ms) [Parameters=[@p0='?' (DbType = Guid), @p1='?', @p2='?', " +
        "@p3='?' (DbType = DateTime), @p4='?', @p5='?' (DbType = DateTime)], CommandType='Text', " +
        "CommandTimeout='30']\r\nINSERT INTO organisations (id, account_status, billing_email, " +
        "clerk_org_id, created_at, name, plan, slug)\r\nVALUES (@p0, @p1, @p2, @p3, @p4, @p5, @p6, @p7);";

    private static SentryEvent EfCommandError(string message) => new()
    {
        Logger  = ExpectedLogNoise.EfCommandLogger,
        Message = new SentryMessage { Message = message, Formatted = message },
    };

    // ── The one thing it must drop ───────────────────────────────────────────

    [Fact]
    public void Drops_theHandledSignupRace_asProductionActuallyReportsIt()
    {
        Assert.True(ExpectedLogNoise.ShouldDrop(EfCommandError(RealMessage)));
    }

    [Fact]
    public void Drops_itWhenTheLoggerArrivesAsATagRatherThanTheLoggerField()
    {
        var e = new SentryEvent { Message = new SentryMessage { Formatted = RealMessage } };
        e.SetTag("logger", ExpectedLogNoise.EfCommandLogger);

        Assert.True(ExpectedLogNoise.ShouldDrop(e));
    }

    // ── Everything it must NOT drop ──────────────────────────────────────────

    [Fact]
    public void Keeps_aFailedInsertIntoAnyOtherTable()
    {
        var other = RealMessage.Replace("INSERT INTO organisations", "INSERT INTO purchase_orders");

        Assert.False(ExpectedLogNoise.ShouldDrop(EfCommandError(other)));
    }

    [Fact]
    public void Keeps_theSameInsertWhenAnExceptionIsAttached()
    {
        // This is the conjunct that makes the filter safe. An organisations INSERT that genuinely
        // failed — not the race — propagates, and Sentry captures it WITH a type and a stack. The
        // filter must refuse that event even though its message is identical.
        var e = EfCommandError(RealMessage);
        e.SentryExceptions = new[]
        {
            new global::Sentry.Protocol.SentryException
            {
                Type  = "DbUpdateException",
                Value = "An error occurred while saving the entity changes.",
            },
        };

        Assert.False(ExpectedLogNoise.ShouldDrop(e));
    }

    [Fact]
    public void Keeps_anOrganisationsInsertReportedByAnyOtherLogger()
    {
        var e = new SentryEvent
        {
            Logger  = "ProcuLink.Api.Middleware.TenantResolutionMiddleware",
            Message = new SentryMessage { Formatted = RealMessage },
        };

        Assert.False(ExpectedLogNoise.ShouldDrop(e));
    }

    [Fact]
    public void Keeps_anOrdinaryEventWithNoMessageAtAll()
    {
        Assert.False(ExpectedLogNoise.ShouldDrop(new SentryEvent()));
        Assert.False(ExpectedLogNoise.ShouldDrop(null));
    }

    // ── The wiring, not just the predicate ───────────────────────────────────

    [Fact]
    public void TheAttachedBeforeSend_dropsTheRace_andStillRedactsEverythingElse()
    {
        var options = new SentryOptions();
        options.UseProcuLinkScrubbing();
        var beforeSend = SentryScrubbingHostWiringTests
            .Internal<Func<SentryEvent, SentryHint, SentryEvent?>>(options, "BeforeSendInternal");
        Assert.NotNull(beforeSend);

        Assert.Null(beforeSend!(EfCommandError(RealMessage), new SentryHint()));

        // Anti-vacuity: the same hook still passes an ordinary event through. A BeforeSend that
        // returned null for everything would satisfy the line above and delete the dashboard.
        const string ordinary = "Delivery failed: HTTP 502 from https://api.supplier.example.com/inbound/orders";
        var kept = beforeSend(new SentryEvent { Message = new SentryMessage { Formatted = ordinary } }, new SentryHint());
        Assert.NotNull(kept);
        Assert.Equal(ordinary, kept!.Message!.Formatted);
    }

    // ── The constant cannot drift from the schema ────────────────────────────

    [Fact]
    public void TheTableNameMatchesWhatEfActuallyMaps()
    {
        // Renaming the organisations table would leave the filter matching a string that no
        // message contains any more — it would stop working and nothing would say so, because a
        // filter that drops nothing looks exactly like a filter with nothing to drop. This is the
        // only way that failure becomes loud.
        //
        // Npgsql rather than the in-memory provider: table names are a RELATIONAL concept and the
        // in-memory model has none. Nothing connects — only the model is built.
        using var context = new ProcuLinkDbContext(
            new DbContextOptionsBuilder<ProcuLinkDbContext>()
                .UseNpgsql("Host=model-only;Database=model-only")
                .Options);

        var mapped = context.Model.FindEntityType(typeof(Organisation))?.GetTableName();

        Assert.Equal(ExpectedLogNoise.OrganisationsTable, mapped);
    }
}
