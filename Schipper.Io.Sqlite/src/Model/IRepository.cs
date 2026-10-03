using Schipper.Io.Sqlite.Query;

namespace Schipper.Io.Sqlite.Model;

/// <summary>
/// The operations every generated repository provides that do not name the key.
///
/// This is the interface the <see cref="IAuditedRow"/> requirement buys: because every row is known
/// to carry a version and an author, generic code — an outbox writer, an audit-trail snapshotter, a
/// substitutable fake in a test — can be written over rows without reflection or further generation.
///
/// Key-shaped operations live on <see cref="IKeyedRepository{TRow,TKey}"/>, which composite-key
/// tables cannot implement.
/// </summary>
public interface IRepository<TRow>
    where TRow : class, IAuditedRow
{
    /// <summary>Filtered read, streamed. Nothing is buffered; one row is live at a time.</summary>
    IAsyncEnumerable<TRow> StreamAsync(SqliteSession session, SqlFilter? filter = null, CancellationToken cancellationToken = default);

    /// <summary>Filtered read with keyset pagination. <paramref name="limit"/> is required.</summary>
    Task<QueryPage<TRow>> QueryAsync(SqliteSession session, int limit, SqlFilter? filter = null, CancellationToken cancellationToken = default);

    /// <summary>How many rows the same filter matches, ignoring any cursor.</summary>
    Task<int> CountAsync(SqliteSession session, SqlFilter? filter = null, CancellationToken cancellationToken = default);

    /// <summary>Inserts a new row, stamping Created, Modified, User and Version.</summary>
    Task<int> InsertAsync(SqliteSession session, TRow row, CancellationToken cancellationToken = default);

    /// <summary>Version-checked full-row replace. Throws <see cref="SqliteConcurrencyException"/> on a stale row.</summary>
    Task UpdateAsync(SqliteSession session, TRow row, CancellationToken cancellationToken = default);

    /// <summary>Version-checked full-row replace. Returns false instead of throwing.</summary>
    Task<bool> TryUpdateAsync(SqliteSession session, TRow row, CancellationToken cancellationToken = default);

    /// <summary>Insert-or-update-non-null. Throws <see cref="SqliteConcurrencyException"/> when a supplied version was stale.</summary>
    Task UpsertAsync(SqliteSession session, TRow row, CancellationToken cancellationToken = default);

    /// <summary>Insert-or-update-non-null. Returns false instead of throwing.</summary>
    Task<bool> TryUpsertAsync(SqliteSession session, TRow row, CancellationToken cancellationToken = default);
}

/// <summary>
/// Adds the key-shaped operations, for tables with a single-column primary key.
///
/// Composite-key tables get the same generated methods but implement only
/// <see cref="IRepository{TRow}"/> — a two-part key cannot be expressed as one <typeparamref name="TKey"/>
/// without boxing it into a tuple, which would trade real type safety for an interface nobody asked for.
/// </summary>
public interface IKeyedRepository<TRow, in TKey> : IRepository<TRow>
    where TRow : class, IAuditedRow
    where TKey : notnull
{
    /// <summary>Reads one row by key, or null.</summary>
    Task<TRow?> GetAsync(SqliteSession session, TKey key, CancellationToken cancellationToken = default);

    /// <summary>Deletes by key. Returns the number of rows removed.</summary>
    Task<int> DeleteAsync(SqliteSession session, TKey key, CancellationToken cancellationToken = default);
}
