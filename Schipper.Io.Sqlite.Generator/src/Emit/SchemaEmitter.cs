using System.Security.Cryptography;
using System.Text;

namespace Schipper.Io.Sqlite.Generator.Emit;

/// <summary>
/// Turns row types into <c>CREATE TABLE</c> and <c>CREATE INDEX</c>.
///
/// This is what makes <c>[Unique]</c>, <c>[Index]</c>, <c>[TableIndex]</c>, <c>[References]</c> and
/// <c>[SqlDefault]</c> mean something. Until it existed they were declared, documented and read by
/// nothing — an attribute that promises a constraint and produces no SQL is worse than no attribute,
/// because the promise is believed.
///
/// It also removes the second source of truth. Column type and nullability come from the same C#
/// declaration the repository is generated from, so the schema and the code that reads it cannot
/// disagree about what a column holds.
/// </summary>
internal static class SchemaEmitter
{
    /// <summary>Every statement for one table: the CREATE TABLE, then its indexes.</summary>
    public static List<string> Statements(RowModel row)
    {
        var statements = new List<string> { CreateTable(row) };
        var key = row.Columns.First(c => c.IsKey);

        foreach (var column in row.Columns.Where(c => c.Indexed))
        {
            // The key is appended so the index can serve a paged read. Generated reads always order
            // by the key, so an index stopping at the filter column leaves SQLite sorting every
            // matching row on every page — measured to degrade quadratically as the table grows.
            // Doing it here rather than documenting it means it cannot be forgotten.
            var columns = column.Name == key.Name
                ? SqlIdent.Quote(column.Name)
                : $"{SqlIdent.Quote(column.Name)}, {SqlIdent.Quote(key.Name)}";

            statements.Add(
                $"CREATE INDEX IF NOT EXISTS {SqlIdent.Quote($"IX_{row.Table}_{column.Name}")} " +
                $"ON {SqlIdent.Quote(row.Table)}({columns});");
        }

        foreach (var index in row.Indexes)
        {
            statements.Add(CreateIndex(row, index, key));
        }

        return statements;
    }

    private static string CreateTable(RowModel row)
    {
        var keys = row.Columns.Where(c => c.IsKey).ToList();
        var sb = new StringBuilder();

        // A literal \n rather than Environment.NewLine. The DDL text is hashed, and a newline that
        // depended on the build machine would make the same schema hash differently on Windows and
        // Linux — reporting drift on every deploy, which is how a drift check gets ignored.
        // RS1035 bans Environment in analyzers for adjacent reasons.
        const string nl = "\n";

        sb.Append("CREATE TABLE IF NOT EXISTS ").Append(SqlIdent.Quote(row.Table)).Append(" (");

        foreach (var column in row.Columns)
        {
            sb.Append(nl).Append("    ").Append(ColumnDefinition(column)).Append(',');
        }

        sb.Append(nl).Append("    PRIMARY KEY (")
          .Append(string.Join(", ", keys.Select(k => SqlIdent.Quote(k.Name)))).Append(')')
          .Append(nl).Append(");");

        return sb.ToString();
    }

    /// <summary>
    /// One column's DDL fragment. Public because ALTER TABLE ADD COLUMN reuses it verbatim: a
    /// column added later must be defined exactly as it would have been at CREATE TABLE, or the
    /// comparison would report drift forever after.
    /// </summary>
    public static string ColumnDefinition(ColumnModel column)
    {
        var sb = new StringBuilder();

        sb.Append(SqlIdent.Quote(column.Name)).Append(' ').Append(column.Map.Storage);
        sb.Append(column.Nullable ? " NULL" : " NOT NULL");

        if (column.Default is { Length: > 0 } expression)
        {
            sb.Append(" DEFAULT ").Append(expression);
        }

        // A UNIQUE column constraint rather than a unique index: the same guarantee, one fewer name
        // to collide, and it travels with the table definition where it is visible.
        if (column.Unique)
        {
            sb.Append(" UNIQUE");
        }

        if (column.References is { } fk)
        {
            sb.Append(" REFERENCES ").Append(SqlIdent.Quote(fk.Table))
              .Append('(').Append(SqlIdent.Quote(fk.Column)).Append(')');

            if (fk.Cascade)
            {
                sb.Append(" ON DELETE CASCADE");
            }
        }

        return sb.ToString();
    }

    private static string CreateIndex(RowModel row, TableIndex index, ColumnModel key)
    {
        var columns = index.Columns.ToList();

        // Same rule as [Index] above — except for a unique index, where appending the key would
        // change what is being enforced from "these columns are unique" to "these columns plus the
        // key are unique", which is no constraint at all.
        if (!index.Unique && !columns.Contains(key.Name))
        {
            columns.Add(key.Name);
        }

        var name = $"{(index.Unique ? "UX" : "IX")}_{row.Table}_{string.Join("_", index.Columns)}";
        var sql = $"CREATE {(index.Unique ? "UNIQUE " : "")}INDEX IF NOT EXISTS {SqlIdent.Quote(name)} " +
                  $"ON {SqlIdent.Quote(row.Table)}({string.Join(", ", columns.Select(SqlIdent.Quote))})";

        return index.Where is { Length: > 0 } predicate
            ? $"{sql} WHERE {predicate};"
            : $"{sql};";
    }

    /// <summary>
    /// A stable fingerprint of the whole schema, recorded in the database when it is created and
    /// checked on every start.
    ///
    /// SHA-256 over the statements rather than a string hash code, because <c>string.GetHashCode</c>
    /// is randomised per process in .NET Core — a value that changed between runs would report drift
    /// every time and teach everyone to ignore it.
    /// </summary>
    public static string Hash(IEnumerable<string> statements)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n", statements)));
        var sb = new StringBuilder(16);

        // Sixteen hex characters. This detects change, it does not defend against a forged hash, so
        // the full digest would be noise in an error message somebody has to read.
        for (var i = 0; i < 8; i++)
        {
            sb.Append(bytes[i].ToString("x2"));
        }

        return sb.ToString();
    }

    /// <summary>
    /// Scale is not a SQLite storage class, so it is hashed beside the DDL. A <c>[Scale]</c> edit
    /// must not take the applied-hash fast path: every stored integer would be read at the wrong
    /// magnitude.
    /// </summary>
    public static IEnumerable<string> ScaleLines(RowModel row)
    {
        foreach (var column in row.Columns)
        {
            if (column.Map.Kind == MapKind.Decimal)
            {
                yield return $"SCALE {row.Table}.{column.Name}={column.Map.Scale}";
            }
        }
    }
}
