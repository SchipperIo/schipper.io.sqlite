# Values and sessions

[Index](README.md)

SQLite stores NULL, INTEGER, REAL, TEXT, and BLOB. Every other CLR type is a mapping this library chooses, and hand-written SQL uses the same mapping through `SqliteValue`.

## Type mapping

| C# | Stored as | Encoding |
| --- | --- | --- |
| `string` | TEXT | The text itself |
| `char` | TEXT | One character |
| `bool` | INTEGER | 0 or 1 |
| `byte`, `sbyte`, `short`, `ushort`, `int`, `uint`, `long` | INTEGER | SQLite integers are 64-bit |
| `float`, `double` | REAL | IEEE values |
| `decimal` | INTEGER | Scaled by 10ⁿ. Default n is 4. `[Scale(n)]` overrides it, up to 10 |
| `Guid` | TEXT | Canonical lowercase `8-4-4-4-12` |
| `DateTimeOffset` | INTEGER | UTC ticks. The offset is dropped |
| `DateTime` | INTEGER | UTC ticks. `DateTimeKind.Unspecified` is treated as UTC |
| `DateOnly` | INTEGER | Day number |
| `TimeOnly`, `TimeSpan` | INTEGER | Ticks |
| `enum` | INTEGER | The underlying value |
| `byte[]` | BLOB | The bytes |

Nullable forms of each of these are mapped. `ulong` is rejected (`SQLG007`) because SQLite INTEGER is signed 64-bit. `float`, `double`, and `byte[]` keys are rejected (`SQLG008`). Any other property type is `SQLG004`.

`decimal` is stored as a scaled integer so comparisons are numeric and exact. `SqliteValue.FromDecimal` throws `OverflowException` rather than truncating. `[Scale]` must be an `int` in 0..10 (`SQLG013`); omitting it still defaults to 4. Scale is part of the schema hash. Changing `[Scale]` on a column that already holds data is an incompatible schema change and requires `[Rebuild]` plus `SchemaUpdate.Rebuild`. Treat the scale as part of the schema.

`DateTimeOffset` is stored as UTC ticks. If the original offset matters, give it a column of its own. `Guid` is TEXT so a `sqlite3` prompt can read it, and so a UUIDv7 key sorts chronologically as text for keyset pages.

## SqliteValue

```csharp
using Schipper.Io.Sqlite.Model;

long storedPrice = SqliteValue.FromDecimal(3.4567m);          // scale 4
decimal price = SqliteValue.ToDecimal(storedPrice);

long ticks = SqliteValue.FromDateTimeOffset(DateTimeOffset.UtcNow);
DateTimeOffset instant = SqliteValue.ToDateTimeOffset(ticks); // offset zero

string guidText = SqliteValue.FromGuid(id);
Guid idAgain = SqliteValue.ToGuid(guidText);
```

The pairs are `FromDecimal` / `ToDecimal`, `FromGuid` / `ToGuid`, `FromDateTimeOffset` / `ToDateTimeOffset`, `FromDateTime` / `ToDateTime`, `FromDateOnly` / `ToDateOnly`, `FromTimeOnly` / `ToTimeOnly`, `FromTimeSpan` / `ToTimeSpan`, and `FromChar` / `ToChar`.

`DefaultScale` is 4. `MaxScale` is 10. Pass the same scale to `FromDecimal` and `ToDecimal` that `[Scale]` declares on the property.

`SqliteValue.Parameter` returns the object you may pass to `AddWithValue`. Null becomes `DBNull.Value`. `decimal`, `DateTimeOffset`, `DateTime`, `Guid`, `DateOnly`, `TimeOnly`, and `TimeSpan` throw `ArgumentException`. Convert them first. Generated repositories already convert. Ad-hoc parameters go through `Parameter` inside `ScalarAsync`, `QueryAsync`, and `ExecuteAsync`, so the same throw happens there.

## Sessions

`SqliteDb` opens every connection with:

```text
PRAGMA busy_timeout = 5000;
PRAGMA synchronous = NORMAL;
PRAGMA foreign_keys = ON;
PRAGMA temp_store = MEMORY;
```

`PRAGMA journal_mode = WAL` is applied once per `SqliteDb` instance. `busy_timeout` and `foreign_keys` are set on every open because pooling hands back connections that have lost them.

| Method | Session |
| --- | --- |
| `OpenAsync()` | Read connection. `User` is `SqliteSession.SystemUser` (`"System"`) |
| `OpenAsync(user)` | Read connection attributed to `user` |
| `BeginAsync()` | Write transaction. Commit with `CommitAsync` |
| `BeginAsync(user)` | Write transaction attributed to `user` |

`SqliteSession.Connection` and `Transaction` are public. Generated code and `CreateCommand` enlist in `Transaction`. `CommitAsync` throws when the session was opened with `OpenAsync`, because there is no transaction.

Dispose with `await using`. A transaction that was not committed rolls back. If the caller already committed or rolled back `Transaction` directly, dispose still closes the connection and does not throw. A second `DisposeAsync` is a no-op. The connection closes on dispose.

`User` is stamped onto `AuditedRow.User` by insert, update, and upsert. Set it by opening the session with a user. Assigning `session.User` before the write has the same effect.

## Metrics

```csharp
SqliteMetrics.Observer = executed =>
{
    logger.LogDebug("{Sql} in {ElapsedMs} ms", executed.Sql, executed.ElapsedMs);
};
```

`SqliteCommandExecuted` carries the SQL text and the elapsed milliseconds. The clock covers `ExecuteReaderAsync`, `ExecuteNonQueryAsync`, and `ExecuteScalarAsync`, including statements that throw. It stops when the statement returns, so a slow row mapper is not counted as database time.

`Observer` is a static property. Set it once at startup. Leave it null and the timing calls are skipped. `SqliteMetrics.IsObserved` reports whether an observer is installed. `Report` is what the session calls.

## Concurrency exception

`SqliteConcurrencyException` is thrown by `UpdateAsync` and `UpsertAsync` when the version predicate matches nothing. It is a distinct type from `System.Data.DBConcurrencyException`. Catch `Schipper.Io.Sqlite.SqliteConcurrencyException` by its full name when both are in scope.

`Table` is the table name. `Key` is the key text. The message says the row was changed or removed since it was read.
