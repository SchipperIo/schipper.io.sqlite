namespace Schipper.Io.Sqlite.Generator;

/// <summary>A foreign key declared by <c>[References]</c>.</summary>
internal sealed record ForeignKey(string Table, string Column, bool Cascade);

/// <summary>A composite or partial index declared by <c>[TableIndex]</c>.</summary>
internal sealed record TableIndex(string[] Columns, string? Where, bool Unique)
{
    // Arrays are reference-equal by default, which would make every RowModel look changed to the
    // incremental generator. Compared by value so the cache can do its job.
    public bool Equals(TableIndex? other) =>
        other is not null &&
        Unique == other.Unique &&
        Where == other.Where &&
        Columns.Length == other.Columns.Length &&
        !Columns.Where((c, i) => c != other.Columns[i]).Any();

    public override int GetHashCode()
    {
        var hash = (Where?.GetHashCode() ?? 0) ^ Unique.GetHashCode();

        foreach (var column in Columns)
        {
            hash = (hash * 31) ^ column.GetHashCode();
        }

        return hash;
    }
}

/// <summary>One column: its name, how it is stored, and the constraints declared on it.</summary>
internal sealed record ColumnModel(
    string Name,
    TypeMapping Map,
    bool Nullable,
    bool IsKey,
    bool Unique = false,
    bool Indexed = false,
    string? Default = null,
    ForeignKey? References = null,
    string? RenamedFrom = null);

/// <summary>One table, as read off its row type.</summary>
internal sealed record RowModel(
    string Namespace,
    string TypeName,
    string Table,
    List<ColumnModel> Columns,
    List<TableIndex> Indexes,
    string? RenamedFrom = null,
    bool AllowRebuild = false,
    string? DatabaseNamespace = null);
