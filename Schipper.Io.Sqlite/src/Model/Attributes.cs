namespace Schipper.Io.Sqlite.Model;

// Declarative schema, read by Schipper.Io.Sqlite.Generator at build time to emit both the repository and
// the CREATE TABLE for each row type. The row class is the single source of truth: there is no
// separate .sql file to keep in step, because there is nothing to keep in step with.
//
// Only what SQLite actually needs is modelled. Column type and nullability are deliberately NOT
// attributes — they come from the C# type and its nullable annotation, so they cannot contradict the
// property they describe.

/// <summary>Marks a row type as a table and names it. The type must derive from <see cref="AuditedRow"/>.</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class TableAttribute(string name) : Attribute
{
    /// <summary>The table name as it appears in SQL.</summary>
    public string Name { get; } = name;
}

/// <summary>Marks the primary key. Apply to more than one property for a composite key.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class KeyAttribute : Attribute;

/// <summary>Adds a UNIQUE constraint to this column.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class UniqueAttribute : Attribute;

/// <summary>Indexes this column.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class IndexAttribute : Attribute;

/// <summary>A composite or partial index over the table.</summary>
/// <remarks>
/// Generated reads always order by the key, so <b>an index intended to serve a paged read must end
/// with the key column</b>. An index that stops at the filter column leaves SQLite sorting every
/// matching row on every page: measured at 38k rows/sec against 1.5M rows with the key appended,
/// and the single-column form degrades quadratically as the table grows.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class TableIndexAttribute(params string[] columns) : Attribute
{
    /// <summary>The indexed columns, in order.</summary>
    public string[] Columns { get; } = columns;

    /// <summary>Optional predicate for a partial index, e.g. <c>"DispatchedAt IS NULL"</c>.</summary>
    public string? Where { get; set; }

    /// <summary>Whether the index enforces uniqueness.</summary>
    public bool Unique { get; set; }
}

/// <summary>Declares a foreign key to another table's column.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class ReferencesAttribute(string table, string column = "Id") : Attribute
{
    /// <summary>The referenced table.</summary>
    public string Table { get; } = table;

    /// <summary>The referenced column.</summary>
    public string Column { get; } = column;

    /// <summary>Emits ON DELETE CASCADE, for children that cannot outlive their parent.</summary>
    public bool Cascade { get; set; }
}

/// <summary>A literal SQL DEFAULT for the column, e.g. <c>"0"</c> or <c>"'US'"</c>.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SqlDefaultAttribute(string expression) : Attribute
{
    /// <summary>The default expression, emitted verbatim into the DDL.</summary>
    public string Expression { get; } = expression;
}

/// <summary>
/// Declares that this column or table used to have a different name, so an existing database is
/// renamed rather than rebuilt.
/// </summary>
/// <remarks>
/// <para>
/// A rename is otherwise indistinguishable from dropping one column and adding another. Left to
/// guess, the schema check would add the new column, leave the old one orphaned, and every existing
/// row would hold the default while the real data sat next to it — a plausible wrong answer rather
/// than an error. So an unexplained add-and-remove on one table is refused, and this attribute is
/// what explains it.
/// </para>
/// <para>
/// Safe to leave in place forever. On a database that has already been renamed the old name is gone,
/// so nothing matches and nothing happens; on a fresh database the table is simply created. It is a
/// statement about history, not an instruction to run once.
/// </para>
/// <example>
/// <code>
/// [RenameFrom("Amount")] public long Total { get; set; }
/// </code>
/// </example>
/// </remarks>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Class)]
public sealed class RenameFromAttribute(string previousName) : Attribute
{
    /// <summary>The name this column or table had before.</summary>
    public string PreviousName { get; } = previousName;
}

/// <summary>
/// Permits this table to be rebuilt when a change cannot be applied in place.
/// </summary>
/// <remarks>
/// <para>
/// SQLite cannot retype a column, change its nullability, or alter a key. The only way to do any of
/// those is the twelve-step rebuild from the SQLite documentation: create a new table, copy the rows
/// across, drop the old one, rename. This attribute is how you say that is acceptable for this table.
/// </para>
/// <para>
/// <b>It can lose data, which is why it cannot be inferred.</b> The copy is an
/// <c>INSERT ... SELECT</c>, so SQLite coerces per column affinity — narrowing a column silently
/// truncates — and any column no longer on the row type is simply not carried across. A rename is
/// preserved only if you also say <c>[RenameFrom]</c>; without it the old column's values go.
/// </para>
/// <para>
/// Two keys are required, not one: this attribute <i>and</i> <c>SchemaUpdate.Rebuild</c> at the call
/// site. A destructive operation should not be reachable by editing one file.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class)]
public sealed class RebuildAttribute : Attribute;

/// <summary>
/// Places the generated <c>Database</c> type in this namespace instead of the project's
/// <c>RootNamespace</c>.
/// </summary>
/// <remarks>
/// There is one <c>Database</c> type for the whole compilation, built from every <c>[Table]</c>
/// row type. If more than one row specifies this attribute, they must name the same namespace.
/// Use it when the default would collide with a type or namespace you already have — most often
/// a <c>Database</c> folder under the app root.
/// </remarks>
/// <example>
/// <code>
/// [Table("Widgets")]
/// [DatabaseNamespace("MyApp.Persistence")]
/// public sealed class WidgetRow : AuditedRow { ... }
/// // generates MyApp.Persistence.Database
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class)]
public sealed class DatabaseNamespaceAttribute(string fullNamespace) : Attribute
{
    /// <summary>The full namespace the generated <c>Database</c> type should live in.</summary>
    public string FullNamespace { get; } = fullNamespace;
}
