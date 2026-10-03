namespace Schipper.Io.Sqlite.Schema;

/// <summary>
/// The schema the running code expects, generated from the row types.
///
/// This is a model rather than a list of SQL strings because the interesting question at startup is
/// not "what should I run" but "how does the database differ from this" — and only a model can
/// answer that in terms a person can act on.
/// </summary>
/// <param name="Hash">Fingerprint of the whole schema; equal hashes skip the comparison entirely.</param>
/// <param name="Tables">Every table, ordered by name so the hash does not depend on file order.</param>
public sealed record SchemaModel(string Hash, IReadOnlyList<TableModel> Tables);

/// <summary>One table and everything declared on it.</summary>
/// <param name="Name">The table name as it appears in SQL.</param>
/// <param name="Columns">Every column, in the order the generated statements use.</param>
/// <param name="Indexes">Indexes from <c>[Index]</c> and <c>[TableIndex]</c>.</param>
/// <param name="CreateSql">The full CREATE TABLE, used when the table is missing altogether.</param>
/// <param name="RenamedFrom">A previous table name declared by <c>[RenameFrom]</c>, if any.</param>
/// <param name="AllowRebuild">Whether <c>[Rebuild]</c> permits this table to be rebuilt in place.</param>
public sealed record TableModel(
    string Name,
    IReadOnlyList<ColumnDef> Columns,
    IReadOnlyList<IndexDef> Indexes,
    string CreateSql,
    string? RenamedFrom = null,
    bool AllowRebuild = false);

/// <summary>One column, with enough detail to decide whether it can be added to a live table.</summary>
/// <param name="Name">Column name.</param>
/// <param name="Storage">SQLite storage class: TEXT, INTEGER, REAL or BLOB.</param>
/// <param name="NotNull">Whether the column is NOT NULL.</param>
/// <param name="Default">Literal SQL default, or null.</param>
/// <param name="IsKey">Whether the column is part of the primary key.</param>
/// <param name="Unique">Whether the column carries a UNIQUE constraint.</param>
/// <param name="HasForeignKey">Whether the column references another table.</param>
/// <param name="Definition">The column's DDL fragment, reused verbatim by ALTER TABLE ADD COLUMN.</param>
/// <param name="RenamedFrom">A previous column name declared by <c>[RenameFrom]</c>, if any.</param>
/// <param name="Scale">Decimal digits for a scaled-integer column; 0 when the column is not a <c>decimal</c>.</param>
public sealed record ColumnDef(
    string Name,
    string Storage,
    bool NotNull,
    string? Default,
    bool IsKey,
    bool Unique,
    bool HasForeignKey,
    string Definition,
    string? RenamedFrom = null,
    int Scale = 0)
{
    /// <summary>
    /// Whether SQLite will let this column be added to a table that already exists.
    ///
    /// The restrictions are SQLite's, not ours: <c>ADD COLUMN</c> cannot introduce a PRIMARY KEY or
    /// UNIQUE constraint, a NOT NULL column must carry a non-null default, and a column with a
    /// foreign key must default to NULL. Everything else needs the twelve-step table rebuild, which
    /// this library deliberately does not attempt.
    /// </summary>
    public bool CanBeAddedToExistingTable =>
        !IsKey &&
        !Unique &&
        (!NotNull || Default is not null) &&
        (!HasForeignKey || !NotNull);

    /// <summary>Why <see cref="CanBeAddedToExistingTable"/> is false, for the error message.</summary>
    public string WhyNotAddable =>
        IsKey ? "it is part of the primary key"
        : Unique ? "SQLite cannot add a UNIQUE column to an existing table"
        : NotNull && Default is null ? "it is NOT NULL with no default, so existing rows would have no value"
        : HasForeignKey && NotNull ? "a NOT NULL foreign key cannot be added to a table that already has rows"
        : "it cannot be added";
}

/// <summary>An index, by name and by the statement that creates it.</summary>
public sealed record IndexDef(string Name, string CreateSql);

/// <summary>What kind of difference was found between the model and the database.</summary>
public enum SchemaChangeKind
{
    /// <summary>A table in the model is not in the database. Created automatically.</summary>
    AddTable,

    /// <summary>A column in the model is not in the database, and can be added. Applied automatically.</summary>
    AddColumn,

    /// <summary>An index in the model is not in the database. Created automatically.</summary>
    AddIndex,

    /// <summary>
    /// A column or table declares <c>[RenameFrom]</c> and the old name is present. Renamed in place,
    /// which preserves the data — the whole reason the attribute exists.
    /// </summary>
    Rename,

    /// <summary>
    /// Something in the database is not in the model. Harmless to reads, because every generated
    /// statement names its columns, but reported so drift is visible.
    /// </summary>
    Extra,

    /// <summary>
    /// The table is being rebuilt because a change cannot be applied in place. Requires both
    /// <c>[Rebuild]</c> on the row type and <see cref="SchemaUpdate.Rebuild"/> at the call site.
    /// </summary>
    Rebuild,

    /// <summary>A difference that cannot be applied safely. Always fatal.</summary>
    Incompatible,
}

/// <summary>One difference between the expected schema and the database.</summary>
/// <param name="Kind">How the difference was classified.</param>
/// <param name="Table">The table it concerns.</param>
/// <param name="Column">The column, when the difference is column-level.</param>
/// <param name="Detail">A sentence a person can act on.</param>
/// <param name="Sql">The statement that resolves it, when one exists.</param>
public sealed record SchemaChange(
    SchemaChangeKind Kind,
    string Table,
    string? Column,
    string Detail,
    string? Sql = null)
{
    /// <inheritdoc />
    public override string ToString() =>
        $"{Kind} {Table}{(Column is null ? "" : "." + Column)}: {Detail}";
}

/// <summary>How much <see cref="SchemaApplier"/> is allowed to change.</summary>
public enum SchemaUpdate
{
    /// <summary>
    /// Create missing tables and indexes, and add columns that SQLite can add safely. The default,
    /// because a new table and a new nullable column are the two changes that actually happen, and
    /// both are non-destructive.
    /// </summary>
    Additive,

    /// <summary>
    /// Create missing tables and indexes, but treat a missing column as fatal. For deployments that
    /// would rather fail than have a process alter a shared table on startup.
    /// </summary>
    CreateOnly,

    /// <summary>Change nothing. Any difference at all is fatal.</summary>
    VerifyOnly,

    /// <summary>
    /// Everything <see cref="Additive"/> does, plus rebuilding tables marked <c>[Rebuild]</c> when a
    /// change cannot be applied in place.
    ///
    /// Separate from the attribute on purpose. A rebuild can lose data, so it takes two deliberate
    /// acts to reach — marking the type, and asking for it here.
    /// </summary>
    Rebuild,
}
