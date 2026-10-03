using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Schipper.Io.Sqlite;
using Schipper.Io.Sqlite.Query;

namespace Schipper.Io.Sqlite.Bench;

/// <summary>
/// Concurrent mixed-workload run against a pre-populated database.
///
/// The benchmark answers "how fast is one thread". This answers the question that actually decides
/// whether SQLite survives production: <b>what happens when several workers hit it at once.</b>
///
/// SQLite allows many concurrent readers but exactly one writer. WAL lets readers continue during a
/// write, but two writers still serialise, and the loser gets <c>SQLITE_BUSY</c> once
/// <c>busy_timeout</c> expires. Nothing in this codebase retries on that today — the PRAGMA is the
/// only backstop — so the point of this run is to find out whether that is sufficient or merely
/// untested.
///
/// Workers each own a connection, mix reads and writes in realistic proportions, and some hold a
/// transaction across several statements the way a real order-with-payment does. Every SQLite error
/// is caught and classified rather than aborting the run, because the interesting output is the
/// distribution of failures, not the first one.
/// </summary>
internal static class Chaos
{
    private const int SeedRows = 20_000;
    private const int TagCount = 20;

    public static async Task<int> RunAsync(int workers, TimeSpan duration, int? seed)
    {
        var dbPath = Path.Combine(AppContext.BaseDirectory, "chaos.db");

        foreach (var stale in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
        {
            if (File.Exists(stale)) File.Delete(stale);
        }

        var db = new SqliteDb($"Data Source={dbPath}");
        await Database.ApplyAsync(db, cancellationToken: CancellationToken.None);

        Console.WriteLine($"  seeding {SeedRows:N0} rows ...");
        var ids = await SeedAsync(db, SeedRows);

        Console.WriteLine($"  {workers} workers for {duration.TotalSeconds:N0}s ...");
        Console.WriteLine();

        using var cts = new CancellationTokenSource(duration);
        var stats = new Stats();
        var started = Stopwatch.GetTimestamp();

        var running = Enumerable.Range(0, workers)
            .Select(w => Task.Run(() => WorkerAsync(db, ids, w, seed ?? 20260801, stats, cts.Token)))
            .ToArray();

        await Task.WhenAll(running);
        var elapsed = Stopwatch.GetElapsedTime(started);

        var invariantsHold = await ReportAsync(db, stats, elapsed, workers);

        // A busy timeout that is never hit proves nothing about a machine under real load, so the
        // exit code reflects genuine failures only: anything that was not a contention timeout.
        // Invariant FAIL is a hard failure too — a cascade that stopped working must not exit 0.
        return stats.HardFailures == 0 && invariantsHold ? 0 : 1;
    }

    private static async Task WorkerAsync(SqliteDb db, string[] ids, int worker, int seed, Stats stats, CancellationToken ct)
    {
        // Per-worker deterministic RNG: the *interleaving* is chaotic, the choices are reproducible.
        var rng = new Random(seed + worker);
        var widgets = new WidgetRowRepository();
        var parts = new PartRowRepository();

        while (!ct.IsCancellationRequested)
        {
            var roll = rng.Next(100);
            var start = Stopwatch.GetTimestamp();

            try
            {
                // Roughly a till's traffic: mostly reads, a steady trickle of writes, and the
                // occasional multi-statement transaction standing in for take-payment.
                if (roll < 45)
                {
                    await ReadOneAsync(db, widgets, ids, rng, ct);
                    stats.Record(Op.Get, start);
                }
                else if (roll < 60)
                {
                    await PageAsync(db, widgets, rng, ct);
                    stats.Record(Op.Query, start);
                }
                else if (roll < 70)
                {
                    await StreamAsync(db, widgets, ct);
                    stats.Record(Op.Stream, start);
                }
                else if (roll < 85)
                {
                    await UpsertAsync(db, widgets, ids, rng, ct);
                    stats.Record(Op.Upsert, start);
                }
                else if (roll < 95)
                {
                    await MultiStatementAsync(db, widgets, parts, ids, rng, ct);
                    stats.Record(Op.Transaction, start);
                }
                else
                {
                    await ChurnAsync(db, parts, ids, rng, ct);
                    stats.Record(Op.Churn, start);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (SqliteConcurrencyException)
            {
                // Expected: two workers racing UpdateAsync on the same row. Counted separately
                // from SQLITE_BUSY so the lost-update distribution is visible instead of aborting
                // the run.
                stats.RecordConcurrency();
            }
            catch (SqliteException ex)
            {
                stats.RecordFailure(ex);
            }
        }
    }

    // --- Operations ------------------------------------------------------------------------------

    private static async Task ReadOneAsync(SqliteDb db, WidgetRowRepository widgets, string[] ids, Random rng, CancellationToken ct)
    {
        await using var session = await db.OpenAsync(ct);
        _ = await widgets.GetAsync(session, ids[rng.Next(ids.Length)], ct);
    }

    private static async Task PageAsync(SqliteDb db, WidgetRowRepository widgets, Random rng, CancellationToken ct)
    {
        await using var session = await db.OpenAsync(ct);
        var filter = new SqlFilter("Category = $c", ("$c", $"cat-{rng.Next(10)}"));
        var page = await widgets.QueryAsync(session, 100, filter);

        if (page.Count > 0)
        {
            _ = await widgets.QueryAsync(session, 100, filter, page[^1].Id, ct);
        }
    }

    /// <summary>
    /// A deliberately long-lived read. In WAL this holds a read snapshot open, which prevents the
    /// checkpointer from reclaiming the log — the usual cause of a WAL file growing without bound in
    /// production while nothing looks wrong.
    /// </summary>
    private static async Task StreamAsync(SqliteDb db, WidgetRowRepository widgets, CancellationToken ct)
    {
        await using var session = await db.OpenAsync(ct);
        var seen = 0;

        await foreach (var _ in widgets.StreamAsync(session, null, ct))
        {
            if (++seen >= 2_000) break;
        }
    }

    private static async Task UpsertAsync(SqliteDb db, WidgetRowRepository widgets, string[] ids, Random rng, CancellationToken ct)
    {
        await using var session = await db.BeginAsync(ct);
        var id = ids[rng.Next(ids.Length)];

        await widgets.UpsertAsync(session, new WidgetRow
        {
            Id = id,
            Name = $"chaos-{rng.Next(10_000)}",
            Category = $"cat-{rng.Next(10)}",
            Price = rng.Next(100_000),
            Quantity = rng.Next(500),
            Note = rng.Next(3) == 0 ? null : $"touched {rng.Next(1000)}",
            RetiredAt = null,
            UpdatedAt = rng.Next(),
        }, ct);

        await session.CommitAsync(ct);
    }

    /// <summary>
    /// Read-then-write inside one transaction, which is the shape of every interesting write path
    /// in the real application. It is also the shape most likely to lose a contention race, because
    /// the transaction starts deferred and only takes the write lock partway through.
    /// </summary>
    private static async Task MultiStatementAsync(
        SqliteDb db, WidgetRowRepository widgets, PartRowRepository parts, string[] ids, Random rng, CancellationToken ct)
    {
        await using var session = await db.BeginAsync(ct);

        var id = ids[rng.Next(ids.Length)];
        var existing = await widgets.GetAsync(session, id, ct);

        if (existing is not null)
        {
            existing.Quantity += 1;
            existing.UpdatedAt = rng.Next();
            await widgets.UpdateAsync(session, existing, ct);

            await parts.InsertAsync(session, new PartRow
            {
                Id = Guid.CreateVersion7().ToString("D").ToUpperInvariant(),
                WidgetId = id,
                Name = $"part-{rng.Next(1000)}",
                Cost = rng.Next(5000),
                Supplier = rng.Next(4) == 0 ? null : $"supplier-{rng.Next(7)}",
            }, ct);
        }

        await session.CommitAsync(ct);
    }

    /// <summary>Insert then delete, exercising the cascade and keeping the table churning.</summary>
    private static async Task ChurnAsync(SqliteDb db, PartRowRepository parts, string[] ids, Random rng, CancellationToken ct)
    {
        var partId = Guid.CreateVersion7().ToString("D").ToUpperInvariant();

        await using (var session = await db.BeginAsync(ct))
        {
            await parts.InsertAsync(session, new PartRow
            {
                Id = partId,
                WidgetId = ids[rng.Next(ids.Length)],
                Name = "ephemeral",
                Cost = 1,
            }, ct);

            await session.CommitAsync(ct);
        }

        await using (var session = await db.BeginAsync(ct))
        {
            await parts.DeleteAsync(session, partId, ct);
            await session.CommitAsync(ct);
        }
    }

    // --- Seeding and reporting -------------------------------------------------------------------

    private static async Task<string[]> SeedAsync(SqliteDb db, int count)
    {
        var ids = Enumerable.Range(0, count)
            .Select(i => $"0199{i:x8}-0000-7000-8000-{i:x12}")
            .ToArray();

        var widgets = new WidgetRowRepository();
        var tags = new TagRowRepository();

        await using var session = await db.BeginAsync(CancellationToken.None);

        for (var i = 0; i < TagCount; i++)
        {
            await tags.InsertAsync(session, new TagRow { Id = $"tag-{i}", Name = $"tag-{i}" }, CancellationToken.None);
        }

        for (var i = 0; i < count; i++)
        {
            await widgets.InsertAsync(session, new WidgetRow
            {
                Id = ids[i],
                Name = $"Widget {i}",
                Category = $"cat-{i % 10}",
                Price = i * 100L,
                Quantity = i % 50,
                Note = $"seeded {i}",
                RetiredAt = i % 4 == 0 ? null : i,
                UpdatedAt = i,
            }, CancellationToken.None);
        }

        await session.CommitAsync(CancellationToken.None);
        return ids;
    }

    private static async Task<bool> ReportAsync(SqliteDb db, Stats stats, TimeSpan elapsed, int workers)
    {
        Console.WriteLine("Schipper.Io.Sqlite - chaos run");
        Console.WriteLine(new string('=', 78));
        Console.WriteLine($"  workers   {workers}");
        Console.WriteLine($"  duration  {elapsed.TotalSeconds:N1}s");
        Console.WriteLine($"  ops       {stats.Total:N0}  ({stats.Total / elapsed.TotalSeconds:N0}/sec)");
        Console.WriteLine();
        Console.WriteLine($"  {"operation",-16} {"count",10} {"ops/sec",10} {"p50 ms",9} {"p99 ms",9} {"max ms",9}");
        Console.WriteLine($"  {new string('-', 16)} {new string('-', 10)} {new string('-', 10)} {new string('-', 9)} {new string('-', 9)} {new string('-', 9)}");

        foreach (var op in Enum.GetValues<Op>())
        {
            var s = stats.For(op);
            if (s.Count == 0) continue;

            Console.WriteLine($"  {op,-16} {s.Count,10:N0} {s.Count / elapsed.TotalSeconds,10:N0} " +
                              $"{s.Percentile(50),9:N2} {s.Percentile(99),9:N2} {s.Max,9:N2}");
        }

        Console.WriteLine();
        Console.WriteLine("  failures");

        if (stats.Failures.Count == 0)
        {
            Console.WriteLine("    none");
        }
        else
        {
            foreach (var (code, count) in stats.Failures.OrderByDescending(f => f.Value))
            {
                Console.WriteLine($"    {code,-34} {count,8:N0}   {(double)count / stats.Total:P2} of ops");
            }
        }

        Console.WriteLine();
        Console.WriteLine("  invariants");
        var invariantsHold = await CheckInvariantsAsync(db);

        Console.WriteLine();
        Console.WriteLine("  Notes");
        Console.WriteLine("    * SQLite permits many concurrent readers but one writer. WAL lets reads");
        Console.WriteLine("      continue during a write; two writers still serialise, and the loser gets");
        Console.WriteLine("      SQLITE_BUSY once busy_timeout expires.");
        Console.WriteLine("    * busy_timeout is 5000ms and applied per connection, because it resets");
        Console.WriteLine("      under pooling. Nothing retries above that today.");
        Console.WriteLine("    * Microsoft.Data.Sqlite has no true async I/O, so each worker occupies a");
        Console.WriteLine("      thread for the duration of its call. This is thread concurrency, not");
        Console.WriteLine("      overlapped I/O.");

        return invariantsHold;
    }

    private static async Task<bool> CheckInvariantsAsync(SqliteDb db)
    {
        await using var session = await db.OpenAsync(CancellationToken.None);

        var ok = true;

        ok &= await CheckAsync(session, "no orphaned parts (FK holds)",
            "SELECT COUNT(*) FROM Parts p LEFT JOIN Widgets w ON w.Id = p.WidgetId WHERE w.Id IS NULL", 0);

        ok &= await CheckAsync(session, "no duplicate widget ids",
            "SELECT COUNT(*) FROM (SELECT Id FROM Widgets GROUP BY Id HAVING COUNT(*) > 1)", 0);

        ok &= await CheckAsync(session, "widgets still present", "SELECT COUNT(*) FROM Widgets", SeedRows);

        // A foreign_keys PRAGMA that silently reverted would make the first check meaningless.
        await using var pragma = session.Connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys;";
        var on = Convert.ToInt64(await pragma.ExecuteScalarAsync()) == 1;
        Console.WriteLine($"    {(on ? "OK  " : "FAIL")} foreign_keys enforcement is on");

        return ok && on;
    }

    private static async Task<bool> CheckAsync(SqliteSession session, string name, string sql, long expected)
    {
        await using var command = session.Connection.CreateCommand();
        command.CommandText = sql;
        var actual = Convert.ToInt64(await command.ExecuteScalarAsync());

        var passed = actual == expected;
        Console.WriteLine(passed
            ? $"    OK   {name}"
            : $"    FAIL {name}  (expected {expected:N0}, got {actual:N0})");
        return passed;
    }

    // --- Bookkeeping -----------------------------------------------------------------------------

    internal enum Op { Get, Query, Stream, Upsert, Transaction, Churn }

    private sealed class Stats
    {
        private readonly Dictionary<Op, Latencies> _byOp = Enum.GetValues<Op>().ToDictionary(o => o, _ => new Latencies());
        private readonly Lock _gate = new();

        public Dictionary<string, int> Failures { get; } = [];

        public int Total { get; private set; }

        /// <summary>Failures that are not contention. A BUSY is a tuning finding; a constraint violation is a bug.</summary>
        public int HardFailures { get; private set; }

        public Latencies For(Op op) => _byOp[op];

        public void Record(Op op, long startTimestamp)
        {
            var ms = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;

            lock (_gate)
            {
                _byOp[op].Add(ms);
                Total++;
            }
        }

        public void RecordConcurrency()
        {
            lock (_gate)
            {
                const string code = "concurrency stale version";
                Failures[code] = Failures.GetValueOrDefault(code) + 1;
                Total++;
            }
        }

        public void RecordFailure(SqliteException ex)
        {
            var code = $"{ex.SqliteErrorCode}/{ex.SqliteExtendedErrorCode} {Describe(ex)}";

            lock (_gate)
            {
                Failures[code] = Failures.GetValueOrDefault(code) + 1;
                Total++;

                // SQLITE_BUSY (5) and SQLITE_LOCKED (6) are contention, which is expected under
                // concurrency and is a tuning question. Anything else means the data layer is wrong.
                if (ex.SqliteErrorCode is not (5 or 6))
                {
                    HardFailures++;
                }
            }
        }

        private static string Describe(SqliteException ex) => ex.SqliteErrorCode switch
        {
            5 => "SQLITE_BUSY",
            6 => "SQLITE_LOCKED",
            19 => "SQLITE_CONSTRAINT",
            _ => ex.SqliteErrorCode.ToString(),
        };
    }

    private sealed class Latencies
    {
        private readonly List<double> _samples = [];

        public int Count => _samples.Count;

        public double Max => _samples.Count == 0 ? 0 : _samples.Max();

        public void Add(double ms) => _samples.Add(ms);

        public double Percentile(int p)
        {
            if (_samples.Count == 0) return 0;

            var ordered = _samples.Order().ToArray();
            var index = (int)Math.Ceiling(p / 100.0 * ordered.Length) - 1;
            return ordered[Math.Clamp(index, 0, ordered.Length - 1)];
        }
    }
}
