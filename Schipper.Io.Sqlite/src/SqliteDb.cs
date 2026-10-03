using Microsoft.Data.Sqlite;
using Schipper.Io.Sqlite.Model;

namespace Schipper.Io.Sqlite;

/// <summary>
/// Opens connections and hands out <see cref="SqliteSession"/>s.
///
/// PRAGMAs are applied on every open rather than once at startup, because <c>busy_timeout</c> is a
/// per-connection setting that resets to zero under pooling — a connection handed back from the pool
/// has none of it. <c>CommandTimeout</c> does not set it either (efcore#28135), which is what made
/// this worth writing down the first time.
/// </summary>
public sealed class SqliteDb(string connectionString)
{
    // Per-connection: busy_timeout resets under pooling; foreign_keys defaults OFF on a new
    // connection. journal_mode is persistent on the file, so it is applied once per SqliteDb.
    private const string SessionPragmas = """
        PRAGMA busy_timeout = 5000;
        PRAGMA synchronous = NORMAL;
        PRAGMA foreign_keys = ON;
        PRAGMA temp_store = MEMORY;
        """;

    private const string WalPragma = "PRAGMA journal_mode = WAL;";

    private int _walApplied;

    /// <summary>The connection string every session is opened against.</summary>
    public string ConnectionString { get; } = connectionString;

    /// <summary>Opens a session with no transaction. For reads.</summary>
    public async Task<SqliteSession> OpenAsync(CancellationToken cancellationToken = default)
    {
        var connection = await OpenConnectionAsync(cancellationToken);
        return new SqliteSession(connection, transaction: null);
    }

    /// <summary>
    /// Opens a read session attributed to a user. Reads do not stamp anything, but the session
    /// carries the attribution so a caller can promote it without re-plumbing.
    /// </summary>
    public async Task<SqliteSession> OpenAsync(string user, CancellationToken cancellationToken = default)
    {
        var connection = await OpenConnectionAsync(cancellationToken);
        return new SqliteSession(connection, transaction: null) { User = user };
    }

    /// <summary>
    /// Opens a session inside a transaction. For any operation that writes more than one row.
    ///
    /// Nothing is committed unless <see cref="SqliteSession.CommitAsync"/> is called; disposing
    /// without committing rolls back. That is deliberate — the failure mode of forgetting is
    /// "nothing happened", which is loud, rather than "some of it happened", which is not.
    /// </summary>
    public async Task<SqliteSession> BeginAsync(CancellationToken cancellationToken = default)
    {
        var connection = await OpenConnectionAsync(cancellationToken);
        var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        return new SqliteSession(connection, transaction);
    }

    /// <summary>
    /// Opens a write transaction attributed to a user. Every insert, update and upsert on the
    /// session stamps <c>User</c> with this value.
    /// </summary>
    public async Task<SqliteSession> BeginAsync(string user, CancellationToken cancellationToken = default)
    {
        var connection = await OpenConnectionAsync(cancellationToken);
        var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        return new SqliteSession(connection, transaction) { User = user };
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);

        var includeWal = _walApplied == 0;
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = includeWal ? SessionPragmas + WalPragma : SessionPragmas;
        await pragma.ExecuteNonQueryAsync(cancellationToken);

        if (includeWal)
        {
            _walApplied = 1;
        }

        return connection;
    }
}

/// <summary>
/// A connection, and optionally the transaction everything on it must enlist in.
///
/// Generated repositories take one of these and read <see cref="Connection"/>,
/// <see cref="Transaction"/> and <see cref="User"/> from it, so a caller sets the transaction
/// boundary and the attribution once and every write on the session inherits both.
/// </summary>
public sealed class SqliteSession(SqliteConnection connection, SqliteTransaction? transaction) : IAsyncDisposable
{
    /// <summary>Attribution used when no user is supplied.</summary>
    public const string SystemUser = "System";

    private bool _committed;
    private bool _disposed;
    private Dictionary<string, SqliteCommand>? _commands;

    /// <summary>The open connection. Exposed so callers can run SQL the generator does not cover.</summary>
    public SqliteConnection Connection { get; } = connection;

    /// <summary>The transaction every command on this session enlists in, or null for reads.</summary>
    public SqliteTransaction? Transaction { get; } = transaction;

    /// <summary>
    /// Who the writes on this session are attributed to. Set once when the session is opened —
    /// typically from the authenticated principal — and read by every generated write.
    ///
    /// Defaults to <see cref="SystemUser"/> so that background work, seeding and migrations
    /// attribute honestly rather than to whoever happened to be last through the door.
    /// </summary>
    public string User { get; init; } = SystemUser;

    // ----------------------------------------------------------------------------------------------
    // Ad-hoc SQL
    // ----------------------------------------------------------------------------------------------
    //
    // The generator covers rows addressed by identity. It deliberately does not cover aggregates,
    // grouping or joins — a repository method that answered a screen's question would drag that
    // screen's assumptions into the data layer — so services write those by hand, and the shape they
    // wrote was the same forty lines every time: create the command, remember to attach the
    // transaction, bind, read, map by ordinal.
    //
    // Forgetting `command.Transaction = session.Transaction` is the one that bites. It compiles, and
    // it works right up until the session actually has a transaction, at which point the command runs
    // outside it — reading state the transaction has not committed, or blocking on the writer it
    // should have been part of. Attaching it here means it cannot be forgotten.
    //
    // **The same rule as SqlFilter applies and is not enforceable by a signature: the SQL is
    // developer-authored and expected to be a compile-time constant; every value is bound.** These
    // helpers make binding the path of least resistance, which is the most a helper can do.

    /// <summary>
    /// Runs a statement returning one value — a <c>COUNT</c>, a <c>SUM</c>, an <c>EXISTS</c>.
    ///
    /// Returns <c>default</c> for SQL NULL, which is what an aggregate over no rows gives: a caller
    /// wanting zero should say <c>COALESCE(..., 0)</c> rather than rely on the mapping.
    /// </summary>
    public async Task<T?> ScalarAsync<T>(
        string sql,
        CancellationToken cancellationToken = default,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = CreateCommand(sql, parameters);
        var value = await ExecuteScalarAsync(command, cancellationToken);

        return value is null or DBNull ? default : (T)Convert.ChangeType(value, typeof(T));
    }

    /// <summary>
    /// Runs a query and maps every row.
    ///
    /// <paramref name="map"/> is called once per row and is expected to read by
    /// <c>GetOrdinal("Name")</c> rather than by position — several same-typed columns in a row is
    /// exactly the shape that gets silently transposed when positions are hard-coded. The lookup is a
    /// hash of a short string against a column list, which is not the cost worth optimising against a
    /// row read.
    /// </summary>
    public async Task<List<T>> QueryAsync<T>(
        string sql,
        Func<SqliteDataReader, T> map,
        CancellationToken cancellationToken = default,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = CreateCommand(sql, parameters);
        await using var reader = await ExecuteReaderAsync(command, cancellationToken);

        var rows = new List<T>();

        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(map(reader));
        }

        return rows;
    }

    /// <summary>Runs a statement that returns no rows, and gives back the number affected.</summary>
    public async Task<int> ExecuteAsync(
        string sql,
        CancellationToken cancellationToken = default,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = CreateCommand(sql, parameters);
        return await ExecuteNonQueryAsync(command, cancellationToken);
    }

    /// <summary>
    /// A command already enlisted in this session's transaction.
    ///
    /// Public because the generated repositories use it too. Everything that reaches the database
    /// through this layer is built here, which is what gives <see cref="SqliteMetrics"/> somewhere to
    /// stand — and what means the transaction cannot be forgotten by any of the callers.
    /// </summary>
    public SqliteCommand CreateCommand(string sql = "")
    {
        var command = Connection.CreateCommand();

        command.Transaction = Transaction;
        command.CommandText = sql;

        return command;
    }

    /// <summary>
    /// A session-owned command for a fixed statement. Parameters are cleared on each call.
    /// Do not dispose it — the session does, and disposing would drop the prepared statement
    /// the next <c>GetAsync</c> on this session is trying to reuse.
    /// </summary>
    public SqliteCommand Command(string sql)
    {
        _commands ??= new Dictionary<string, SqliteCommand>(StringComparer.Ordinal);

        if (_commands.TryGetValue(sql, out var command))
        {
            command.Parameters.Clear();
            return command;
        }

        command = CreateCommand(sql);
        _commands[sql] = command;
        return command;
    }

    /// <summary>Runs a reader, reporting the execution to <see cref="SqliteMetrics"/>.</summary>
    public async Task<SqliteDataReader> ExecuteReaderAsync(
        SqliteCommand command,
        CancellationToken cancellationToken = default)
    {
        if (!SqliteMetrics.IsObserved)
        {
            return await command.ExecuteReaderAsync(cancellationToken);
        }

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var reader = await command.ExecuteReaderAsync(cancellationToken);

        // Timed at execution rather than across the read loop. This measures the statement, not the
        // caller's mapping — conflating the two would make a slow projection look like a slow query.
        Measure(command, started);

        return reader;
    }

    /// <summary>Runs a non-query, reporting the execution to <see cref="SqliteMetrics"/>.</summary>
    public async Task<int> ExecuteNonQueryAsync(
        SqliteCommand command,
        CancellationToken cancellationToken = default)
    {
        if (!SqliteMetrics.IsObserved)
        {
            return await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var started = System.Diagnostics.Stopwatch.GetTimestamp();

        try
        {
            return await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            // In a finally, so a statement that threw is still counted. A failing write that is
            // retried is exactly the kind of thing worth seeing in the count.
            Measure(command, started);
        }
    }

    /// <summary>Runs a scalar, reporting the execution to <see cref="SqliteMetrics"/>.</summary>
    public async Task<object?> ExecuteScalarAsync(
        SqliteCommand command,
        CancellationToken cancellationToken = default)
    {
        if (!SqliteMetrics.IsObserved)
        {
            return await command.ExecuteScalarAsync(cancellationToken);
        }

        var started = System.Diagnostics.Stopwatch.GetTimestamp();

        try
        {
            return await command.ExecuteScalarAsync(cancellationToken);
        }
        finally
        {
            Measure(command, started);
        }
    }

    private static void Measure(SqliteCommand command, long started) =>
        SqliteMetrics.Report(
            command.CommandText,
            System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);

    /// <summary>Builds a command already enlisted in this session's transaction, with values bound.</summary>
    private SqliteCommand CreateCommand(string sql, (string Name, object? Value)[] parameters)
    {
        var command = CreateCommand(sql);

        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, SqliteValue.Parameter(value));
        }

        return command;
    }

    /// <summary>Commits the transaction. Throws if the session was opened without one.</summary>
    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        if (Transaction is null)
        {
            throw new InvalidOperationException(
                "This session was opened without a transaction. Use SqliteDb.BeginAsync for writes.");
        }

        await Transaction.CommitAsync(cancellationToken);
        _committed = true;
    }

    /// <summary>Rolls back an uncommitted transaction, then disposes the connection.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            if (_commands is not null)
            {
                foreach (var command in _commands.Values)
                {
                    await command.DisposeAsync();
                }

                _commands = null;
            }

            if (Transaction is not null && !_committed)
            {
                try
                {
                    await Transaction.RollbackAsync();
                }
                catch (InvalidOperationException)
                {
                    // The caller already committed or rolled back through Transaction.
                }
            }
        }
        finally
        {
            if (Transaction is not null)
            {
                await Transaction.DisposeAsync();
            }

            await Connection.DisposeAsync();
        }
    }
}
