# Schipper.Io.Sqlite

An opinionated bridge from .NET to SQLite. You declare row types; it generates the repositories.

Native AOT clean, no reflection, no runtime SQL building, no `IQueryable`.

The usage guide is in [docs/](docs/README.md).

```csharp
[Table("Widgets")]
[TableIndex("Category", "Id")]
public sealed class WidgetRow : AuditedRow
{
    [Key] public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public long Price { get; set; }
    public string? Note { get; set; }      // nullable -> upsert preserves it
}
```

That generates `WidgetRowRepository` with `GetAsync`, `QueryAsync` (a `QueryPage<T>` with `HasMore`),
`StreamAsync`, `InsertAsync`, `UpdateAsync`/`TryUpdateAsync`, `UpsertAsync`/`TryUpsertAsync` and
`DeleteAsync`.

```csharp
var db = new SqliteDb("Data Source=app.db");

await using var session = await db.BeginAsync(user: "ada");
await widgets.InsertAsync(session, new WidgetRow { Id = id, Name = "Nebula", Price = 3_4567 });
await session.CommitAsync();
```

## What it decides for you

**The row type is the only source of truth.** Column type, nullability and upsert semantics are read
off the C# declaration, so they cannot contradict it. There is no mapping file to keep in step.

**Nullability drives upsert.** A nullable property becomes `COALESCE(excluded.X, T.X)`, so a null
leaves the stored value alone. The consequence is deliberate: upsert can never clear a column to
`NULL` — `UpdateAsync` is what does that. Which columns behave this way is already written down in
the type; restating it in SQL by hand is a chance to state it wrong.

**Every table carries `Created`, `Modified`, `User` and `Version`** — that is what `AuditedRow` is.
`Created` is set by insert and by the insert half of an upsert, and never changes again. `Version` is
incremented *by the database*, so it is monotonic without trusting a clock.

**Writes are version-checked.** `UpdateAsync` carries the version that was read, so a row somebody
else has written no longer matches and nothing is silently overwritten. `UpsertAsync` checks too, but
only when you supply a non-zero version — stored versions start at 1, so `Version == 0` means "I
never read this row" and gets make-it-so semantics, which is what keeps upsert usable for seeding,
webhook replay and reconciliation.

**Reads are keyset-paged, not `OFFSET`.** `QueryAsync` takes a required `limit` and returns a
`QueryPage<T>` whose `HasMore` says whether another page exists. A default page size is a silent
truncation: callers that treat the result as "all matches" would drop every row past it.
`StreamAsync` is the unpaged path. `OFFSET` rescans every skipped row, and can skip or repeat rows
when the table changes under a paging client.

Composite-key tables have no single-value cursor — paging on one key column cannot advance when a
filter holds that column constant — so those `QueryAsync` overloads take a filter and a limit only.

**`SqlFilter` clauses are yours to author; values are always bound.** That division is the safety
property — a clause assembled from request data would be an injection hole no matter how carefully
the values travelled. Binding a `decimal`, `DateTimeOffset` or `Guid` without going through
`SqliteValue` throws rather than storing TEXT, which is how `"9" > "10"` starts answering
plausibly and wrongly.

## Indexes

Generated reads always order by the key, so **an index meant to serve a paged read must end with the
key column**:

```csharp
[TableIndex("Category", "Id")]   // not [TableIndex("Category")]
```

An index that stops at the filter column leaves SQLite sorting every matching row on every page. This
is not a micro-optimisation: measured against 1.5M rows, paged reads degrade *quadratically* as the
table grows without it.

## Types

SQLite has five storage classes — NULL, INTEGER, REAL, TEXT, BLOB. Everything else is a mapping
decision, and each of these is a decision rather than a driver default:

| C# | Stored as | Encoding |
|----|-----------|----------|
| `string` | TEXT | verbatim |
| `char` | TEXT | one character |
| `bool` | INTEGER | 0 / 1 |
| `byte` `sbyte` `short` `ushort` `int` `uint` `long` | INTEGER | SQLite stores all integers 64-bit anyway |
| `float` `double` | REAL | |
| `decimal` | INTEGER | **scaled ×10ⁿ**, default n=4, `[Scale(n)]` to change |
| `Guid` | TEXT | canonical lowercase `8-4-4-4-12` |
| `DateTimeOffset` | INTEGER | UTC ticks — **the offset is not preserved** |
| `DateTime` | INTEGER | UTC ticks; `Unspecified` is treated as UTC |
| `DateOnly` | INTEGER | day number |
| `TimeOnly` `TimeSpan` | INTEGER | ticks |
| `enum` | INTEGER | underlying value |
| `byte[]` | BLOB | |

Plus every nullable form. The conversion helpers are public on `SqliteValue`, because a hand-written
query has to agree with generated code about what a number means.

**Three of those are worth arguing about, so here is the argument.**

*`decimal` is never REAL and never TEXT.* TEXT is the driver's own default — not something EF
invented — and it compares lexicographically, so `"9" > "10"` and a reorder-point check starts
answering plausibly and wrongly. REAL cannot represent `0.1` exactly. Scaled integers compare
numerically and are exact. `FromDecimal` throws `OverflowException` rather than truncating.

*`DateTimeOffset` loses its offset.* A value carrying both a moment and a viewpoint invites code that
compares two of them and is quietly wrong. If the original offset matters, give it its own column
where it is visible.

*`Guid` is TEXT, not BLOB.* BLOB would be 16 bytes rather than 36 and give smaller indexes. A
database you can read at a `sqlite3` prompt is worth more here — and UUIDv7 keys sort chronologically
as text, which is what makes keyset pagination work.

Two refusals:

- **`ulong` is rejected** (`SQLG007`). SQLite's INTEGER is 64-bit *signed*, so anything above
  `long.MaxValue` round-trips negative and compares wrongly — silently, and only for large values.
- **Floating-point and `byte[]` keys are rejected** (`SQLG008`): one can't be compared exactly, the
  other can't serve the cursor.

Anything genuinely unmapped is a **build error** (`SQLG004`), never a silent fallback to TEXT.

## Schema

The generator emits the DDL too, so the row types are the only schema there is.
`Database` appears in your root namespace — a project whose `RootNamespace` is
`MyApp` gets `MyApp.Database`:

```csharp
var changes = await Database.ApplyAsync(db);

foreach (var change in changes)
{
    logger.LogInformation("schema: {Change}", change);
}
```

If that name collides — a `Database` folder under the app root is the usual case — put
`[DatabaseNamespace("MyApp.Persistence")]` on a row type. There is one `Database` type for the
whole compilation, so every `[DatabaseNamespace]` in the project must name the same namespace.

`CREATE TABLE IF NOT EXISTS` alone would handle the first run and silently do nothing forever after,
so instead the database is **read** (`PRAGMA table_info`, `sqlite_master`) and compared against the
model. What happens to each difference:

| Difference | Result |
|---|---|
| New table | created |
| New index | created |
| New column, nullable or with a default | `ALTER TABLE ADD COLUMN` |
| New column, `NOT NULL` with no default | **fails** — SQLite cannot add one |
| Column dropped from the row type | reported; harmless, since every statement names its columns |
| Column added *and* removed on one table | **fails** — see `[RenameFrom]` below |
| Column retyped, or nullability changed | **fails** — SQLite cannot alter either in place |

`ApplyAsync` returns what it did rather than logging it, because this library has no opinion about
your logger — and a caller that discards the fact it just altered a table should have to do so
explicitly. `Database.DiffAsync` reports without changing anything, and `SchemaUpdate.CreateOnly`
or `SchemaUpdate.VerifyOnly` narrow what `ApplyAsync` is allowed to touch.

### Rebuilds

SQLite cannot retype a column, change its nullability, or alter a key. The only way is the twelve-step
rebuild from the SQLite docs — new table, copy the rows, drop, rename — and it is available, behind
**two** keys:

```csharp
[Table("Orders")]
[Rebuild]                                   // this table may be rebuilt
public sealed class OrderRow : AuditedRow { ... }

await Database.ApplyAsync(db, SchemaUpdate.Rebuild);   // ...and you are asking for it now
```

Two rather than one because a rebuild can lose data: the copy is an `INSERT … SELECT`, so SQLite
coerces per column affinity and a narrowing change truncates, and any column no longer on the row
type is simply not carried across. `[RenameFrom]` is honoured during a rebuild, so a renamed column
keeps its values; without it they go. A destructive operation should not be reachable by editing one
file.

Foreign keys are disabled around the rebuild and `PRAGMA foreign_key_check` runs before the commit,
so a violation rolls the whole thing back and leaves the table untouched.

### Renames

A rename is indistinguishable from a drop plus an add. Guessing would add the new column and leave
the old one orphaned, so every existing row would hold the default while the real data sat beside
it — a wrong answer rather than an error. So it is refused, and `[RenameFrom]` is how you say what
actually happened:

```csharp
[RenameFrom("Amount")] public long Total { get; set; }
```

That renames in place and keeps the data. It is safe to leave on the property forever: on an
already-renamed database nothing matches, and on a fresh one the table is simply created. It states
history rather than issuing an instruction.

## Diagnostics

| ID | Meaning |
|----|---------|
| SQLG001 | Row type must derive from `AuditedRow` |
| SQLG002 | Row type has no `[Key]` property |
| SQLG003 | Row type has no mappable properties |
| SQLG004 | Property type has no SQLite mapping |
| SQLG005 | Key property is nullable |
| SQLG006 | Row type is abstract |
| SQLG007 | `ulong` has no lossless SQLite mapping |
| SQLG008 | Key property has an unsuitable type |
| SQLG009 | Conflicting `[DatabaseNamespace]` values |
| SQLG010 | `[DatabaseNamespace]` is not a valid namespace |
| SQLG011 | Row type has no public parameterless constructor |
| SQLG012 | Mapped property setter is not public or internal |
| SQLG013 | `[Scale]` is not an integer in 0..10 |

A generator that silently emits nothing is worse than one that fails: you get a missing type and a
compile error hundreds of lines from the cause.

## Interfaces

Single-key tables implement `IKeyedRepository<TRow, TKey>`; composite-key tables implement
`IRepository<TRow>`, which omits the key-shaped operations. Both constrain on `IAuditedRow` —
knowing every row has a version and an author is what makes generic code over rows possible
without reflection. The generator still requires the `AuditedRow` base class, so it can find the
four columns without scanning further.

## Building

Builds run in the latest .NET 10 SDK container, `mcr.microsoft.com/dotnet/sdk:10.0`. Docker is required. `build.ps1` and `build.sh` both run `container.sh` inside that image. The source is copied into `/tmp` inside the container, so `bin/` and `obj/` stay off the host. `-p` writes the one consumer package — runtime assembly plus generator — to `./dist` and copies it to the shared local feed at `../nuget.cache`. A local `dotnet pack` of `Schipper.Io.Sqlite/src/Schipper.Io.Sqlite.csproj` still writes `artifacts/dist`. Tests and the bench are not packable and are not referenced by the library, so they cannot enter the nupkg.

`nuget.config` restores `Schipper.*` from that feed and every other package from nuget.org. This library has no `Schipper.*` dependency. `global.json` requests SDK 10.0.302 and rolls forward to the latest .NET 10 minor in the image.

`container.sh` builds `Schipper.Io.Sqlite.slnx` and packs `Schipper.Io.Sqlite/src/Schipper.Io.Sqlite.csproj`. The layout stays as it is: the library, its tests, and the bench under `Schipper.Io.Sqlite/`, and the generator with its tests under `Schipper.Io.Sqlite.Generator/`.

No flags builds Release. Flags combine. The runtime identifier defaults to `linux-x64` (`RID=win-x64 ./build.sh` or `./build.ps1 -Rid win-x64`).

```bash
./build.sh           # restore + build
./build.sh -t        # unit tests
./build.sh -i        # integration tests, if any
./build.sh -p        # pack into ./dist and ../nuget.cache
./build.sh -r        # run, when the project is an executable
./build.sh -q        # unit tests under dotnet-trace -> ./dist/trace
./build.sh -o        # also write build/test logs to ./dist/raw
./build.sh -t -p     # flags combine
```

```powershell
./build.ps1
./build.ps1 -t -p
```

`-r` skips this library. Native AOT throughput, chaos, and audit checks live in `Schipper.Io.Sqlite/tests/Schipper.Io.Sqlite.Bench` and are published locally:

```bash
dotnet run --project Schipper.Io.Sqlite/tests/Schipper.Io.Sqlite.Bench
dotnet run --project Schipper.Io.Sqlite/tests/Schipper.Io.Sqlite.Bench -- chaos
dotnet run --project Schipper.Io.Sqlite/tests/Schipper.Io.Sqlite.Bench -- audit
```

## Continuous integration

`.github/workflows/build.yml` runs `./build.sh -t -p` on Ubuntu for every push and pull request, using the same SDK container. The packed package is uploaded as the `nuget` workflow artifact.

## Status

Early. The API will move. `dotnet pack` produces one package containing both the runtime assembly and
the generator, so consumers need a single `PackageReference`.

## License

Licensed under the MIT License. See [LICENSE](LICENSE) for details.
