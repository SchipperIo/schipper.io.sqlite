namespace Schipper.Io.Sqlite;

/// <summary>One executed statement, as reported to an observer.</summary>
/// <param name="Sql">The statement text. Developer-authored, so it carries no caller data.</param>
/// <param name="ElapsedMs">How long the execution took.</param>
public readonly record struct SqliteCommandExecuted(string Sql, double ElapsedMs);

/// <summary>
/// An opt-in tap on every statement this layer executes.
///
/// <b>Why it exists.</b> Wall-clock timing alone cannot see the problem worth finding: a screen that
/// issues sixty statements to show twenty orders is not slow for any reason a stopwatch explains, and
/// it is invisible until someone counts. The old EF Core codebase got this from a
/// <c>DbCommandInterceptor</c>; there is no such thing here, so the layer reports it itself.
///
/// <b>Off by default, and free when off.</b> <see cref="Observer"/> is a null field read on each
/// execution — no allocation, no timestamp taken, nothing measured. Turning it on is what a profiling
/// run does; nothing in the application does.
///
/// <b>Static, deliberately.</b> The question is "how many statements did this unit of work issue",
/// and a unit of work opens and closes several sessions. Per-session counters would answer a question
/// nobody is asking. That means an observer sees every thread's statements, which is correct for a
/// single-threaded profiling run and wrong for production telemetry.
/// </summary>
public static class SqliteMetrics
{
    /// <summary>Called after every statement, or null when nothing is listening.</summary>
    public static Action<SqliteCommandExecuted>? Observer { get; set; }

    /// <summary>Whether anything is listening. Checked before a stopwatch is started.</summary>
    public static bool IsObserved => Observer is not null;

    /// <summary>Reports an execution. Swallows an observer's own failure.</summary>
    public static void Report(string sql, double elapsedMs)
    {
        var observer = Observer;

        if (observer is null)
        {
            return;
        }

        try
        {
            observer(new SqliteCommandExecuted(sql, elapsedMs));
        }
        catch
        {
            // A profiler that throws must not take the application down with it. There is nowhere
            // useful to log this from inside the data layer, and the failure is the observer's.
        }
    }
}
