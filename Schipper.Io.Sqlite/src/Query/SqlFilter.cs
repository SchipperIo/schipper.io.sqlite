using Microsoft.Data.Sqlite;
using Schipper.Io.Sqlite.Model;

namespace Schipper.Io.Sqlite.Query;

/// <summary>
/// A read qualifier: a WHERE fragment plus the values it binds.
///
/// The division of responsibility is the whole safety property. The <see cref="Clause"/> is
/// developer-authored and expected to be a compile-time constant; the values are <b>always</b> bound
/// as parameters and never interpolated. A clause assembled from request data would be a SQL
/// injection hole no matter how carefully the values travelled, so don't build one.
///
/// <code>
/// new SqlFilter("StoreId = $storeId AND Status IN (2,3,4)", ("$storeId", storeId.ToString()))
/// </code>
/// </summary>
public sealed class SqlFilter
{
    /// <summary>The unfiltered filter. Composes away to nothing.</summary>
    public static readonly SqlFilter None = new(string.Empty);

    private readonly (string Name, object? Value)[] _parameters;

    /// <summary>Creates a filter from a developer-authored clause and the values it binds.</summary>
    public SqlFilter(string clause, params (string Name, object? Value)[] parameters)
    {
        Clause = clause;
        _parameters = parameters;
    }

    /// <summary>The WHERE fragment, without the keyword. Empty means unfiltered.</summary>
    public string Clause { get; }

    /// <summary>Whether this filter qualifies anything.</summary>
    public bool IsEmpty => string.IsNullOrWhiteSpace(Clause);

    /// <summary>Binds this filter's values to a command.</summary>
    public void Bind(SqliteCommand command)
    {
        foreach (var (name, value) in _parameters)
        {
            command.Parameters.AddWithValue(name, SqliteValue.Parameter(value));
        }
    }

    /// <summary>Combines with AND. Either side may be empty.</summary>
    public SqlFilter And(SqlFilter? other)
    {
        if (other is null || other.IsEmpty)
        {
            return this;
        }

        return IsEmpty
            ? other
            : new SqlFilter($"({Clause}) AND ({other.Clause})", [.. _parameters, .. other._parameters]);
    }

    /// <summary>
    /// Assembles the final statement for a generated repository read, and binds everything to
    /// <paramref name="command"/>.
    ///
    /// Keyset pagination rather than OFFSET: <paramref name="afterColumn"/> is the key, ids are
    /// UUIDv7 and already sort chronologically, so a page boundary is just "the last id I saw".
    /// OFFSET would rescan every skipped row and can also skip or repeat rows if the table changes
    /// underneath a paging client. <paramref name="orderColumn"/> is the full ORDER BY list,
    /// including direction — e.g. <c>Id DESC</c>.
    /// </summary>
    public static string Compose(
        string select,
        SqlFilter? filter,
        SqliteCommand command,
        string? afterColumn,
        object? afterValue,
        string orderColumn,
        int? limit)
    {
        string? where = null;

        if (filter is { IsEmpty: false })
        {
            where = "(" + filter.Clause + ")";
            filter.Bind(command);
        }

        if (afterColumn is not null && afterValue is not null)
        {
            var cursor = afterColumn + " < $__after";
            where = where is null ? cursor : where + " AND " + cursor;
            command.Parameters.AddWithValue("$__after", SqliteValue.Parameter(afterValue));
        }

        var sql = where is null ? select : select + " WHERE " + where;
        sql += " ORDER BY " + orderColumn;

        if (limit is not null)
        {
            sql += " LIMIT $__limit";
            command.Parameters.AddWithValue("$__limit", limit.Value);
        }

        return sql + ";";
    }
}
