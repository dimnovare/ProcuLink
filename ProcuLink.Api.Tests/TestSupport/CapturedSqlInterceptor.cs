using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ProcuLink.Api.Tests.TestSupport;

/// <summary>
/// Records the SQL of every command the code under test actually executes, so a test can assert
/// on the query production sends rather than on a hand-written lookalike.
///
/// <para>Two kinds of claim need this. <b>How many round trips</b> an endpoint costs is invisible
/// to the in-memory provider, which issues no commands at all — an endpoint that had quietly gone
/// back to four sequential counts would pass every in-memory test it has. And <b>what the ORDER BY
/// contains</b> decides whether a Skip/Take walk is exhaustive; a tie-break column is not
/// observable from the rows of one page, because a small table usually comes back in insertion
/// order whether or not the sort is total.</para>
/// </summary>
public sealed class CapturedSqlInterceptor : DbCommandInterceptor
{
    private readonly List<string> _commands = [];

    public IReadOnlyList<string> Commands
    {
        get { lock (_commands) return _commands.ToList(); }
    }

    /// <summary>Drops everything captured so far — call it after fixture setup, before the act.</summary>
    public void Clear() { lock (_commands) _commands.Clear(); }

    /// <summary>The captured commands, joined for an assertion failure message.</summary>
    public string Describe() =>
        string.Join(Environment.NewLine + "---" + Environment.NewLine, Commands);

    private void Record(DbCommand command)
    {
        lock (_commands) _commands.Add(command.CommandText);
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    { Record(command); return result; }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    { Record(command); return ValueTask.FromResult(result); }

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    { Record(command); return result; }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    { Record(command); return ValueTask.FromResult(result); }
}
