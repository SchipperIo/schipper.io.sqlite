using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Schipper.Io.Sqlite;
using Schipper.Io.Sqlite.Query;

namespace Schipper.Io.Sqlite.Bench;

/// <summary>
/// Single-threaded throughput per operation, at one or more table sizes.
///
/// A benchmark, not a micro-benchmark: no warmup games or statistical machinery. The question is
/// roughly how many rows per second the generated code moves and which call shapes are expensive,
/// which a few thousand real operations against a real file-backed database answers adequately.
/// </summary>
internal static class Benchmark
{
    public static async Task<int> RunAsync(int[] scales)
    {
        foreach (var rows in scales)
        {
            await RunOneAsync(rows);
        }

        return 0;
    }

    private static async Task RunOneAsync(int rows)
    {
        var dbPath = Path.Combine(AppContext.BaseDirectory, $"bench-{rows}.db");

        foreach (var stale in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
        {
            if (File.Exists(stale)) File.Delete(stale);
        }

        var db = new SqliteDb($"Data Source={dbPath}");
        var widgets = new WidgetRowRepository();
        var parts = new PartRowRepository();
        var tags = new TagRowRepository();
        var widgetTags = new WidgetTagRowRepository();
        var ct = CancellationToken.None;

        await Database.ApplyAsync(db, cancellationToken: ct);

        var results = new List<Result>();
        var ids = Enumerable.Range(0, rows).Select(Id).ToArray();
        var tagIds = Enumerable.Range(0, 20).Select(i => Id(1_000_000 + i)).ToArray();

        // The first two measure identical work. Outside an explicit transaction SQLite wraps every
        // statement in its own and commits it, so the per-row figure is the cost of committing N
        // times instead of once. Measured here that is roughly 6x, not the three orders of magnitude
        // the folklore quotes — that assumes synchronous=FULL and a rollback journal, and we run WAL
        // with synchronous=NORMAL. The batched number describes this application, because every
        // write path runs inside a transaction.
        await Measure("Insert - one transaction per row", Math.Min(rows / 20, 500), async count =>
        {
            for (var i = 0; i < count; i++)
            {
                await using var session = await db.BeginAsync(ct);
                await widgets.InsertAsync(session, NewWidget(ids[i], i), ct);
                await session.CommitAsync(ct);
            }

            return count;
        });

        await Measure("Insert - batched in one transaction", rows, async count =>
        {
            await using var session = await db.BeginAsync(ct);

            for (var i = 0; i < count; i++)
            {
                await widgets.UpsertAsync(session, NewWidget(ids[i], i), ct);
            }

            await session.CommitAsync(ct);
            return count;
        });

        await Measure("Insert many-to-one (Parts)", rows, async count =>
        {
            await using var session = await db.BeginAsync(ct);

            for (var i = 0; i < count; i++)
            {
                await parts.InsertAsync(session, new PartRow
                {
                    Id = Id(2_000_000 + i),
                    WidgetId = ids[i],
                    Name = $"Part {i}",
                    Cost = i * 13L,
                    Supplier = i % 3 == 0 ? null : $"Supplier {i % 7}",
                }, ct);
            }

            await session.CommitAsync(ct);
            return count;
        });

        await using (var seeding = await db.BeginAsync(ct))
        {
            foreach (var tagId in tagIds)
            {
                await tags.InsertAsync(seeding, new TagRow { Id = tagId, Name = $"tag-{tagId[^4..]}" }, ct);
            }

            await seeding.CommitAsync(ct);
        }

        await Measure("Insert many-to-many (composite key)", rows, async count =>
        {
            await using var session = await db.BeginAsync(ct);

            for (var i = 0; i < count; i++)
            {
                await widgetTags.InsertAsync(session, new WidgetTagRow
                {
                    WidgetId = ids[i],
                    TagId = tagIds[i % tagIds.Length],
                    AssignedAt = i,
                }, ct);
            }

            await session.CommitAsync(ct);
            return count;
        });

        // UpdateAsync is version-checked, so the row has to carry the version it expects. The two
        // insert passes above leave the table in a known state: the first N rows were inserted
        // (Version 1) and then upserted (Version 2); the rest were only upserted, so they are still
        // at Version 1. Constructing a fresh row here without that would fail every update — which
        // is exactly what the concurrency check is for, and is how this benchmark caught itself.
        var perRowInserted = Math.Min(rows / 20, 500);

        await Measure("Update - full row replace", rows, async count =>
        {
            await using var session = await db.BeginAsync(ct);

            for (var i = 0; i < count; i++)
            {
                var row = NewWidget(ids[i], i);
                row.Note = null;                       // full replace, so this clears the column
                row.Version = i < perRowInserted ? 2 : 1;
                await widgets.UpdateAsync(session, row, ct);
            }

            await session.CommitAsync(ct);
            return count;
        });

        await Measure("Upsert - update path, nulls preserved", rows, async count =>
        {
            await using var session = await db.BeginAsync(ct);

            for (var i = 0; i < count; i++)
            {
                var row = NewWidget(ids[i], i);
                row.Note = null;                       // COALESCE keeps whatever is stored
                await widgets.UpsertAsync(session, row, ct);
            }

            await session.CommitAsync(ct);
            return count;
        });

        await Measure("Get by key", rows, async count =>
        {
            await using var session = await db.OpenAsync(ct);

            for (var i = 0; i < count; i++)
            {
                _ = await widgets.GetAsync(session, ids[i], ct);
            }

            return count;
        });

        await Measure("Get by composite key", rows, async count =>
        {
            await using var session = await db.OpenAsync(ct);

            for (var i = 0; i < count; i++)
            {
                _ = await widgetTags.GetAsync(session, ids[i], tagIds[i % tagIds.Length], ct);
            }

            return count;
        });

        await Measure("Query - filtered, keyset paged (100/page)", rows, async _ =>
        {
            await using var session = await db.OpenAsync(ct);
            var filter = new SqlFilter("Category = $category", ("$category", "cat-3"));
            string? after = null;
            var seen = 0;

            while (true)
            {
                var page = await widgets.QueryAsync(session, 100, filter, after, ct);
                if (page.Count == 0) break;

                seen += page.Count;
                after = page[^1].Id;
            }

            return seen;
        });

        await Measure("Stream - IAsyncEnumerable, unbuffered", rows, async count =>
        {
            await using var session = await db.OpenAsync(ct);
            var seen = 0;

            await foreach (var _ in widgets.StreamAsync(session, null, ct))
            {
                if (++seen >= count) break;
            }

            return seen;
        });

        await Measure("Delete by key", rows, async count =>
        {
            await using var session = await db.BeginAsync(ct);

            for (var i = 0; i < count; i++)
            {
                await widgets.DeleteAsync(session, ids[i], ct);
            }

            await session.CommitAsync(ct);
            return count;
        });

        Report();
        return;

        // The action reports how many rows it ACTUALLY touched. Passing the requested count through
        // blindly is how an earlier version claimed 1.5M rows/sec for a filtered query that matches
        // only a tenth of the table.
        async Task Measure(string name, int count, Func<int, Task<int>> action)
        {
            var before = GC.GetTotalAllocatedBytes(precise: true);
            var start = Stopwatch.GetTimestamp();

            var actual = await action(count);

            var elapsed = Stopwatch.GetElapsedTime(start);
            var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

            results.Add(new Result(name, actual, elapsed, allocated));
        }

        void Report()
        {
            var native = IsNativeAot();

            Console.WriteLine();
            Console.WriteLine("Schipper.Io.Sqlite - generated data layer throughput");
            Console.WriteLine(new string('=', 92));
            Console.WriteLine($"  runtime   {(native ? "Native AOT" : "JIT")}");
            Console.WriteLine($"  sqlite    {SqliteVersion()}");
            Console.WriteLine($"  rows      {rows:N0}");
            Console.WriteLine();
            Console.WriteLine($"  {"operation",-42} {"rows",9} {"ms",10} {"rows/sec",12} {"alloc/row",11}");
            Console.WriteLine($"  {new string('-', 42)} {new string('-', 9)} {new string('-', 10)} {new string('-', 12)} {new string('-', 11)}");

            foreach (var r in results)
            {
                Console.WriteLine(
                    $"  {r.Name,-42} {r.Count,9:N0} {r.Elapsed.TotalMilliseconds,10:N1} " +
                    $"{r.PerSecond,12:N0} {(double)r.Allocated / r.Count,9:N0} B");
            }

            Console.WriteLine();
            Console.WriteLine("  Caveats");
            Console.WriteLine("    * The two Insert rows measure identical work; the difference is committing");
            Console.WriteLine("      once per row versus once per batch. Under WAL + synchronous=NORMAL that is");
            Console.WriteLine("      a few times slower, not the 1000x quoted for synchronous=FULL.");
            Console.WriteLine("    * Row counts are what each operation actually touched. The filtered query");
            Console.WriteLine("      matches a tenth of the table, so it reports a tenth of the rows.");
            Console.WriteLine("    * 'Insert - one transaction per row' is capped at 500 iterations, so it is a");
            Console.WriteLine("      thinner sample than the rest. It is there for the contrast, not precision.");
            Console.WriteLine("    * Microsoft.Data.Sqlite has no true async I/O; these async methods complete");
            Console.WriteLine("      synchronously, so nothing here overlaps. See chaos mode for concurrency.");
            Console.WriteLine("    * A local file-backed database. Network storage would change everything.");
            Console.WriteLine();
        }
    }

    private static WidgetRow NewWidget(string id, int i) => new()
    {
        Id = id,
        Name = $"Widget {i}",
        Category = $"cat-{i % 10}",
        Price = i * 100L,
        Quantity = i % 50,
        Note = $"note {i}",
        RetiredAt = i % 4 == 0 ? null : i,
        UpdatedAt = i,
    };

    private static string Id(int i) => $"0199{i:x8}-0000-7000-8000-{i:x12}";

    /// <summary>
    /// Whether this is a native binary rather than a JIT run, which the header reports.
    ///
    /// <c>Assembly.Location</c> is empty under Native AOT — and IL3000 exists to say so, which is
    /// why the suppression is on the method rather than a pragma: the emptiness IS the signal being
    /// read, so the warning is the mechanism and not a defect.
    ///
    /// <c>RuntimeFeature.IsDynamicCodeSupported</c> is NOT a substitute. <c>PublishAot</c> writes it
    /// false into runtimeconfig.json for the JIT build of the same project, so it reports "native"
    /// for a binary that is nothing of the sort — which is exactly how this label was wrong once.
    /// </summary>
    [UnconditionalSuppressMessage("SingleFile", "IL3000",
        Justification = "An empty Location is the intended signal: it is how a native binary is detected.")]
    private static bool IsNativeAot() =>
        string.IsNullOrEmpty(Assembly.GetEntryAssembly()?.Location);

    private static string SqliteVersion()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sqlite_version();";
        return (string)(command.ExecuteScalar() ?? "unknown");
    }

    private sealed record Result(string Name, int Count, TimeSpan Elapsed, long Allocated)
    {
        public double PerSecond => Elapsed.TotalSeconds > 0 ? Count / Elapsed.TotalSeconds : 0;
    }
}
