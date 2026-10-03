using Schipper.Io.Sqlite;
using Schipper.Io.Sqlite.Model;
using Schipper.Io.Sqlite.Query;

namespace Schipper.Io.Sqlite.Bench;

/// <summary>
/// Verifies the audit and concurrency rules the generator emits, because "the SQL looks right" is
/// not evidence. Run with <c>dotnet run --project Schipper.Io.Sqlite/tests/Schipper.Io.Sqlite.Bench -- audit</c>.
///
/// The rules under test:
///   Created   set by Insert and by the insert half of Upsert; never changed by an update
///   Modified  set by every write
///   User      taken from SqliteSession.User; "System" when the session was opened without one
///   Version   incremented by the database on every write; UpdateAsync refuses a stale one
/// </summary>
internal static class Audit
{
    public static async Task<int> RunAsync()
    {
        var dbPath = Path.Combine(AppContext.BaseDirectory, "audit.db");

        foreach (var stale in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
        {
            if (File.Exists(stale)) File.Delete(stale);
        }

        var db = new SqliteDb($"Data Source={dbPath}");
        await Database.ApplyAsync(db, cancellationToken: CancellationToken.None);

        var widgets = new WidgetRowRepository();
        var ct = CancellationToken.None;
        var failures = 0;

        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "  -> " + detail : "")}");
            if (!ok) failures++;
        }

        const string id = "01990000-0000-7000-8000-000000000001";

        Console.WriteLine("Schipper.Io.Sqlite - audit and concurrency rules");
        Console.WriteLine(new string('=', 78));
        Console.WriteLine();

        // --- Insert stamps everything ------------------------------------------------------------
        Console.WriteLine("insert");
        long createdAt;

        await using (var session = await db.BeginAsync("ada", ct))
        {
            await widgets.InsertAsync(session, New(id), ct);
            await session.CommitAsync(ct);
        }

        var row = await ReadAsync(db, widgets, id, ct);
        createdAt = row!.Created;

        Check("Created set", row.Created > 0, row.Created.ToString());
        Check("Modified set", row.Modified > 0);
        Check("Created == Modified on insert", row.Created == row.Modified);
        Check("User taken from session", row.User == "ada", row.User);
        Check("Version starts at 1", row.Version == 1, row.Version.ToString());

        // --- Unattributed session falls back to System -------------------------------------------
        Console.WriteLine();
        Console.WriteLine("attribution");

        await using (var session = await db.BeginAsync(ct))
        {
            var other = New("01990000-0000-7000-8000-000000000002");
            await widgets.InsertAsync(session, other, ct);
            await session.CommitAsync(ct);
        }

        var unattributed = await ReadAsync(db, widgets, "01990000-0000-7000-8000-000000000002", ct);
        Check("no user supplied -> System", unattributed!.User == "System", unattributed.User);

        // --- Update: Created frozen, everything else moves ----------------------------------------
        Console.WriteLine();
        Console.WriteLine("update");
        await Task.Delay(5, ct);                       // so a changed Modified is visibly different

        row.Name = "renamed";

        await using (var session = await db.BeginAsync("grace", ct))
        {
            await widgets.UpdateAsync(session, row, ct);
            await session.CommitAsync(ct);
        }

        var updated = await ReadAsync(db, widgets, id, ct);

        Check("Created UNCHANGED by update", updated!.Created == createdAt, $"{updated.Created} vs {createdAt}");
        Check("Modified advanced", updated.Modified > createdAt);
        Check("User reattributed", updated.User == "grace", updated.User);
        Check("Version incremented", updated.Version == 2, updated.Version.ToString());
        Check("data column written", updated.Name == "renamed", updated.Name);
        Check("in-memory row tracks the new version", row.Version == 2, row.Version.ToString());

        // --- Optimistic concurrency ---------------------------------------------------------------
        Console.WriteLine();
        Console.WriteLine("optimistic concurrency");

        // Two callers read the same row, then both write. This is the cross-transaction lost update
        // that a plain "WHERE Id = ?" cannot see.
        var first = await ReadAsync(db, widgets, id, ct);
        var second = await ReadAsync(db, widgets, id, ct);

        first!.Name = "first writer";

        await using (var session = await db.BeginAsync("first", ct))
        {
            await widgets.UpdateAsync(session, first, ct);
            await session.CommitAsync(ct);
        }

        second!.Name = "second writer";
        var secondWon = true;

        await using (var session = await db.BeginAsync("second", ct))
        {
            secondWon = await widgets.TryUpdateAsync(session, second, ct);
            await session.CommitAsync(ct);
        }

        Check("stale writer is refused", !secondWon);

        var afterRace = await ReadAsync(db, widgets, id, ct);
        Check("first writer's value survived", afterRace!.Name == "first writer", afterRace.Name);
        Check("version advanced once, not twice", afterRace.Version == 3, afterRace.Version.ToString());

        var threw = false;

        try
        {
            await using var session = await db.BeginAsync("second", ct);
            await widgets.UpdateAsync(session, second, ct);
        }
        catch (SqliteConcurrencyException)
        {
            threw = true;
        }

        Check("UpdateAsync throws SqliteConcurrencyException on a stale row", threw);

        // --- Upsert -------------------------------------------------------------------------------
        Console.WriteLine();
        Console.WriteLine("upsert");
        await Task.Delay(5, ct);

        var reupsert = New(id);
        reupsert.Name = "upserted";
        reupsert.Note = null;                          // COALESCE should keep the stored note

        await using (var session = await db.BeginAsync("kiosk", ct))
        {
            await widgets.UpsertAsync(session, reupsert, ct);
            await session.CommitAsync(ct);
        }

        var upserted = await ReadAsync(db, widgets, id, ct);

        Check("Created STILL unchanged after upsert", upserted!.Created == createdAt, $"{upserted.Created} vs {createdAt}");
        Check("Modified advanced", upserted.Modified > updated.Modified);
        Check("User reattributed", upserted.User == "kiosk", upserted.User);
        Check("Version incremented", upserted.Version == 4, upserted.Version.ToString());
        Check("null field preserved by COALESCE", upserted.Note is not null, upserted.Note ?? "null");

        var fresh = New("01990000-0000-7000-8000-000000000003");

        await using (var session = await db.BeginAsync("kiosk", ct))
        {
            await widgets.UpsertAsync(session, fresh, ct);
            await session.CommitAsync(ct);
        }

        var inserted = await ReadAsync(db, widgets, "01990000-0000-7000-8000-000000000003", ct);
        Check("upsert insert half stamps Created", inserted!.Created > 0);
        Check("upsert insert half starts at Version 1", inserted.Version == 1, inserted.Version.ToString());
        Check("upsert reports the resulting version on the row", fresh.Version == 1, fresh.Version.ToString());

        // --- Upsert concurrency -------------------------------------------------------------------
        Console.WriteLine();
        Console.WriteLine("upsert concurrency");

        // Version 0 means "I never read this row": the check is skipped, which is what keeps upsert
        // usable for seeding, webhook replay and reconciliation.
        var blind = New(id);
        blind.Version = 0;
        var blindWon = false;

        await using (var session = await db.BeginAsync("replay", ct))
        {
            blindWon = await widgets.TryUpsertAsync(session, blind, ct);
            await session.CommitAsync(ct);
        }

        Check("Version 0 skips the check (make-it-so)", blindWon);
        Check("blind upsert still reports the new version", blind.Version == 5, blind.Version.ToString());

        // A supplied version is enforced exactly as UpdateAsync enforces it.
        var current = await ReadAsync(db, widgets, id, ct);
        var stalecopy = await ReadAsync(db, widgets, id, ct);

        current!.Name = "upsert winner";

        await using (var session = await db.BeginAsync("winner", ct))
        {
            await widgets.UpsertAsync(session, current, ct);
            await session.CommitAsync(ct);
        }

        stalecopy!.Name = "upsert loser";
        var staleWon = true;

        await using (var session = await db.BeginAsync("loser", ct))
        {
            staleWon = await widgets.TryUpsertAsync(session, stalecopy, ct);
            await session.CommitAsync(ct);
        }

        Check("stale upsert is refused", !staleWon);

        var afterUpsertRace = await ReadAsync(db, widgets, id, ct);
        Check("upsert winner's value survived", afterUpsertRace!.Name == "upsert winner", afterUpsertRace.Name);
        Check("version advanced once, not twice", afterUpsertRace.Version == 6, afterUpsertRace.Version.ToString());

        var upsertThrew = false;

        try
        {
            await using var session = await db.BeginAsync("loser", ct);
            await widgets.UpsertAsync(session, stalecopy, ct);
        }
        catch (SqliteConcurrencyException)
        {
            upsertThrew = true;
        }

        Check("UpsertAsync throws on a stale supplied version", upsertThrew);

        // --- Type round-trip ----------------------------------------------------------------------
        Console.WriteLine();
        Console.WriteLine("type round-trip");
        await TypesAsync(db, Check, ct);

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILURE(S)");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Writes one row of every mapped type and reads it back through a real SQLite file.
    ///
    /// Asserting on generated source proves the generator emitted what was intended; only this
    /// proves the encoding survives the driver. Values are chosen to be wrong-looking if an encoding
    /// slips: a decimal that TEXT would order incorrectly, a Guid whose text form sorts differently
    /// from its bytes, an instant with a non-zero offset.
    /// </summary>
    private static async Task TypesAsync(SqliteDb db, Action<string, bool, string> report, CancellationToken ct)
    {
        // Wrapper so the detail argument stays optional here; `report` already counts failures.
        void check(string name, bool ok, string detail = "") => report(name, ok, detail);

        var specimens = new SpecimenRowRepository();
        var id = Guid.Parse("01990000-0000-7000-8000-0000000000aa");

        var written = new SpecimenRow
        {
            Id = id,
            Grade = 'A',
            Active = true,
            Tiny = 200,                                   // above sbyte range
            Small = -3_000,
            Medium = 1_234_567,
            Large = 9_000_000_000_000L,                   // beyond int
            Approximate = 1.5f,
            Precise = 3.141592653589793,
            Price = 1234.5678m,
            Weight = 12.34m,                              // [Scale(2)]
            ObservedAt = new DateTimeOffset(2026, 8, 1, 9, 30, 0, TimeSpan.FromHours(-5)),
            RecordedAt = new DateTime(2026, 8, 1, 14, 30, 0, DateTimeKind.Utc),
            Day = new DateOnly(2026, 8, 1),
            Hour = new TimeOnly(14, 30, 15),
            Duration = TimeSpan.FromMinutes(90),
            State = SpecimenState.Observed,
            Payload = [0x00, 0xFF, 0x10, 0x00],           // embedded nulls: BLOB, not TEXT
            Parent = Guid.Parse("01990000-0000-7000-8000-0000000000bb"),
            Discount = 0.05m,
            RetiredAt = null,
            Previous = null,
        };

        await using (var session = await db.BeginAsync("curator", ct))
        {
            await specimens.InsertAsync(session, written, ct);
            await session.CommitAsync(ct);
        }

        SpecimenRow? read;

        await using (var session = await db.OpenAsync(ct))
        {
            read = await specimens.GetAsync(session, id, ct);
        }

        if (read is null)
        {
            check("row round-trips", false, "not found");
            return;
        }

        check("Guid key", read.Id == id, read.Id.ToString());
        check("char", read.Grade == 'A', read.Grade.ToString());
        check("bool", read.Active);
        check("byte (above sbyte range)", read.Tiny == 200, read.Tiny.ToString());
        check("short (negative)", read.Small == -3_000, read.Small.ToString());
        check("int", read.Medium == 1_234_567, read.Medium.ToString());
        check("long (beyond int)", read.Large == 9_000_000_000_000L, read.Large.ToString());
        check("float", Math.Abs(read.Approximate - 1.5f) < 0.0001f, read.Approximate.ToString());
        check("double keeps full precision", read.Precise == 3.141592653589793, read.Precise.ToString("R"));
        check("decimal, default scale 4", read.Price == 1234.5678m, read.Price.ToString());
        check("decimal, [Scale(2)]", read.Weight == 12.34m, read.Weight.ToString());
        check("DateTimeOffset is the same instant", read.ObservedAt == written.ObservedAt, read.ObservedAt.ToString("O"));
        check("DateTimeOffset comes back as UTC", read.ObservedAt.Offset == TimeSpan.Zero, read.ObservedAt.Offset.ToString());
        check("DateTime", read.RecordedAt == written.RecordedAt, read.RecordedAt.ToString("O"));
        check("DateTime Kind is Utc", read.RecordedAt.Kind == DateTimeKind.Utc, read.RecordedAt.Kind.ToString());
        check("DateOnly", read.Day == written.Day, read.Day.ToString());
        check("TimeOnly", read.Hour == written.Hour, read.Hour.ToString());
        check("TimeSpan", read.Duration == written.Duration, read.Duration.ToString());
        check("enum", read.State == SpecimenState.Observed, read.State.ToString());
        check("byte[] with embedded nulls", read.Payload.SequenceEqual(written.Payload), read.Payload.Length + " bytes");
        check("nullable Guid, present", read.Parent == written.Parent, read.Parent?.ToString() ?? "null");
        check("nullable decimal, present", read.Discount == 0.05m, read.Discount?.ToString() ?? "null");
        check("nullable DateTimeOffset, absent", read.RetiredAt is null);
        check("nullable enum, absent", read.Previous is null);

        // decimal stored as TEXT is the '9' > '10' defect. Scaled integers compare numerically, so
        // this ordering is the actual regression guard rather than a restatement of the encoding.
        await using (var session = await db.BeginAsync(ct))
        {
            foreach (var (suffix, price) in new[] { ("b1", 9m), ("b2", 10m) })
            {
                await specimens.InsertAsync(session, new SpecimenRow
                {
                    Id = Guid.Parse($"01990000-0000-7000-8000-0000000000{suffix}"),
                    Grade = 'B',
                    Price = price,
                    Payload = [],
                }, ct);
            }

            await session.CommitAsync(ct);
        }

        await using (var session = await db.OpenAsync(ct))
        {
            var cheap = await specimens.QueryAsync(
                session, 10, new SqlFilter("Price < $p", ("$p", SqliteValue.FromDecimal(10m))), cancellationToken: ct);

            check("decimal compares numerically, not as text",
                cheap.Count == 1 && cheap[0].Price == 9m,
                $"{cheap.Count} row(s) below 10");
        }

        // The cursor is a Guid while the column is TEXT, so this only works if it converts.
        await using (var readSession = await db.OpenAsync(ct))
        {
            var page = await specimens.QueryAsync(readSession, 2, cancellationToken: ct);
            var next = page.Count > 0
                ? await specimens.QueryAsync(readSession, 2, after: page[^1].Id, cancellationToken: ct)
                : new QueryPage<SpecimenRow>([], hasMore: false);

            check("keyset cursor converts a Guid key", page.Count == 2 && next.Count == 1,
                $"page {page.Count}, next {next.Count}");
        }
    }

    private static async Task<WidgetRow?> ReadAsync(SqliteDb db, WidgetRowRepository widgets, string id, CancellationToken ct)
    {
        await using var session = await db.OpenAsync(ct);
        return await widgets.GetAsync(session, id, ct);
    }

    private static WidgetRow New(string id) => new()
    {
        Id = id,
        Name = "original",
        Category = "cat-1",
        Price = 1_000,
        Quantity = 5,
        Note = "note",
        RetiredAt = null,
        UpdatedAt = 0,
    };
}
