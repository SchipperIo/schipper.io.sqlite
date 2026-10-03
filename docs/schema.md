# Schema

[Index](README.md)

The generator emits DDL from the row types and a `Database` class that applies it. There is no separate SQL file.

```csharp
IReadOnlyList<SchemaChange> changes = await Database.ApplyAsync(db);
IReadOnlyList<SchemaChange> pending = await Database.DiffAsync(db);
```

`ApplyAsync` changes the database as far as the `SchemaUpdate` mode allows, and returns every change it made or found. `DiffAsync` reports the same comparison and writes nothing.

`Database` is generated into the project's root namespace. A `Database` folder under the project root often collides with that name. Put `[DatabaseNamespace("MyApp.Persistence")]` on a row type and the generated class becomes `MyApp.Persistence.Database`. One compilation has one `Database`. Every `[DatabaseNamespace]` in the project must name the same namespace.

`Database.Model` is the `SchemaModel`. `Database.Hash` is the fingerprint stored in the `__SchipperSchema` table. A matching hash skips the comparison.

`SchemaApplier.ApplyAsync(db, model, update)` is the same operation against a model you built yourself. Application code uses `Database`.

## What a difference becomes

| Difference | Result |
| --- | --- |
| New table | Created |
| New index | Created |
| New column, nullable or with a default | `ALTER TABLE ADD COLUMN` |
| New `NOT NULL` column with no default | Fails. SQLite cannot add one |
| Column removed from the row type | Reported as `Extra`. Generated statements name their columns, so the leftover column is unread |
| Column added and removed on one table | Fails, until `[RenameFrom]` explains it |
| Column retyped, or nullability changed | Fails, until a rebuild is requested |
| Decimal `[Scale]` changed | Fails as `Incompatible`, until a rebuild is requested. Scale is part of the schema hash |
| New `UNIQUE` or primary-key column on an existing table | Fails. SQLite cannot add those in place |

`ApplyAsync` throws `SqliteSchemaException` for a difference the mode cannot apply. `Changes` on the exception is the list, and each `SchemaChange.Detail` says what differed. `ToString` prints `Kind Table.Column: Detail`.

| `SchemaChangeKind` | Meaning |
| --- | --- |
| `AddTable`, `AddColumn`, `AddIndex` | Applied, in modes that allow it |
| `Rename` | `[RenameFrom]` matched a live name and the object was renamed |
| `Extra` | Something in the database is absent from the model |
| `Rebuild` | The table was rebuilt |
| `Incompatible` | The difference cannot be applied. Always fatal |

## SchemaUpdate

| Mode | What it may do |
| --- | --- |
| `Additive` | The default. Create missing tables and indexes, and add columns SQLite can add |
| `CreateOnly` | Create missing tables and indexes. A missing column is fatal |
| `VerifyOnly` | Write nothing. Any difference is fatal, including an extra column that has no SQL. A non-empty diff does not record a new hash. An empty diff may refresh the stored hash |
| `Rebuild` | Everything `Additive` does, plus rebuild tables marked `[Rebuild]` |

```csharp
await Database.ApplyAsync(db, SchemaUpdate.VerifyOnly);
```

`CreateOnly` suits a process that should fail rather than alter a shared table on startup.

## Renames

A rename looks like a drop plus an add. Guessing would add the new column, leave the old one in place, and every existing row would show the default while the previous values sat beside them. That case throws. `[RenameFrom]` names the old column or table:

```csharp
[Table("Orders")]
[RenameFrom("OldOrders")]
public sealed class OrderRow : AuditedRow
{
    [Key] public string Id { get; set; } = "";

    [RenameFrom("Amount")]
    public long Total { get; set; }
}
```

The rename runs in place and keeps the data. Leave the attribute on the type. After the database has been renamed, the old name is gone and the attribute matches nothing. On a fresh database the table is created under the new name.

## Rebuilds

SQLite cannot retype a column, change nullability, or alter a key in place. The rebuild creates a new table, copies the rows, drops the old table, and renames. It runs only when both keys are present:

```csharp
[Table("Orders")]
[Rebuild]
public sealed class OrderRow : AuditedRow
{
    // ...
}

await Database.ApplyAsync(db, SchemaUpdate.Rebuild);
```

The copy is `INSERT … SELECT`. SQLite coerces by column affinity, so a narrower type truncates. A column that left the row type is not copied. `[RenameFrom]` is honored during the copy, so a renamed column keeps its values. Without it, those values are dropped.

Foreign keys are disabled around the rebuild. `PRAGMA foreign_key_check` runs before the commit. A violation rolls the rebuild back and leaves the table as it was. A rebuild that fails does not record the new schema hash.

`[Rebuild]` on the class without `SchemaUpdate.Rebuild` at the call does not rebuild. `SchemaUpdate.Rebuild` without `[Rebuild]` on that table does not rebuild it either.

## Index changes

A new index is created. An index whose predicate changed is dropped and created again. A unique constraint added over existing duplicates fails when the unique index is built. A unique constraint removed from the row type is refused, because dropping it silently would widen the table. A `UX_{table}_{column}` index created for a later `[Unique]` on a non-key column is kept while that column still declares `[Unique]`.

Table and column identifiers are quoted in apply SQL. Identifier quotes are ignored when comparing index SQL, so the first quoted deploy does not drop and recreate every index.

A hand-written index that the model does not mention is left in place and reported as `Extra`.
