using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Schipper.Io.Sqlite.Schema;

namespace Schipper.Io.Sqlite.Tests;

/// <summary>
/// What happens when the row types change under an existing database.
///
/// These run against real SQLite files rather than a mock, because the whole question is what the
/// engine will actually accept: which columns it will let you add, what <c>PRAGMA table_info</c>
/// reports back, and where <c>ALTER TABLE</c> stops.
/// </summary>
[TestClass]
public sealed class SchemaTests
{
    private string _path = "";

    private SqliteDb Db => new($"Data Source={_path}");

    [TestInitialize]
    public void CreateDatabase() =>
        _path = Path.Combine(Path.GetTempPath(), $"schipper-schema-{Guid.NewGuid():n}.db");

    [TestCleanup]
    public void DeleteDatabase()
    {
        SqliteConnection.ClearAllPools();

        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            if (File.Exists(file)) File.Delete(file);
        }
    }

    // --- Models, written by hand so a "change" is just a different model ---------------------------

    private static ColumnDef Key(string name = "Id") =>
        new(name, "TEXT", NotNull: true, Default: null, IsKey: true, Unique: false,
            HasForeignKey: false, Definition: $"{name} TEXT NOT NULL");

    private static ColumnDef Text(string name, bool notNull = false, string? @default = null,
        string? renamedFrom = null, bool unique = false) =>
        new(name, "TEXT", notNull, @default, IsKey: false, Unique: unique, HasForeignKey: false,
            Definition: $"{name} TEXT {(notNull ? "NOT NULL" : "NULL")}" +
                        $"{(@default is null ? "" : $" DEFAULT {@default}")}{(unique ? " UNIQUE" : "")}",
            RenamedFrom: renamedFrom);

    /// <summary>
    /// The index is derived from the columns rather than hard-coded, exactly as the generator derives
    /// it from the row type — a fixed index name outlives a renamed column and then indexes something
    /// that is not there.
    /// </summary>
    private static SchemaModel Model(string hash, params ColumnDef[] columns)
    {
        var first = columns.First(c => !c.IsKey).Name;

        return new(hash, [new TableModel(
            "Things",
            columns,
            [new IndexDef($"IX_Things_{first}",
                $"CREATE INDEX IF NOT EXISTS IX_Things_{first} ON Things({first}, Id);")],
            "CREATE TABLE IF NOT EXISTS Things (\n" +
            string.Join(",\n", columns.Select(c => "    " + c.Definition)) +
            ",\n    PRIMARY KEY (Id)\n);")]);
    }

    private static SchemaModel V1 => Model("v1", Key(), Text("Name", notNull: true));

    private static SchemaModel NamedTable(
        string hash, string table, string? renamedFrom, params ColumnDef[] columns)
    {
        var first = columns.First(c => !c.IsKey).Name;

        return new(hash, [new TableModel(
            table,
            columns,
            [new IndexDef($"IX_{table}_{first}",
                $"CREATE INDEX IF NOT EXISTS IX_{table}_{first} ON {table}({first}, Id);")],
            "CREATE TABLE IF NOT EXISTS " + table + " (\n" +
            string.Join(",\n", columns.Select(c => "    " + c.Definition)) +
            ",\n    PRIMARY KEY (Id)\n);",
            renamedFrom)]);
    }

    private static SchemaModel ModelWithIndex(
        string hash, ColumnDef[] columns, string indexName, string indexSql) =>
        new(hash, [new TableModel(
            "Things",
            columns,
            [new IndexDef(indexName, indexSql)],
            "CREATE TABLE IF NOT EXISTS Things (\n" +
            string.Join(",\n", columns.Select(c => "    " + c.Definition)) +
            ",\n    PRIMARY KEY (Id)\n);")]);

    // --- First run --------------------------------------------------------------------------------

    [TestMethod]
    public async Task FirstRun_CreatesTheTableAndItsIndexes()
    {
        var changes = await SchemaApplier.ApplyAsync(Db, V1);

        CollectionAssert.AreEquivalent(
            new[] { SchemaChangeKind.AddTable, SchemaChangeKind.AddIndex },
            changes.Select(c => c.Kind).ToArray());
    }

    [TestMethod]
    public async Task SecondRun_DoesNothing()
    {
        await SchemaApplier.ApplyAsync(Db, V1);
        var changes = await SchemaApplier.ApplyAsync(Db, V1);

        Assert.AreEqual(0, changes.Count, "an unchanged schema should not be touched twice");
    }

    // --- The two changes people actually make ------------------------------------------------------

    /// <summary>A new table is the easy case, and the one worth being sure about.</summary>
    [TestMethod]
    public async Task NewTable_IsCreated()
    {
        await SchemaApplier.ApplyAsync(Db, V1);

        var withSecondTable = new SchemaModel("v2", [
            .. V1.Tables,
            new TableModel("Extras", [Key()], [],
                "CREATE TABLE IF NOT EXISTS Extras (\n    Id TEXT NOT NULL,\n    PRIMARY KEY (Id)\n);"),
        ]);

        var changes = await SchemaApplier.ApplyAsync(Db, withSecondTable);

        Assert.AreEqual(1, changes.Count);
        Assert.AreEqual(SchemaChangeKind.AddTable, changes[0].Kind);
        Assert.AreEqual("Extras", changes[0].Table);
    }

    [TestMethod]
    public async Task NewNullableColumn_IsAdded_AndKeepsExistingRows()
    {
        await SchemaApplier.ApplyAsync(Db, V1);
        await InsertAsync("row-1", "original");

        var changes = await SchemaApplier.ApplyAsync(Db, Model("v2", Key(), Text("Name", notNull: true), Text("Note")));

        Assert.AreEqual(1, changes.Count);
        Assert.AreEqual(SchemaChangeKind.AddColumn, changes[0].Kind);
        Assert.AreEqual("Note", changes[0].Column);

        // The point of ADD COLUMN over a rebuild: the row that was already there is untouched.
        Assert.AreEqual("original", await ScalarAsync("SELECT Name FROM Things WHERE Id = 'row-1';"));
        Assert.IsNull(await ScalarAsync("SELECT Note FROM Things WHERE Id = 'row-1';"));
    }

    [TestMethod]
    public async Task NewNotNullColumnWithADefault_IsAdded()
    {
        await SchemaApplier.ApplyAsync(Db, V1);
        await InsertAsync("row-1", "original");

        var changes = await SchemaApplier.ApplyAsync(
            Db, Model("v2", Key(), Text("Name", notNull: true), Text("Region", notNull: true, @default: "'US'")));

        Assert.AreEqual(SchemaChangeKind.AddColumn, changes.Single().Kind);
        Assert.AreEqual("US", await ScalarAsync("SELECT Region FROM Things WHERE Id = 'row-1';"));
    }

    /// <summary>SQLite refuses this outright, so the diff has to refuse it first and say why.</summary>
    [TestMethod]
    public async Task NewNotNullColumnWithoutADefault_IsRefused()
    {
        await SchemaApplier.ApplyAsync(Db, V1);

        var thrown = await Assert.ThrowsExactlyAsync<SqliteSchemaException>(async () =>
            await SchemaApplier.ApplyAsync(
                Db, Model("v2", Key(), Text("Name", notNull: true), Text("Region", notNull: true))));

        StringAssert.Contains(thrown.Message, "NOT NULL with no default");
    }

    // --- Renames ------------------------------------------------------------------------------------

    /// <summary>
    /// The ambiguity that makes [RenameFrom] necessary. Adding Title and leaving Name would put the
    /// default in every row while the real data sat in an orphaned column — a wrong answer, not an
    /// error, so it must be refused.
    /// </summary>
    [TestMethod]
    public async Task AddAndRemoveOnOneTable_IsRefusedAsAmbiguous()
    {
        await SchemaApplier.ApplyAsync(Db, V1);

        var thrown = await Assert.ThrowsExactlyAsync<SqliteSchemaException>(async () =>
            await SchemaApplier.ApplyAsync(Db, Model("v2", Key(), Text("Title", notNull: true))));

        StringAssert.Contains(thrown.Message, "indistinguishable from a rename");
        StringAssert.Contains(thrown.Message, "[RenameFrom(\"Name\")]");
    }

    [TestMethod]
    public async Task RenameFrom_RenamesInPlaceAndKeepsTheData()
    {
        await SchemaApplier.ApplyAsync(Db, V1);
        await InsertAsync("row-1", "keep me");

        var changes = await SchemaApplier.ApplyAsync(
            Db, Model("v2", Key(), Text("Title", notNull: true, renamedFrom: "Name")));

        Assert.AreEqual(SchemaChangeKind.Rename, changes.Single(c => c.Kind == SchemaChangeKind.Rename).Kind);
        Assert.AreEqual("keep me", await ScalarAsync("SELECT Title FROM Things WHERE Id = 'row-1';"));
    }

    /// <summary>
    /// [RenameFrom] is a statement about history, not an instruction to run once, so leaving it in
    /// place forever has to be harmless.
    /// </summary>
    [TestMethod]
    public async Task RenameFrom_IsIdempotent()
    {
        await SchemaApplier.ApplyAsync(Db, V1);

        var renamed = Model("v2", Key(), Text("Title", notNull: true, renamedFrom: "Name"));
        await SchemaApplier.ApplyAsync(Db, renamed);

        Assert.AreEqual(0, (await SchemaApplier.ApplyAsync(Db, renamed)).Count);
    }

    [TestMethod]
    public async Task RenameFrom_OnAFreshDatabase_JustCreatesTheTable()
    {
        var changes = await SchemaApplier.ApplyAsync(
            Db, Model("v2", Key(), Text("Title", notNull: true, renamedFrom: "Name")));

        Assert.IsFalse(changes.Any(c => c.Kind == SchemaChangeKind.Rename),
            "nothing to rename when the table did not exist");
        Assert.IsTrue(changes.Any(c => c.Kind == SchemaChangeKind.AddTable));
    }

    // --- What must still fail -------------------------------------------------------------------------

    [TestMethod]
    public async Task RetypedColumn_IsRefused()
    {
        await SchemaApplier.ApplyAsync(Db, V1);

        var retyped = new ColumnDef("Name", "INTEGER", true, null, false, false, false, "Name INTEGER NOT NULL");

        var thrown = await Assert.ThrowsExactlyAsync<SqliteSchemaException>(async () =>
            await SchemaApplier.ApplyAsync(Db, Model("v2", Key(), retyped)));

        StringAssert.Contains(thrown.Message, "cannot retype a column in place");
    }

    [TestMethod]
    public async Task ChangedNullability_IsRefused()
    {
        await SchemaApplier.ApplyAsync(Db, V1);

        var thrown = await Assert.ThrowsExactlyAsync<SqliteSchemaException>(async () =>
            await SchemaApplier.ApplyAsync(Db, Model("v2", Key(), Text("Name"))));

        StringAssert.Contains(thrown.Message, "nullable in the row type");
    }

    /// <summary>
    /// A column left in the database is harmless: every generated statement names its columns, so
    /// nothing reads it. Reported rather than ignored, so the drift is visible.
    /// </summary>
    [TestMethod]
    public async Task ColumnRemovedFromTheRowType_IsReportedButNotFatal()
    {
        await SchemaApplier.ApplyAsync(Db, Model("v1", Key(), Text("Name", notNull: true), Text("Note")));

        var changes = await SchemaApplier.ApplyAsync(Db, Model("v2", Key(), Text("Name", notNull: true)));

        Assert.AreEqual(SchemaChangeKind.Extra, changes.Single().Kind);
        Assert.AreEqual("Note", changes.Single().Column);
    }

    /// <summary>
    /// SQLite's RENAME COLUMN rewrites an index's definition to use the new name but keeps the index's
    /// old NAME. Without cleaning that up the stale index survives and a duplicate is built beside it
    /// — two indexes doing one job, paid for on every write.
    /// </summary>
    [TestMethod]
    public async Task RenamingAColumn_DoesNotLeaveADuplicateIndexBehind()
    {
        await SchemaApplier.ApplyAsync(Db, V1);
        await SchemaApplier.ApplyAsync(Db, Model("v2", Key(), Text("Title", notNull: true, renamedFrom: "Name")));

        // sqlite_autoindex_* is excluded: SQLite builds one implicitly for a PRIMARY KEY that is not
        // an INTEGER rowid alias, so a TEXT key always has one and it is none of our business.
        var indexes = await ScalarAsync(
            "SELECT group_concat(name) FROM sqlite_master " +
            "WHERE type = 'index' AND tbl_name = 'Things' AND name NOT LIKE 'sqlite_autoindex%';");

        Assert.AreEqual("IX_Things_Title", indexes,
            "the index should follow the rename, not be duplicated beside a stale one");
    }

    /// <summary>An index somebody wrote by hand is not ours to remove.</summary>
    [TestMethod]
    public async Task AHandWrittenIndex_IsLeftAlone()
    {
        await SchemaApplier.ApplyAsync(Db, V1);

        await using (var session = await Db.BeginAsync())
        {
            await using var command = session.Connection.CreateCommand();
            command.Transaction = session.Transaction;
            command.CommandText = "CREATE INDEX my_own_index ON Things(Name);";
            await command.ExecuteNonQueryAsync();
            await session.CommitAsync();
        }

        await SchemaApplier.ApplyAsync(Db, Model("v2", Key(), Text("Name", notNull: true), Text("Note")));

        Assert.AreEqual(1L, Convert.ToInt64(await ScalarAsync(
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'my_own_index';")));
    }

    // --- Uniqueness -----------------------------------------------------------------------------------

    /// <summary>
    /// PRAGMA table_info says nothing about uniqueness, so without an explicit comparison a [Unique]
    /// added later would be quietly ignored: the hash would advance, the constraint would not be
    /// enforced, and the schema would claim otherwise.
    /// </summary>
    [TestMethod]
    public async Task UniqueAddedLater_IsEnforcedByAUniqueIndex()
    {
        await SchemaApplier.ApplyAsync(Db, V1);
        await InsertAsync("row-1", "taken");

        var changes = await SchemaApplier.ApplyAsync(
            Db, Model("v2", Key(), Text("Name", notNull: true, unique: true)));

        Assert.IsTrue(changes.Any(c => c.Kind == SchemaChangeKind.AddIndex && c.Column == "Name"));

        // The constraint has to actually bite, not merely exist in sqlite_master.
        var duplicate = await Assert.ThrowsExactlyAsync<SqliteException>(async () =>
            await InsertAsync("row-2", "taken"));

        StringAssert.Contains(duplicate.Message, "UNIQUE constraint failed");
    }

    [TestMethod]
    public async Task UniqueIndex_SurvivesAnUnrelatedNullableColumn()
    {
        await SchemaApplier.ApplyAsync(Db, V1);
        await InsertAsync("row-1", "taken");
        await SchemaApplier.ApplyAsync(Db, Model("v2", Key(), Text("Name", notNull: true, unique: true)));

        var changes = await SchemaApplier.ApplyAsync(
            Db, Model("v3", Key(), Text("Name", notNull: true, unique: true), Text("Note")));

        Assert.IsTrue(changes.Any(c => c.Kind == SchemaChangeKind.AddColumn && c.Column == "Note"));
        Assert.IsFalse(changes.Any(c => c.Sql is not null && c.Sql.Contains("DROP INDEX", StringComparison.Ordinal)
            && c.Sql.Contains("UX_Things_Name", StringComparison.Ordinal)));

        Assert.AreEqual(1L, Convert.ToInt64(await ScalarAsync(
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'UX_Things_Name';")));

        var duplicate = await Assert.ThrowsExactlyAsync<SqliteException>(async () =>
            await InsertAsync("row-2", "taken"));
        StringAssert.Contains(duplicate.Message, "UNIQUE constraint failed");
    }

    /// <summary>Adding it over existing duplicates must fail rather than half-apply.</summary>
    [TestMethod]
    public async Task UniqueAddedOverExistingDuplicates_Fails()
    {
        await SchemaApplier.ApplyAsync(Db, V1);
        await InsertAsync("row-1", "same");
        await InsertAsync("row-2", "same");

        await Assert.ThrowsExactlyAsync<SqliteException>(async () =>
            await SchemaApplier.ApplyAsync(Db, Model("v2", Key(), Text("Name", notNull: true, unique: true))));
    }

    /// <summary>A UNIQUE in the table definition cannot be taken back without a rebuild.</summary>
    [TestMethod]
    public async Task UniqueRemovedFromTheRowType_IsRefused()
    {
        await SchemaApplier.ApplyAsync(Db, Model("v1", Key(), Text("Name", notNull: true, unique: true)));

        var thrown = await Assert.ThrowsExactlyAsync<SqliteSchemaException>(async () =>
            await SchemaApplier.ApplyAsync(Db, Model("v2", Key(), Text("Name", notNull: true))));

        StringAssert.Contains(thrown.Message, "cannot be dropped in place");
    }

    // --- Rebuild ------------------------------------------------------------------------------------

    private static SchemaModel Rebuildable(string hash, params ColumnDef[] columns)
    {
        var model = Model(hash, columns);
        return model with { Tables = [model.Tables[0] with { AllowRebuild = true }] };
    }

    /// <summary>
    /// Two keys, not one. The attribute alone does nothing without SchemaUpdate.Rebuild, so a
    /// destructive operation is not reachable by editing a single file.
    /// </summary>
    [TestMethod]
    public async Task RebuildAttributeAlone_DoesNotRebuild()
    {
        await SchemaApplier.ApplyAsync(Db, V1);

        var retyped = new ColumnDef("Name", "INTEGER", true, null, false, false, false, "Name INTEGER NOT NULL");

        await Assert.ThrowsExactlyAsync<SqliteSchemaException>(async () =>
            await SchemaApplier.ApplyAsync(Db, Rebuildable("v2", Key(), retyped)));
    }

    [TestMethod]
    public async Task RebuildModeAlone_DoesNotRebuild()
    {
        await SchemaApplier.ApplyAsync(Db, V1);

        var retyped = new ColumnDef("Name", "INTEGER", true, null, false, false, false, "Name INTEGER NOT NULL");

        await Assert.ThrowsExactlyAsync<SqliteSchemaException>(async () =>
            await SchemaApplier.ApplyAsync(Db, Model("v2", Key(), retyped), SchemaUpdate.Rebuild));
    }

    /// <summary>A retype is the case SQLite cannot do any other way.</summary>
    [TestMethod]
    public async Task Rebuild_RetypesAColumnAndKeepsTheRows()
    {
        await SchemaApplier.ApplyAsync(Db, V1);
        await InsertAsync("row-1", "42");

        var retyped = new ColumnDef("Name", "INTEGER", true, null, false, false, false, "Name INTEGER NOT NULL");
        var changes = await SchemaApplier.ApplyAsync(Db, Rebuildable("v2", Key(), retyped), SchemaUpdate.Rebuild);

        Assert.AreEqual(SchemaChangeKind.Rebuild, changes.Single().Kind);
        Assert.AreEqual("INTEGER", await ScalarAsync(
            "SELECT type FROM pragma_table_info('Things') WHERE name = 'Name';"));
        Assert.AreEqual(42L, Convert.ToInt64(await ScalarAsync("SELECT Name FROM Things WHERE Id = 'row-1';")));
    }

    [TestMethod]
    public async Task Rebuild_TightensNullability()
    {
        await SchemaApplier.ApplyAsync(Db, Model("v1", Key(), Text("Name")));
        await InsertAsync("row-1", "present");

        var changes = await SchemaApplier.ApplyAsync(
            Db, Rebuildable("v2", Key(), Text("Name", notNull: true)), SchemaUpdate.Rebuild);

        Assert.AreEqual(SchemaChangeKind.Rebuild, changes.Single().Kind);
        Assert.AreEqual(1L, Convert.ToInt64(await ScalarAsync(
            "SELECT \"notnull\" FROM pragma_table_info('Things') WHERE name = 'Name';")));
        Assert.AreEqual("present", await ScalarAsync("SELECT Name FROM Things WHERE Id = 'row-1';"));
    }

    /// <summary>
    /// A rebuild carries across only the columns that still exist. That IS the data loss the
    /// attribute is an acknowledgement of, so it needs to be pinned down rather than assumed.
    /// </summary>
    [TestMethod]
    public async Task Rebuild_DropsColumnsTheRowTypeNoLongerHas()
    {
        await SchemaApplier.ApplyAsync(Db, Model("v1", Key(), Text("Name", notNull: true), Text("Note")));

        await using (var session = await Db.BeginAsync())
        {
            await using var command = session.Connection.CreateCommand();
            command.Transaction = session.Transaction;
            command.CommandText = "INSERT INTO Things (Id, Name, Note) VALUES ('row-1', 'kept', 'discarded');";
            await command.ExecuteNonQueryAsync();
            await session.CommitAsync();
        }

        var retyped = new ColumnDef("Name", "INTEGER", true, null, false, false, false, "Name INTEGER NOT NULL");
        await SchemaApplier.ApplyAsync(Db, Rebuildable("v2", Key(), retyped), SchemaUpdate.Rebuild);

        Assert.AreEqual(0L, Convert.ToInt64(await ScalarAsync(
            "SELECT COUNT(*) FROM pragma_table_info('Things') WHERE name = 'Note';")));
    }

    /// <summary>[RenameFrom] still preserves the values when the table is rebuilt rather than altered.</summary>
    [TestMethod]
    public async Task Rebuild_HonoursRenameFrom()
    {
        await SchemaApplier.ApplyAsync(Db, V1);
        await InsertAsync("row-1", "carried across");

        var renamedAndRetyped = new ColumnDef(
            "Title", "TEXT", NotNull: true, Default: null, IsKey: false, Unique: true,
            HasForeignKey: false, Definition: "Title TEXT NOT NULL UNIQUE", RenamedFrom: "Name");

        await SchemaApplier.ApplyAsync(Db, Rebuildable("v2", Key(), renamedAndRetyped), SchemaUpdate.Rebuild);

        Assert.AreEqual("carried across", await ScalarAsync("SELECT Title FROM Things WHERE Id = 'row-1';"));
    }

    /// <summary>The rebuilt table gets its indexes back; they went with the table it replaced.</summary>
    [TestMethod]
    public async Task Rebuild_RecreatesIndexes()
    {
        await SchemaApplier.ApplyAsync(Db, V1);

        var retyped = new ColumnDef("Name", "INTEGER", true, null, false, false, false, "Name INTEGER NOT NULL");
        await SchemaApplier.ApplyAsync(Db, Rebuildable("v2", Key(), retyped), SchemaUpdate.Rebuild);

        Assert.AreEqual("IX_Things_Name", await ScalarAsync(
            "SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name = 'Things' " +
            "AND name NOT LIKE 'sqlite_autoindex%';"));
    }

    [TestMethod]
    public async Task Rebuild_LeavesTheSchemaSettledAfterwards()
    {
        await SchemaApplier.ApplyAsync(Db, V1);

        var retyped = new ColumnDef("Name", "INTEGER", true, null, false, false, false, "Name INTEGER NOT NULL");
        var v2 = Rebuildable("v2", Key(), retyped);

        await SchemaApplier.ApplyAsync(Db, v2, SchemaUpdate.Rebuild);

        Assert.AreEqual(0, (await SchemaApplier.DiffAsync(Db, v2)).Count,
            "a rebuilt table should match the model exactly, or it would rebuild on every start");
    }

    // --- Update modes -----------------------------------------------------------------------------

    [TestMethod]
    public async Task CreateOnly_RefusesToAddAColumn()
    {
        await SchemaApplier.ApplyAsync(Db, V1);

        await Assert.ThrowsExactlyAsync<SqliteSchemaException>(async () =>
            await SchemaApplier.ApplyAsync(
                Db, Model("v2", Key(), Text("Name", notNull: true), Text("Note")), SchemaUpdate.CreateOnly));
    }

    [TestMethod]
    public async Task VerifyOnly_RefusesEvenANewTable()
    {
        await Assert.ThrowsExactlyAsync<SqliteSchemaException>(async () =>
            await SchemaApplier.ApplyAsync(Db, V1, SchemaUpdate.VerifyOnly));
    }

    [TestMethod]
    public async Task VerifyOnly_RefusesAnExtraColumn()
    {
        await SchemaApplier.ApplyAsync(Db, Model("v1", Key(), Text("Name", notNull: true), Text("Note")));
        var hash = await ScalarAsync($"SELECT Hash FROM {SchemaApplier.MetadataTable} LIMIT 1;");

        var thrown = await Assert.ThrowsExactlyAsync<SqliteSchemaException>(async () =>
            await SchemaApplier.ApplyAsync(
                Db, Model("v2", Key(), Text("Name", notNull: true)), SchemaUpdate.VerifyOnly));

        Assert.IsTrue(thrown.Changes.Any(c => c.Kind == SchemaChangeKind.Extra && c.Column == "Note"));
        Assert.AreEqual(hash, await ScalarAsync($"SELECT Hash FROM {SchemaApplier.MetadataTable} LIMIT 1;"));
        Assert.AreEqual(1L, Convert.ToInt64(await ScalarAsync(
            "SELECT COUNT(*) FROM pragma_table_info('Things') WHERE name = 'Note';")));
    }

    [TestMethod]
    public async Task Diff_ReportsWithoutChangingAnything()
    {
        var changes = await SchemaApplier.DiffAsync(Db, V1);

        Assert.IsTrue(changes.Any(c => c.Kind == SchemaChangeKind.AddTable));
        Assert.AreEqual(0L, Convert.ToInt64(
            await ScalarAsync("SELECT COUNT(*) FROM sqlite_master WHERE name = 'Things';")));
    }

    /// <summary>
    /// The hash is the next start's success signal. Recording it before a rebuild that then fails
    /// would make the next start skip the comparison while the table is still the old shape.
    /// </summary>
    [TestMethod]
    public async Task Rebuild_ThatFails_DoesNotRecordTheNewHash()
    {
        await SchemaApplier.ApplyAsync(Db, Model("v1", Key(), Text("Name")));

        await using (var session = await Db.BeginAsync())
        {
            await using var command = session.Connection.CreateCommand();
            command.Transaction = session.Transaction;
            command.CommandText = "INSERT INTO Things (Id, Name) VALUES ('row-1', NULL);";
            await command.ExecuteNonQueryAsync();
            await session.CommitAsync();
        }

        var v2 = Rebuildable("v2", Key(), Text("Name", notNull: true));

        await Assert.ThrowsExactlyAsync<SqliteException>(async () =>
            await SchemaApplier.ApplyAsync(Db, v2, SchemaUpdate.Rebuild));

        Assert.AreEqual("v1", await ScalarAsync(
            $"SELECT Hash FROM {SchemaApplier.MetadataTable} LIMIT 1;"));
        Assert.IsNull(await ScalarAsync("SELECT Name FROM Things WHERE Id = 'row-1';"));
        Assert.AreEqual(0L, Convert.ToInt64(await ScalarAsync(
            "SELECT \"notnull\" FROM pragma_table_info('Things') WHERE name = 'Name';")));
    }

    /// <summary>
    /// Table-level [RenameFrom] has to diff columns against the name that is actually in the
    /// database. Comparing the new name before the rename runs makes every column look missing.
    /// </summary>
    [TestMethod]
    public async Task TableRenameFrom_RenamesInPlaceAndKeepsTheData()
    {
        await SchemaApplier.ApplyAsync(Db, NamedTable("v1", "Things", renamedFrom: null, Key(), Text("Name", notNull: true)));
        await InsertAsync("row-1", "keep me");

        var changes = await SchemaApplier.ApplyAsync(
            Db, NamedTable("v2", "Items", renamedFrom: "Things", Key(), Text("Name", notNull: true)));

        Assert.AreEqual(SchemaChangeKind.Rename, changes.Single(c => c.Kind == SchemaChangeKind.Rename).Kind);
        Assert.AreEqual("keep me", await ScalarAsync("SELECT Name FROM Items WHERE Id = 'row-1';"));

        var indexes = await ScalarAsync(
            "SELECT group_concat(name) FROM sqlite_master " +
            "WHERE type = 'index' AND tbl_name = 'Items' AND name NOT LIKE 'sqlite_autoindex%';");

        Assert.AreEqual("IX_Items_Name", indexes,
            "the index should follow the table rename, not sit beside a stale IX_Things_*");
        Assert.AreEqual(0L, Convert.ToInt64(await ScalarAsync(
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name LIKE 'IX_Things_%';")));
    }

    /// <summary>
    /// Index names do not include WHERE, so a predicate edit keeps the same name. Compared by SQL
    /// rather than by name, otherwise CREATE INDEX IF NOT EXISTS is a silent no-op and the original
    /// predicate stays forever.
    /// </summary>
    [TestMethod]
    public async Task ChangingAnIndexPredicate_DropsAndRecreatesIt()
    {
        var columns = new[] { Key(), Text("Name", notNull: true) };

        await SchemaApplier.ApplyAsync(Db, ModelWithIndex(
            "v1", columns,
            "IX_Things_Name",
            "CREATE INDEX IF NOT EXISTS IX_Things_Name ON Things(Name, Id) WHERE Name IS NOT NULL;"));

        var changes = await SchemaApplier.ApplyAsync(Db, ModelWithIndex(
            "v2", columns,
            "IX_Things_Name",
            "CREATE INDEX IF NOT EXISTS IX_Things_Name ON Things(Name, Id) WHERE Name IS NULL;"));

        Assert.IsTrue(changes.Any(c => c.Sql is not null && c.Sql.StartsWith("DROP INDEX", StringComparison.Ordinal)));
        Assert.IsTrue(changes.Any(c => c.Kind == SchemaChangeKind.AddIndex));

        var sql = (string)(await ScalarAsync(
            "SELECT sql FROM sqlite_master WHERE type = 'index' AND name = 'IX_Things_Name';"))!;

        StringAssert.Contains(sql, "Name IS NULL");
        Assert.IsFalse(sql.Contains("NOT NULL", StringComparison.Ordinal),
            "the original predicate must not survive a WHERE-clause edit");

        Assert.AreEqual(0, (await SchemaApplier.DiffAsync(Db, ModelWithIndex(
            "v2", columns,
            "IX_Things_Name",
            "CREATE INDEX IF NOT EXISTS IX_Things_Name ON Things(Name, Id) WHERE Name IS NULL;"))).Count,
            "the recreated index must compare equal to the model, or it would drop-and-create on every start");
    }

    [TestMethod]
    public async Task KeywordTableAndColumn_Apply()
    {
        var columns = new[]
        {
            Key(),
            new ColumnDef("Group", "TEXT", true, null, false, false, false, "\"Group\" TEXT NOT NULL"),
        };

        var model = new SchemaModel("v1", [new TableModel(
            "Order",
            columns,
            [new IndexDef("IX_Order_Group",
                "CREATE INDEX IF NOT EXISTS \"IX_Order_Group\" ON \"Order\"(\"Group\", \"Id\");")],
            "CREATE TABLE IF NOT EXISTS \"Order\" (\n" +
            "    \"Id\" TEXT NOT NULL,\n    \"Group\" TEXT NOT NULL,\n    PRIMARY KEY (\"Id\")\n);")]);

        var changes = await SchemaApplier.ApplyAsync(Db, model);

        Assert.IsTrue(changes.Any(c => c.Kind == SchemaChangeKind.AddTable));
        Assert.AreEqual(0, (await SchemaApplier.DiffAsync(Db, model)).Count);
        Assert.AreEqual(1L, Convert.ToInt64(await ScalarAsync(
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'Order';")));
    }

    [TestMethod]
    public async Task DecimalScaleMismatch_IsIncompatible()
    {
        static ColumnDef Price(int scale) =>
            new("Price", "INTEGER", true, null, false, false, false, "Price INTEGER NOT NULL", Scale: scale);

        static SchemaModel Priced(string hash, int scale) =>
            new(hash, [new TableModel(
                "Things",
                [Key(), Price(scale)],
                [],
                "CREATE TABLE IF NOT EXISTS Things (\n    Id TEXT NOT NULL,\n    Price INTEGER NOT NULL,\n    PRIMARY KEY (Id)\n);")]);

        await SchemaApplier.ApplyAsync(Db, Priced("v1", 4));

        var diff = await SchemaApplier.DiffAsync(Db, Priced("v2", 2));

        Assert.IsTrue(diff.Any(c =>
            c.Kind == SchemaChangeKind.Incompatible && c.Column == "Price" && c.Detail.Contains("scale", StringComparison.OrdinalIgnoreCase)));
    }

    // --- Helpers ----------------------------------------------------------------------------------

    private async Task InsertAsync(string id, string name)
    {
        await using var session = await Db.BeginAsync();
        await using var command = session.Connection.CreateCommand();
        command.Transaction = session.Transaction;
        command.CommandText = "INSERT INTO Things (Id, Name) VALUES ($id, $name);";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$name", name);
        await command.ExecuteNonQueryAsync();
        await session.CommitAsync();
    }

    private async Task<object?> ScalarAsync(string sql)
    {
        await using var session = await Db.OpenAsync();
        await using var command = session.Connection.CreateCommand();
        command.CommandText = sql;

        var value = await command.ExecuteScalarAsync();
        return value is DBNull ? null : value;
    }
}
