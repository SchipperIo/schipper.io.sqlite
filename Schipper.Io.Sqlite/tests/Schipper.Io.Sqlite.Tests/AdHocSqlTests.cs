using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Schipper.Io.Sqlite.Model;

namespace Schipper.Io.Sqlite.Tests;

/// <summary>
/// The session's ad-hoc SQL helpers, against a real database.
///
/// The generator covers rows addressed by identity and deliberately stops there, so every aggregate
/// and every join in an application above this is hand-written SQL. These helpers exist so that code
/// is a statement and a mapping rather than forty lines of command plumbing repeated per query — and
/// so the one line that is easy to leave out cannot be.
/// </summary>
[TestClass]
public sealed class AdHocSqlTests
{
    private string _path = "";

    private SqliteDb Db => new($"Data Source={_path}");

    [TestInitialize]
    public async Task CreateDatabaseAsync()
    {
        _path = Path.Combine(Path.GetTempPath(), $"schipper-adhoc-{Guid.NewGuid():n}.db");

        await using var session = await Db.BeginAsync("test");

        await session.ExecuteAsync("CREATE TABLE Widgets (Id TEXT NOT NULL PRIMARY KEY, Name TEXT NOT NULL, Price INTEGER NOT NULL);");
        await session.CommitAsync();
    }

    [TestCleanup]
    public void DeleteDatabase()
    {
        SqliteConnection.ClearAllPools();

        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            if (File.Exists(file)) File.Delete(file);
        }
    }

    [TestMethod]
    public async Task Scalar_reads_an_aggregate()
    {
        await SeedAsync(("a", "Alpha", 100), ("b", "Beta", 250));

        await using var session = await Db.OpenAsync();

        Assert.AreEqual(2, await session.ScalarAsync<int>("SELECT COUNT(*) FROM Widgets;"));
        Assert.AreEqual(350L, await session.ScalarAsync<long>("SELECT SUM(Price) FROM Widgets;"));
    }

    /// <summary>
    /// <c>SUM</c> over no rows is NULL, not zero. Mapping that to <c>default</c> rather than throwing
    /// is the whole reason a caller who wants zero has to write <c>COALESCE</c> and mean it.
    /// </summary>
    [TestMethod]
    public async Task Scalar_maps_a_null_aggregate_to_default()
    {
        await using var session = await Db.OpenAsync();

        Assert.AreEqual(0L, await session.ScalarAsync<long>("SELECT SUM(Price) FROM Widgets;"));
        Assert.AreEqual(0, await session.ScalarAsync<int>("SELECT COUNT(*) FROM Widgets;"));
    }

    [TestMethod]
    public async Task Values_are_bound_not_interpolated()
    {
        await SeedAsync(("a", "Alpha", 100), ("b", "Beta", 250));

        await using var session = await Db.OpenAsync();

        // A name that would end the statement and start another if it were ever concatenated in.
        var count = await session.ScalarAsync<int>(
            "SELECT COUNT(*) FROM Widgets WHERE Name = $name;",
            CancellationToken.None,
            ("$name", "'; DROP TABLE Widgets; --"));

        Assert.AreEqual(0, count);

        // The table is still there, which is the actual assertion.
        Assert.AreEqual(2, await session.ScalarAsync<int>("SELECT COUNT(*) FROM Widgets;"));
    }

    [TestMethod]
    public async Task Query_maps_every_row()
    {
        await SeedAsync(("a", "Alpha", 100), ("b", "Beta", 250), ("c", "Gamma", 75));

        await using var session = await Db.OpenAsync();

        var rows = await session.QueryAsync(
            "SELECT Name AS Name, Price AS Price FROM Widgets WHERE Price >= $floor ORDER BY Price;",
            reader => (
                Name: reader.GetString(reader.GetOrdinal("Name")),
                Price: reader.GetInt64(reader.GetOrdinal("Price"))),
            CancellationToken.None,
            ("$floor", 100L));

        CollectionAssert.AreEqual(new[] { "Alpha", "Beta" }, rows.Select(r => r.Name).ToArray());
        CollectionAssert.AreEqual(new[] { 100L, 250L }, rows.Select(r => r.Price).ToArray());
    }

    /// <summary>
    /// The line that is easy to leave out. A command built by hand without
    /// <c>command.Transaction = session.Transaction</c> compiles and works right up until the session
    /// actually has a transaction — at which point it runs outside it and cannot see the writes it is
    /// supposed to be part of. Reading uncommitted work back through the same session is what proves
    /// the helper attaches it.
    /// </summary>
    [TestMethod]
    public async Task Commands_enlist_in_the_session_transaction()
    {
        await using var session = await Db.BeginAsync("test");

        await session.ExecuteAsync(
            "INSERT INTO Widgets (Id, Name, Price) VALUES ($id, $name, $price);",
            CancellationToken.None,
            ("$id", "a"),
            ("$name", "Alpha"),
            ("$price", 100L));

        Assert.AreEqual(1, await session.ScalarAsync<int>("SELECT COUNT(*) FROM Widgets;"));

        // And nothing outside it sees the row until the commit.
        await using (var other = await Db.OpenAsync())
        {
            Assert.AreEqual(0, await other.ScalarAsync<int>("SELECT COUNT(*) FROM Widgets;"));
        }

        await session.CommitAsync();

        await using var after = await Db.OpenAsync();
        Assert.AreEqual(1, await after.ScalarAsync<int>("SELECT COUNT(*) FROM Widgets;"));
    }

    /// <summary>
    /// The driver's default for decimal is TEXT, which compares lexicographically. Binding one
    /// without going through <see cref="SqliteValue"/> has to fail here rather than write a value
    /// that will later rank "9" above "10".
    /// </summary>
    [TestMethod]
    public async Task Bind_rejects_decimal_datetimeoffset_and_guid()
    {
        await using var session = await Db.OpenAsync();

        await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            session.ScalarAsync<int>(
                "SELECT COUNT(*) FROM Widgets WHERE Price = $p;",
                CancellationToken.None,
                ("$p", 4.50m)));

        await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            session.ScalarAsync<int>(
                "SELECT COUNT(*) FROM Widgets WHERE Id = $id;",
                CancellationToken.None,
                ("$id", Guid.NewGuid())));

        var thrown = await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            session.ScalarAsync<int>(
                "SELECT COUNT(*) FROM Widgets WHERE Name = $n;",
                CancellationToken.None,
                ("$n", DateTimeOffset.UtcNow)));

        StringAssert.Contains(thrown.Message, "SqliteValue");
    }

    [TestMethod]
    public async Task DisposeAsync_AfterCallerRolledBack_DoesNotThrow()
    {
        await using var session = await Db.BeginAsync("test");
        await session.Transaction!.RollbackAsync();
        await session.DisposeAsync();

        await using var again = await Db.OpenAsync();
        Assert.AreEqual(0, await again.ScalarAsync<int>("SELECT COUNT(*) FROM Widgets;"));
    }

    [TestMethod]
    public async Task Execute_reports_how_many_rows_it_changed()
    {
        await SeedAsync(("a", "Alpha", 100), ("b", "Beta", 250));

        await using var session = await Db.BeginAsync("test");

        var changed = await session.ExecuteAsync(
            "UPDATE Widgets SET Price = Price * 2 WHERE Price >= $floor;",
            CancellationToken.None,
            ("$floor", 200L));

        Assert.AreEqual(1, changed);

        await session.CommitAsync();
    }

    private async Task SeedAsync(params (string Id, string Name, long Price)[] widgets)
    {
        await using var session = await Db.BeginAsync("test");

        foreach (var (id, name, price) in widgets)
        {
            await session.ExecuteAsync(
                "INSERT INTO Widgets (Id, Name, Price) VALUES ($id, $name, $price);",
                CancellationToken.None,
                ("$id", id),
                ("$name", name),
                ("$price", price));
        }

        await session.CommitAsync();
    }
}
