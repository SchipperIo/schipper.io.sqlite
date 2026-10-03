# Rows and repositories

[Index](README.md)

The generator reads each `[Table]` class or record class and emits `{TypeName}Repository`. A single-column key implements `IKeyedRepository<TRow, TKey>`. A composite key implements `IRepository<TRow>` only. Both constrain `TRow` to `IAuditedRow`.

The generator itself requires the `AuditedRow` base class, so it can find the four audit columns. `IAuditedRow` is the interface generic code uses.

## Attributes

Declared in `Schipper.Io.Sqlite.Model` unless noted.

| Attribute | Target | Effect |
| --- | --- | --- |
| `[Table("Name")]` | class or record class | Table name. The type must derive from `AuditedRow` and have a public parameterless constructor |
| `[Key]` | property | Primary key. Repeat it for a composite key |
| `[Unique]` | property | `UNIQUE` constraint |
| `[Index]` | property | An index on this column |
| `[TableIndex("A", "B")]` | class | A composite index, in that column order. Repeat the attribute for another index |
| `[References("Other", "Id")]` | property | Foreign key. `Cascade = true` emits `ON DELETE CASCADE` |
| `[SqlDefault("0")]` | property | A SQL default expression, copied verbatim into DDL |
| `[Scale(4)]` | property | Decimal digits for a `decimal` column. Must be an `int` in 0..10. The default scale is 4. See [Values and sessions](values-and-sessions.md) |
| `[RenameFrom("Old")]` | property or class | The previous column or table name. See [Schema](schema.md) |
| `[Rebuild]` | class | Allows a table rebuild when combined with `SchemaUpdate.Rebuild` |
| `[DatabaseNamespace("App.Persistence")]` | class | Namespace of the generated `Database` type |

`TableIndexAttribute.Where` is a partial-index predicate, for example `"DispatchedAt IS NULL"`. `Unique = true` makes that index unique.

Column type and nullability come from the C# property. A `string` is `NOT NULL`. A `string?` is nullable. There is no attribute that restates either. Mapped setters must be public or internal. `init` is accepted.

Table and column names are quoted in generated SQL, so SQLite keywords such as `Order` and `Group` are legal names. A C# reserved keyword as a property name is emitted with `@`.

An index that serves a paged read ends with the key column. Generated reads order by the key. `[TableIndex("Category", "Id")]` can satisfy that order. `[TableIndex("Category")]` leaves SQLite sorting every matching row on every page.

## Repository methods

Construct the repository with `new`. It holds no connection.

```csharp
var widgets = new WidgetRowRepository();
```

`WidgetRowRepository.Table` is the table name. `WidgetRowRepository.Columns` is the column list the statements use, in order.

### Single-key tables

| Method | Behavior |
| --- | --- |
| `GetAsync(session, key)` | One row, or null |
| `DeleteAsync(session, key)` | Deletes by key. Returns the number of rows removed |
| `QueryAsync(session, limit, filter, after)` | A page ordered by the key. `after` is the last key of the previous page |
| `StreamAsync(session, filter)` | Every match, one row at a time |
| `CountAsync(session, filter)` | How many rows the filter matches |
| `InsertAsync(session, row)` | Inserts and stamps the audit columns. Returns rows written |
| `UpdateAsync(session, row)` | Replaces the row where the key and `Version` match |
| `TryUpdateAsync(session, row)` | The same write. Returns false when no row matched |
| `UpsertAsync(session, row)` | Insert, or update when the key exists |
| `TryUpsertAsync(session, row)` | The same upsert. Returns false when a supplied version was stale |

`GetAsync` and `DeleteAsync` on a composite key take one argument per key column, in property order. `IRepository<TRow>` does not include those two methods, because a composite key is not a single `TKey`. The generated class still has them.

### Version checks

`UpdateAsync` writes `WHERE` the key and `Version` equal the values on the instance you pass. A row someone else has written no longer matches.

`UpdateAsync` and `UpsertAsync` throw `SqliteConcurrencyException` when a version check fails. `Table` and `Key` on the exception identify the row. `Key` joins composite key values with `/`. The exception covers both "deleted" and "updated since read". Re-read the row and decide again.

`TryUpdateAsync` and `TryUpsertAsync` return false in that case.

```csharp
WidgetRow? row = await widgets.GetAsync(session, "w1");
if (row is null)
{
    return;
}

row.Name = "Updated";
try
{
    await widgets.UpdateAsync(session, row);
}
catch (SqliteConcurrencyException ex)
{
    logger.LogWarning("{Table} {Key} lost an update", ex.Table, ex.Key);
}
```

`InsertAsync` writes `Created`, `Modified`, `User`, and `Version` (1) back onto the instance before the insert runs, so a later `UpdateAsync` on that object already carries version 1. `TryUpdateAsync` and `TryUpsertAsync` write `Modified`, `User`, and the new `Version` onto the instance after the statement matches a row. `UpdateAsync` goes through `TryUpdateAsync`, so the same fields move on success.

### Upsert and null

A nullable property is updated with `COALESCE(excluded.column, table.column)`. A null on the object leaves the stored value in place. Upsert cannot clear a column to SQL `NULL`. `UpdateAsync` writes the value you set, including null.

`Version == 0` on an upsert means this instance was never read. The version check is skipped, and the write proceeds. Stored versions start at 1, so zero is safe as "seed this row". A non-zero version is checked, and a stale one fails the upsert.

`Created` is written on insert and left untouched on the update half of an upsert.

## Interfaces

`IRepository<TRow>` has `StreamAsync`, `QueryAsync`, `CountAsync`, `InsertAsync`, `UpdateAsync`, `TryUpdateAsync`, `UpsertAsync`, and `TryUpsertAsync`.

`IKeyedRepository<TRow, TKey>` adds `GetAsync` and `DeleteAsync`.

The generated `QueryAsync` for a single-key table takes an `after` cursor. The interface method does not, and the explicit implementation calls the generated method with `after: default`. Generic code that pages uses the concrete repository so it can pass the cursor. See [Queries](queries.md).

## Diagnostics at build time

| Id | Meaning |
| --- | --- |
| SQLG001 | The row type must derive from `AuditedRow` |
| SQLG002 | The row type has no `[Key]` |
| SQLG003 | The row type has no mappable properties |
| SQLG004 | A property type has no SQLite mapping |
| SQLG005 | A key property is nullable |
| SQLG006 | The row type is abstract |
| SQLG007 | `ulong` has no lossless SQLite mapping |
| SQLG008 | The key type is unsuitable (`float`, `double`, or `byte[]`) |
| SQLG009 | Two `[DatabaseNamespace]` values disagree |
| SQLG010 | `[DatabaseNamespace]` is not a valid namespace |
| SQLG011 | The row type has no public parameterless constructor |
| SQLG012 | A mapped property's setter is not public or internal |
| SQLG013 | `[Scale]` is not an `int` in 0..10 |

A row that fails one of these does not emit a repository. The diagnostic points at the row type or the property. `init` accessors are legal. Record classes with `{ get; set; }` (or `init`) are emitted; record structs are ignored.
