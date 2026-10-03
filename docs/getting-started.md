# Getting started

[Index](README.md)

A row type is the schema. It derives from `AuditedRow`, carries `[Table]`, and has at least one `[Key]`. The generator accepts `[Table]` on a class or a record class. `AuditedRow` is a class, so the usual declaration is `public sealed class WidgetRow : AuditedRow`. A positional record without a public parameterless constructor is a build error (`SQLG011`).

```csharp
using Schipper.Io.Sqlite.Model;

[Table("Widgets")]
[TableIndex("Category", "Id")]
public sealed class WidgetRow : AuditedRow
{
    [Key] public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public decimal Price { get; set; }
    public string? Note { get; set; }
}
```

The generator emits `WidgetRowRepository` in the same namespace, and a `Database` type in the project's root namespace. Build the project once. The repository and `Database` then exist as ordinary C# types.

```csharp
using Schipper.Io.Sqlite;

var db = new SqliteDb("Data Source=app.db");
await Database.ApplyAsync(db);

var widgets = new WidgetRowRepository();

await using var session = await db.BeginAsync(user: "ada");
await widgets.InsertAsync(session, new WidgetRow
{
    Id = "w1",
    Name = "Nebula",
    Category = "lamps",
    Price = 3.4567m,
});
await session.CommitAsync();
```

`BeginAsync` opens a transaction. Nothing is committed until `CommitAsync`. Disposing the session without committing rolls the transaction back.

`OpenAsync` opens a connection with no transaction. Use it for reads. A single autocommit write can run there too. Several writes that must succeed or fail together belong in `BeginAsync`.

## What the base class adds

Every table has `Created`, `Modified`, `User`, and `Version`.

| Column | Who writes it |
| --- | --- |
| `Created` | Insert, and the insert half of an upsert. Later updates leave it alone |
| `Modified` | Every write |
| `User` | `SqliteSession.User`, or `"System"` when the session was opened without a user |
| `Version` | The database. Stored versions start at 1 and increase by one on each write |

`Created` and `Modified` are UTC ticks. `CreatedUtc` and `ModifiedUtc` project them as `DateTimeOffset` values with a zero offset.

## Apply the schema before the first query

`Database.ApplyAsync` creates missing tables and indexes, and adds columns SQLite can add in place. Call it at startup:

```csharp
IReadOnlyList<SchemaChange> changes = await Database.ApplyAsync(db);
foreach (SchemaChange change in changes)
{
    logger.LogInformation("schema: {Change}", change);
}
```

An empty list means the database already matches. Details, renames, and rebuilds are in [Schema](schema.md).

## Next

- [Rows and repositories](rows-and-repositories.md) for updates, upserts, and keys.
- [Queries](queries.md) for filters and pages.
- [Values and sessions](values-and-sessions.md) for `decimal`, time, and hand-written SQL.
