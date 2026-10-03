using Microsoft.Data.Sqlite;

namespace Schipper.Io.Sqlite.Schema;

/// <summary>
/// Compares the generated schema against the database and reconciles what can be reconciled.
///
/// The naive version of this is <c>CREATE TABLE IF NOT EXISTS</c> and hope. That handles the first
/// run and silently does nothing on every run after — so a row type that gained a column produces a
/// database missing it, and the failure surfaces far from the cause.
///
/// The version here reads what is actually in the database and diffs it, which means the two changes
/// people actually make — a new table, a new column — apply by themselves, and anything else fails
/// naming exactly what differs.
///
/// It is deliberately <b>not</b> a migration system. SQLite cannot retype a column, change its
/// nullability, or alter a key without rebuilding the table, and a package that attempted that
/// silently would be far more dangerous than one that stops and tells you.
/// </summary>
public static class SchemaApplier
{
    /// <summary>Where the applied schema hash is recorded.</summary>
    public const string MetadataTable = "__SchipperSchema";

    /// <summary>
    /// Brings the database up to <paramref name="model"/> as far as <paramref name="update"/> allows,
    /// and returns everything that was done or found.
    ///
    /// Returning the changes rather than logging them is deliberate: this library has no opinion
    /// about your logger, and a caller that silently discards the fact that it just altered a table
    /// has made that choice explicitly.
    /// </summary>
    /// <exception cref="SqliteSchemaException">A difference that cannot be applied safely.</exception>
    public static async Task<IReadOnlyList<SchemaChange>> ApplyAsync(
        SqliteDb db,
        SchemaModel model,
        SchemaUpdate update = SchemaUpdate.Additive,
        CancellationToken cancellationToken = default)
    {
        // Fast path. An unchanged schema is the common case: peek the hash on a read session so a
        // matching start does not take a write lock or CREATE TABLE IF NOT EXISTS.
        await using (var peek = await db.OpenAsync(cancellationToken))
        {
            if (await ReadHashAsync(peek, cancellationToken) == model.Hash)
            {
                return [];
            }
        }

        await using var session = await db.BeginAsync(cancellationToken);

        await ExecuteAsync(session,
            $"CREATE TABLE IF NOT EXISTS {SqlIdent.Quote(MetadataTable)} " +
            "(Hash TEXT NOT NULL, AppliedAt INTEGER NOT NULL, Scales TEXT);",
            cancellationToken);
        await EnsureScalesColumnAsync(session, cancellationToken);

        var changes = await CompareAsync(session, model, cancellationToken);

        // A table that is going to be rebuilt has all its other changes folded into that one
        // operation: the rebuild creates the final shape, including indexes, so adding a column to a
        // table that is about to be replaced would be work done twice and thrown away.
        var rebuilding = update == SchemaUpdate.Rebuild
            ? model.Tables
                .Where(t => t.AllowRebuild)
                .Where(t => changes.Any(c => c.Kind == SchemaChangeKind.Incompatible && c.Table == t.Name))
                .ToList()
            : [];

        if (rebuilding.Count > 0)
        {
            changes = changes
                .Where(c => rebuilding.All(t => t.Name != c.Table))
                .Concat(rebuilding.Select(t => new SchemaChange(
                    SchemaChangeKind.Rebuild, t.Name, null,
                    "rebuilt: a change to this table cannot be applied in place")))
                .ToList();
        }

        if (update == SchemaUpdate.VerifyOnly)
        {
            if (changes.Count > 0)
            {
                throw new SqliteSchemaException(changes);
            }

            await RecordAsync(session, model, cancellationToken);
            await session.CommitAsync(cancellationToken);
            return changes;
        }

        var blocked = changes
            .Where(c => c.Kind == SchemaChangeKind.Incompatible ||
                        (update == SchemaUpdate.CreateOnly && c.Kind == SchemaChangeKind.AddColumn))
            .ToList();

        if (blocked.Count > 0)
        {
            throw new SqliteSchemaException(blocked);
        }

        foreach (var change in changes.Where(c => c.Sql is not null))
        {
            await ExecuteAsync(session, change.Sql!, cancellationToken);
        }

        // The hash is the next start's success signal. Recording it before a rebuild would make a
        // failed foreign_key_check — or a crash mid-copy — look like a settled schema while the
        // table is still the old shape, which is the plausible-wrong-answer class this library
        // exists to prevent.
        if (rebuilding.Count == 0)
        {
            await RecordAsync(session, model, cancellationToken);
        }

        await session.CommitAsync(cancellationToken);

        // Rebuilds run last and on their own connection. PRAGMA foreign_keys is a silent no-op
        // inside a transaction, so the twelve-step procedure cannot be carried out on the session
        // above — a rebuild written entirely inside BEGIN...COMMIT looks right and is not.
        foreach (var table in rebuilding)
        {
            await RebuildAsync(db, table, cancellationToken);
        }

        if (rebuilding.Count > 0)
        {
            await using var recorded = await db.BeginAsync(cancellationToken);
            await RecordAsync(recorded, model, cancellationToken);
            await recorded.CommitAsync(cancellationToken);
        }

        return changes;
    }

    /// <summary>
    /// The twelve-step rebuild from the SQLite documentation, which is the only way to retype a
    /// column, change its nullability, or alter a key.
    ///
    /// Steps 1 and 12 — turning foreign keys off and on — are deliberately outside the transaction,
    /// because <c>PRAGMA foreign_keys</c> does nothing inside one. Step 1 is not ceremony: with
    /// foreign keys enabled, the <c>DROP TABLE</c> at step 6 performs an implicit <c>DELETE FROM</c>
    /// that cascades, so rows in *other* tables would go. Step 10's <c>foreign_key_check</c> is what
    /// catches anything the disabled constraints let through, and it runs before the commit so a
    /// violation rolls the whole thing back.
    /// </summary>
    private static async Task RebuildAsync(SqliteDb db, TableModel table, CancellationToken cancellationToken)
    {
        var temporary = table.Name + "__schipper_rebuild";
        var quotedTable = SqlIdent.Quote(table.Name);
        var quotedTemporary = SqlIdent.Quote(temporary);

        // Opened without a transaction so the pragmas take effect.
        await using var session = await db.OpenAsync(cancellationToken);

        var actual = await ReadColumnsAsync(session, table.Name, cancellationToken);

        // Only columns present on both sides are carried across, with [RenameFrom] mapping the ones
        // whose name changed. Everything else is what "rebuild can lose data" means.
        var target = new List<string>();
        var source = new List<string>();

        foreach (var column in table.Columns)
        {
            if (actual.ContainsKey(column.Name))
            {
                target.Add(column.Name);
                source.Add(column.Name);
            }
            else if (column.RenamedFrom is { } previous && actual.ContainsKey(previous))
            {
                target.Add(column.Name);
                source.Add(previous);
            }
        }

        await ExecuteAsync(session, "PRAGMA foreign_keys=OFF;", cancellationToken);
        await ExecuteAsync(session, "BEGIN;", cancellationToken);

        try
        {
            await ExecuteAsync(session, CreateAs(table, temporary), cancellationToken);

            if (target.Count > 0)
            {
                await ExecuteAsync(session,
                    $"INSERT INTO {quotedTemporary} ({string.Join(", ", target.Select(SqlIdent.Quote))}) " +
                    $"SELECT {string.Join(", ", source.Select(SqlIdent.Quote))} FROM {quotedTable};",
                    cancellationToken);
            }

            await ExecuteAsync(session, $"DROP TABLE {quotedTable};", cancellationToken);
            await ExecuteAsync(session, $"ALTER TABLE {quotedTemporary} RENAME TO {quotedTable};", cancellationToken);

            // The old indexes went with the old table, so these are recreated rather than kept.
            foreach (var index in table.Indexes)
            {
                await ExecuteAsync(session, index.CreateSql, cancellationToken);
            }

            await CheckForeignKeysAsync(session, table.Name, cancellationToken);
            await ExecuteAsync(session, "COMMIT;", cancellationToken);
        }
        catch
        {
            try
            {
                await ExecuteAsync(session, "ROLLBACK;", cancellationToken);
            }
            catch (SqliteException)
            {
                // Nothing to roll back — the failure happened before the transaction opened.
            }

            throw;
        }
        finally
        {
            await ExecuteAsync(session, "PRAGMA foreign_keys=ON;", cancellationToken);
        }
    }

    /// <summary>
    /// The table's CREATE statement under a different name. Everything from the opening parenthesis
    /// is reused verbatim, so the rebuilt table is defined by the same generated DDL as a fresh one —
    /// a second definition here would be a second thing to keep in step.
    /// </summary>
    private static string CreateAs(TableModel table, string name) =>
        "CREATE TABLE " + SqlIdent.Quote(name) + " " + table.CreateSql[table.CreateSql.IndexOf('(')..];

    private static async Task CheckForeignKeysAsync(
        SqliteSession session, string table, CancellationToken cancellationToken)
    {
        await using var command = session.Connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_key_check;";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (await reader.ReadAsync(cancellationToken))
        {
            throw new SqliteSchemaException([new SchemaChange(
                SchemaChangeKind.Incompatible, table, null,
                $"rebuilding left a foreign key violation in {reader.GetString(0)}. The rebuild was " +
                "rolled back and the table is unchanged.")]);
        }
    }

    /// <summary>
    /// Reports the differences without changing anything. The same comparison
    /// <see cref="ApplyAsync"/> performs, for a caller that wants to decide for itself.
    /// </summary>
    public static async Task<IReadOnlyList<SchemaChange>> DiffAsync(
        SqliteDb db,
        SchemaModel model,
        CancellationToken cancellationToken = default)
    {
        await using var session = await db.OpenAsync(cancellationToken);
        return await CompareAsync(session, model, cancellationToken);
    }

    private static async Task<List<SchemaChange>> CompareAsync(
        SqliteSession session, SchemaModel model, CancellationToken cancellationToken)
    {
        var changes = new List<SchemaChange>();
        var existingTables = await ReadTableNamesAsync(session, cancellationToken);
        var existingIndexes = await ReadIndexesAsync(session, cancellationToken);
        var recordedScales = await ReadScalesAsync(session, cancellationToken);

        foreach (var table in model.Tables)
        {
            var present = existingTables.Contains(table.Name);
            var physicalName = table.Name;

            // A table rename has to run before anything else touches the table, which is why it is
            // emitted first and why statements are applied in order.
            if (!present && table.RenamedFrom is { } previous && existingTables.Contains(previous))
            {
                changes.Add(new SchemaChange(
                    SchemaChangeKind.Rename, table.Name, null,
                    $"renamed from {previous}, preserving its rows",
                    $"ALTER TABLE {SqlIdent.Quote(previous)} RENAME TO {SqlIdent.Quote(table.Name)};"));

                present = true;
                physicalName = previous;

                // The rename has not been applied yet, so PRAGMA and sqlite_master still speak the
                // old name. Re-point the in-memory snapshot so column and index diffs read the
                // table that is actually there, and so IX_{old}_* is recognised as ours to drop.
                foreach (var name in existingIndexes.Keys.ToList())
                {
                    if (string.Equals(existingIndexes[name].Table, previous, StringComparison.OrdinalIgnoreCase))
                    {
                        existingIndexes[name] = existingIndexes[name] with { Table = table.Name };
                    }
                }
            }

            if (!present)
            {
                changes.Add(new SchemaChange(
                    SchemaChangeKind.AddTable, table.Name, null,
                    "table does not exist yet", table.CreateSql));

                foreach (var index in table.Indexes)
                {
                    changes.Add(new SchemaChange(
                        SchemaChangeKind.AddIndex, table.Name, null,
                        $"index {index.Name} for a new table", index.CreateSql));
                }

                continue;
            }

            changes.AddRange(await CompareColumnsAsync(session, table, physicalName, recordedScales, cancellationToken));

            foreach (var index in table.Indexes)
            {
                if (!existingIndexes.TryGetValue(index.Name, out var existing))
                {
                    changes.Add(new SchemaChange(
                        SchemaChangeKind.AddIndex, table.Name, null,
                        $"index {index.Name} is missing", index.CreateSql));
                    continue;
                }

                // Compared by SQL, not just name. The generator names indexes from the column list
                // and does not include WHERE, so a predicate edit keeps the same name. CREATE INDEX
                // IF NOT EXISTS with a different WHERE is a silent no-op — the original predicate
                // stays, the hash advances, and every later start takes the fast path.
                if (existing.Sql is not null && !SqlIdent.IndexSqlEquals(existing.Sql, index.CreateSql))
                {
                    changes.Add(new SchemaChange(
                        SchemaChangeKind.Extra, table.Name, null,
                        $"index {index.Name} no longer matches its declaration; dropping it to recreate",
                        $"DROP INDEX IF EXISTS {SqlIdent.Quote(index.Name)};"));
                    changes.Add(new SchemaChange(
                        SchemaChangeKind.AddIndex, table.Name, null,
                        $"index {index.Name} is being recreated", index.CreateSql));
                }
            }

            // Indexes this library created that the model no longer describes. RENAME COLUMN is the
            // usual cause: SQLite rewrites the index definition to use the new column name but keeps
            // the old index NAME, so without this the stale index survives and a duplicate is created
            // beside it — two indexes doing one job, paid for on every write.
            //
            // Scoped to the IX_/UX_ names the generator owns. A hand-made index is left alone, and
            // sqlite_autoindex_* (created for UNIQUE constraints) never matches.
            var ownedPrefixes = new List<string>
            {
                $"IX_{table.Name}_",
                $"UX_{table.Name}_",
            };

            if (!string.Equals(physicalName, table.Name, StringComparison.OrdinalIgnoreCase))
            {
                ownedPrefixes.Add($"IX_{physicalName}_");
                ownedPrefixes.Add($"UX_{physicalName}_");
            }

            foreach (var stale in existingIndexes
                .Where(i => string.Equals(i.Value.Table, table.Name, StringComparison.OrdinalIgnoreCase))
                .Select(i => i.Key)
                .Where(name => ownedPrefixes.Any(prefix =>
                    name.StartsWith(prefix, StringComparison.Ordinal)))
                .Where(name => table.Indexes.All(i => !string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase)))
                .Where(name => !IsDeclaredColumnUniqueIndex(table, name)))
            {
                changes.Add(new SchemaChange(
                    SchemaChangeKind.Extra, table.Name, null,
                    $"index {stale} is no longer declared and was created by this library; dropping it",
                    $"DROP INDEX IF EXISTS {SqlIdent.Quote(stale)};"));
            }
        }

        return changes;
    }

    private static bool IsDeclaredColumnUniqueIndex(TableModel table, string indexName)
    {
        foreach (var column in table.Columns)
        {
            if (column.Unique && !column.IsKey &&
                string.Equals($"UX_{table.Name}_{column.Name}", indexName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<List<SchemaChange>> CompareColumnsAsync(
        SqliteSession session, TableModel table, string physicalName,
        IReadOnlyDictionary<string, int> recordedScales, CancellationToken cancellationToken)
    {
        var changes = new List<SchemaChange>();
        var actual = await ReadColumnsAsync(session, physicalName, cancellationToken);

        var missing = table.Columns.Where(c => !actual.ContainsKey(c.Name)).ToList();
        var extra = actual.Keys.Where(name => table.Columns.All(c => c.Name != name)).ToList();

        // Renames first: a column that says where it came from is not ambiguous, and renaming in
        // place keeps the data. This is what turns the refusal below into something a developer can
        // resolve by stating intent rather than by hand-migrating.
        foreach (var column in missing.ToList())
        {
            if (column.RenamedFrom is not { } previous || !extra.Contains(previous))
            {
                continue;
            }

            changes.Add(new SchemaChange(
                SchemaChangeKind.Rename, table.Name, column.Name,
                $"renamed from {previous}, preserving its values",
                    $"ALTER TABLE {SqlIdent.Quote(table.Name)} RENAME COLUMN {SqlIdent.Quote(previous)} TO {SqlIdent.Quote(column.Name)};"));

            missing.Remove(column);
            extra.Remove(previous);
        }

        // A rename is otherwise indistinguishable from a drop plus an add. Adding the new column
        // would leave every existing row holding the default while the real data sat in the orphaned
        // one — a plausible wrong answer rather than an error, so ambiguity is refused rather than
        // guessed, and the message says how to resolve it.
        if (missing.Count > 0 && extra.Count > 0)
        {
            changes.Add(new SchemaChange(
                SchemaChangeKind.Incompatible, table.Name, null,
                $"columns were both added ({string.Join(", ", missing.Select(m => m.Name))}) and removed " +
                $"({string.Join(", ", extra)}). That is indistinguishable from a rename, and adding the new " +
                "columns would strand the existing data. If this IS a rename, say so with " +
                $"[RenameFrom(\"{extra[0]}\")] on the new property; otherwise migrate this table by hand."));

            return changes;
        }

        foreach (var column in missing)
        {
            changes.Add(column.CanBeAddedToExistingTable
                ? new SchemaChange(
                    SchemaChangeKind.AddColumn, table.Name, column.Name,
                    "column is missing and can be added",
                    $"ALTER TABLE {SqlIdent.Quote(table.Name)} ADD COLUMN {column.Definition};")
                : new SchemaChange(
                    SchemaChangeKind.Incompatible, table.Name, column.Name,
                    $"column is missing and cannot be added: {column.WhyNotAddable}"));
        }

        foreach (var name in extra)
        {
            changes.Add(new SchemaChange(
                SchemaChangeKind.Extra, table.Name, name,
                "column exists in the database but not in the row type. Reads are unaffected because " +
                "every generated statement names its columns."));
        }

        // Uniqueness. Without this a [Unique] added to an existing column would be silently ignored:
        // PRAGMA table_info does not report it, the hash would advance, and the constraint would
        // simply not be enforced while the schema claimed otherwise.
        //
        // Adding it is safe and additive — CREATE UNIQUE INDEX, which fails loudly if the column
        // already holds duplicates. Removing one is not: a UNIQUE declared in the table definition
        // needs a rebuild to take back.
        var unique = await ReadUniqueColumnsAsync(session, physicalName, cancellationToken);

        foreach (var column in table.Columns.Where(c => c.Unique && !c.IsKey && actual.ContainsKey(c.Name)))
        {
            if (!unique.ContainsKey(column.Name))
            {
                changes.Add(new SchemaChange(
                    SchemaChangeKind.AddIndex, table.Name, column.Name,
                    "column is declared [Unique] but the database does not enforce it",
                    $"CREATE UNIQUE INDEX IF NOT EXISTS {SqlIdent.Quote($"UX_{table.Name}_{column.Name}")} " +
                    $"ON {SqlIdent.Quote(table.Name)}({SqlIdent.Quote(column.Name)});"));
            }
        }

        foreach (var pair in unique.Where(u => u.Value == "u"))
        {
            var column = table.Columns.FirstOrDefault(c => c.Name == pair.Key);

            if (column is { Unique: false, IsKey: false })
            {
                changes.Add(new SchemaChange(
                    SchemaChangeKind.Incompatible, table.Name, column.Name,
                    "the database enforces UNIQUE on this column but the row type no longer declares it. " +
                    "A UNIQUE in a table definition cannot be dropped in place; mark the row type " +
                    "[Rebuild] and apply with SchemaUpdate.Rebuild, or leave the constraint."));
            }
        }

        // Storage class and nullability cannot be altered in place, so a mismatch is always fatal.
        foreach (var column in table.Columns.Where(c => actual.ContainsKey(c.Name)))
        {
            var live = actual[column.Name];

            if (!string.Equals(live.Storage, column.Storage, StringComparison.OrdinalIgnoreCase))
            {
                changes.Add(new SchemaChange(
                    SchemaChangeKind.Incompatible, table.Name, column.Name,
                    $"stored as {live.Storage} but the row type maps to {column.Storage}. SQLite cannot " +
                    "retype a column in place; the table has to be rebuilt."));
            }
            else if (live.NotNull != column.NotNull)
            {
                changes.Add(new SchemaChange(
                    SchemaChangeKind.Incompatible, table.Name, column.Name,
                    $"is {(live.NotNull ? "NOT NULL" : "nullable")} in the database but " +
                    $"{(column.NotNull ? "NOT NULL" : "nullable")} in the row type. SQLite cannot change " +
                    "this in place; the table has to be rebuilt."));
            }
            else if (column.Scale > 0 &&
                     recordedScales.TryGetValue($"{table.Name}.{column.Name}", out var liveScale) &&
                     liveScale != column.Scale)
            {
                changes.Add(new SchemaChange(
                    SchemaChangeKind.Incompatible, table.Name, column.Name,
                    $"decimal scale is {liveScale} in the recorded schema but {column.Scale} in the row type. " +
                    "Changing scale reinterprets every stored integer by a power of ten. Mark the row type " +
                    "[Rebuild] and apply with SchemaUpdate.Rebuild."));
            }
            else if (!SameDefault(column.Default, live.Default))
            {
                changes.Add(new SchemaChange(
                    SchemaChangeKind.Incompatible, table.Name, column.Name,
                    "the DEFAULT clause differs from the row type. SQLite cannot change a default " +
                    "in place; the table has to be rebuilt."));
            }
        }

        // PRAGMA table_info reports key membership; without this a key change updates the hash and
        // then hides behind it — CompareAsync would record the new hash and every later start would
        // take the fast path against a table whose key is still the old one.
        var modelKeys = table.Columns.Where(c => c.IsKey)
            .Select(c => c.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var actualKeys = actual.Where(c => c.Value.IsKey)
            .Select(c => c.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var column in table.Columns)
        {
            if (column.RenamedFrom is { } previous && actualKeys.Contains(previous))
            {
                actualKeys.Remove(previous);
                actualKeys.Add(column.Name);
            }
        }

        if (!modelKeys.SetEquals(actualKeys))
        {
            changes.Add(new SchemaChange(
                SchemaChangeKind.Incompatible, table.Name, null,
                $"primary key is ({string.Join(", ", actualKeys.Order(StringComparer.Ordinal))}) but " +
                $"the row type declares ({string.Join(", ", modelKeys.Order(StringComparer.Ordinal))}). " +
                "SQLite cannot change a key in place; the table has to be rebuilt."));
        }

        return changes;
    }

    // --- Reading what is actually there ------------------------------------------------------------

    private static async Task<HashSet<string>> ReadTableNamesAsync(
        SqliteSession session, CancellationToken cancellationToken)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await using var command = session.Connection.CreateCommand();
        command.Transaction = session.Transaction;
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table';";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    /// <summary>Every index, mapped to the table it belongs to and the SQL that created it.</summary>
    private static async Task<Dictionary<string, ExistingIndex>> ReadIndexesAsync(
        SqliteSession session, CancellationToken cancellationToken)
    {
        var indexes = new Dictionary<string, ExistingIndex>(StringComparer.OrdinalIgnoreCase);

        await using var command = session.Connection.CreateCommand();
        command.Transaction = session.Transaction;
        command.CommandText = "SELECT name, tbl_name, sql FROM sqlite_master WHERE type = 'index';";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            indexes[reader.GetString(0)] = new ExistingIndex(
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2));
        }

        return indexes;
    }

    private static async Task<Dictionary<string, LiveColumn>> ReadColumnsAsync(
        SqliteSession session, string table, CancellationToken cancellationToken)
    {
        var columns = new Dictionary<string, LiveColumn>(StringComparer.OrdinalIgnoreCase);

        await using var command = session.Connection.CreateCommand();
        command.Transaction = session.Transaction;

        // Table names come from the generator, never from input, so interpolation here is not a
        // parameterisation hole — and PRAGMA does not accept a bound parameter anyway.
        command.CommandText = $"PRAGMA table_info({SqlIdent.Quote(table)});";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            // cid, name, type, notnull, dflt_value, pk
            columns[reader.GetString(1)] = new LiveColumn(
                reader.GetString(2),
                reader.GetInt64(3) == 1,
                reader.GetInt64(5) != 0,
                reader.IsDBNull(4) ? null : reader.GetString(4));
        }

        return columns;
    }

    private static bool SameDefault(string? model, string? live)
    {
        static string? Normalize(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var trimmed = value.Trim();
            return trimmed.Length >= 2 && trimmed[0] == '\'' && trimmed[^1] == '\''
                ? trimmed[1..^1]
                : trimmed;
        }

        return Normalize(model) == Normalize(live);
    }

    private readonly record struct ExistingIndex(string Table, string? Sql);

    private readonly record struct LiveColumn(string Storage, bool NotNull, bool IsKey, string? Default);

    /// <summary>
    /// Single-column unique constraints, mapped to how they came about: <c>u</c> for a UNIQUE in the
    /// table definition, <c>c</c> for a CREATE UNIQUE INDEX. The difference matters — one can be
    /// dropped, the other needs a rebuild.
    ///
    /// Primary keys (<c>pk</c>) are excluded: they are unique by definition and are not what
    /// <c>[Unique]</c> is about.
    /// </summary>
    private static async Task<Dictionary<string, string>> ReadUniqueColumnsAsync(
        SqliteSession session, string table, CancellationToken cancellationToken)
    {
        var byIndex = new List<(string Index, string Origin)>();

        await using (var list = session.Connection.CreateCommand())
        {
            list.Transaction = session.Transaction;
            list.CommandText = $"PRAGMA index_list({SqlIdent.Quote(table)});";

            await using var reader = await list.ExecuteReaderAsync(cancellationToken);

            while (await reader.ReadAsync(cancellationToken))
            {
                // seq, name, unique, origin, partial
                if (reader.GetInt64(2) == 1 && reader.GetString(3) != "pk")
                {
                    byIndex.Add((reader.GetString(1), reader.GetString(3)));
                }
            }
        }

        var columns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (index, origin) in byIndex)
        {
            await using var info = session.Connection.CreateCommand();
            info.Transaction = session.Transaction;
            info.CommandText = $"PRAGMA index_info({SqlIdent.Quote(index)});";

            await using var reader = await info.ExecuteReaderAsync(cancellationToken);
            var indexed = new List<string>();

            while (await reader.ReadAsync(cancellationToken))
            {
                indexed.Add(reader.GetString(2));
            }

            // Only single-column uniqueness maps onto [Unique]; a composite one comes from
            // [TableIndex(Unique = true)] and is compared by index name instead.
            if (indexed.Count == 1)
            {
                columns[indexed[0]] = origin;
            }
        }

        return columns;
    }

    // --- Metadata ----------------------------------------------------------------------------------

    private static async Task<string?> ReadHashAsync(SqliteSession session, CancellationToken cancellationToken)
    {
        await using (var exists = session.CreateCommand(
            $"SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = '{MetadataTable}';"))
        {
            if (await session.ExecuteScalarAsync(exists, cancellationToken) is null)
            {
                return null;
            }
        }

        await using var command = session.CreateCommand(
            $"SELECT Hash FROM {SqlIdent.Quote(MetadataTable)} LIMIT 1;");
        return await session.ExecuteScalarAsync(command, cancellationToken) as string;
    }

    private static async Task EnsureScalesColumnAsync(SqliteSession session, CancellationToken cancellationToken)
    {
        var columns = await ReadColumnsAsync(session, MetadataTable, cancellationToken);

        if (!columns.ContainsKey("Scales"))
        {
            await ExecuteAsync(session,
                $"ALTER TABLE {SqlIdent.Quote(MetadataTable)} ADD COLUMN Scales TEXT;",
                cancellationToken);
        }
    }

    private static async Task<Dictionary<string, int>> ReadScalesAsync(
        SqliteSession session, CancellationToken cancellationToken)
    {
        var scales = new Dictionary<string, int>(StringComparer.Ordinal);

        if (await ReadHashAsync(session, cancellationToken) is null)
        {
            return scales;
        }

        await using var command = session.CreateCommand(
            $"SELECT Scales FROM {SqlIdent.Quote(MetadataTable)} LIMIT 1;");
        var text = await session.ExecuteScalarAsync(command, cancellationToken) as string;

        if (string.IsNullOrWhiteSpace(text))
        {
            return scales;
        }

        foreach (var line in text.Split('\n'))
        {
            var separator = line.LastIndexOf('=');
            if (separator <= 0 || separator == line.Length - 1)
            {
                continue;
            }

            if (int.TryParse(line[(separator + 1)..], out var scale))
            {
                scales[line[..separator]] = scale;
            }
        }

        return scales;
    }

    private static async Task RecordAsync(SqliteSession session, SchemaModel model, CancellationToken cancellationToken)
    {
        await using var command = session.CreateCommand(
            $"DELETE FROM {SqlIdent.Quote(MetadataTable)}; " +
            $"INSERT INTO {SqlIdent.Quote(MetadataTable)} (Hash, AppliedAt, Scales) " +
            "VALUES ($hash, $at, $scales);");
        command.Parameters.AddWithValue("$hash", model.Hash);
        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.UtcTicks);
        command.Parameters.AddWithValue("$scales", FormatScales(model));

        await session.ExecuteNonQueryAsync(command, cancellationToken);
    }

    private static string FormatScales(SchemaModel model)
    {
        var lines = new List<string>();

        foreach (var table in model.Tables)
        {
            foreach (var column in table.Columns)
            {
                if (column.Scale > 0)
                {
                    lines.Add($"{table.Name}.{column.Name}={column.Scale}");
                }
            }
        }

        return string.Join("\n", lines);
    }

    private static async Task ExecuteAsync(SqliteSession session, string sql, CancellationToken cancellationToken)
    {
        await using var command = session.CreateCommand(sql);
        await session.ExecuteNonQueryAsync(command, cancellationToken);
    }
}

/// <summary>
/// Raised when the database differs from the schema in a way that cannot be reconciled safely.
///
/// The message names every offending difference, because the whole reason for comparing models
/// rather than hashes is that "the schema changed" is not something anyone can act on.
/// </summary>
public sealed class SqliteSchemaException(IReadOnlyList<SchemaChange> changes)
    : Exception(Describe(changes))
{
    /// <summary>The differences that could not be applied.</summary>
    public IReadOnlyList<SchemaChange> Changes { get; } = changes;

    private static string Describe(IReadOnlyList<SchemaChange> changes) =>
        $"The database does not match the expected schema, and the difference cannot be applied " +
        $"automatically:{Environment.NewLine}  " +
        string.Join(Environment.NewLine + "  ", changes.Select(c => c.ToString()));
}
