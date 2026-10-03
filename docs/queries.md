# Queries

[Index](README.md)

Reads are either a keyset page or a stream. `SqlFilter` carries a WHERE fragment you wrote and the values the library binds.

## SqlFilter

```csharp
using Schipper.Io.Sqlite.Query;

var lamps = new SqlFilter(
    "Category = $category AND Price >= $minPrice",
    ("$category", "lamps"),
    ("$minPrice", SqliteValue.FromDecimal(3.4567m)));

SqlFilter open = lamps.And(new SqlFilter("Note IS NOT NULL"));
SqlFilter everything = SqlFilter.None;
```

`Clause` is the fragment without the `WHERE` keyword. `IsEmpty` is true for `SqlFilter.None` and for a blank clause. `And` parenthesizes both sides and concatenates the bound values. An empty side disappears.

Write the clause as a constant in source. The values are always parameters. A clause built by concatenating request text is an injection hole even when the values go through `Bind`.

`Bind` adds the parameters to a `SqliteCommand`. Generated repositories call it for you. `decimal`, `DateTimeOffset`, `DateTime`, `Guid`, `DateOnly`, `TimeOnly`, and `TimeSpan` have to be converted with `SqliteValue` before they are placed in the parameter list. `SqliteValue.Parameter` throws if one of those arrives raw, because the driver would store them as TEXT. See [Values and sessions](values-and-sessions.md).

## Pages

`QueryAsync` requires `limit`. The page is ordered by the key. `HasMore` is true when at least one more row exists past this page. `QueryPage<T>` is an `IReadOnlyList<T>`, so it enumerates and indexes like a list.

```csharp
string? after = null;
while (true)
{
    QueryPage<WidgetRow> page = await widgets.QueryAsync(
        session,
        limit: 50,
        filter: lamps,
        after: after);

    foreach (WidgetRow row in page)
    {
        Use(row);
    }

    if (!page.HasMore || page.Count == 0)
    {
        break;
    }

    after = page[^1].Id;
}
```

Pass the key of the last row as `after`. The next call reads keys strictly after that value. `OFFSET` is not used. An offset would rescan every skipped row, and it can skip or repeat rows when the table changes under a paging client.

Composite-key tables have no single-column cursor. Their `QueryAsync` takes `session`, `limit`, and `filter` only.

`CountAsync` uses the same filter and does not order.

## Streams

```csharp
await foreach (WidgetRow row in widgets.StreamAsync(session, lamps, cancellationToken))
{
    Use(row);
}
```

`StreamAsync` buffers one row at a time. Use it when the caller will consume the whole match. Use `QueryAsync` when the caller displays a page.

## Ad-hoc SQL

`SqliteSession` runs statements that the generator does not know about. The command enlists in the session transaction.

```csharp
long count = await session.ScalarAsync<long>(
    "SELECT COUNT(*) FROM Widgets WHERE Category = $category",
    cancellationToken,
    ("$category", "lamps")) ?? 0;

List<string> names = await session.QueryAsync(
    "SELECT Name FROM Widgets WHERE Category = $category",
    reader => reader.GetString(reader.GetOrdinal("Name")),
    cancellationToken,
    ("$category", "lamps"));

int changed = await session.ExecuteAsync(
    "UPDATE Widgets SET Name = $name WHERE Id = $id",
    cancellationToken,
    ("$name", "Nebula"),
    ("$id", "w1"));
```

`ScalarAsync` returns `default` when the result is SQL NULL. An aggregate over no rows is NULL. Write `COALESCE(..., 0)` in the SQL when the caller wants zero.

`QueryAsync` calls `map` once per row. Read columns with `GetOrdinal("Name")`. Several columns of the same storage class are easy to swap when the code uses a hard-coded position.

`ExecuteAsync` returns the number of rows changed.

`Command(sql)` returns a cached `SqliteCommand` for that text and clears its parameters. The session owns it. Disposing it drops the prepared statement the next call on this session is trying to reuse. Generated repositories use `Command` for statements they run on every request. Hand-written SQL is clearer through `ScalarAsync`, `QueryAsync`, and `ExecuteAsync`.

`CreateCommand` returns a new command already pointed at `Connection` and `Transaction`. Dispose that one.

## Map

Each repository has a public static `Map(SqliteDataReader)` that reads a row by column name. Use it when a hand-written `SELECT` returns the same column list as `Columns`.
