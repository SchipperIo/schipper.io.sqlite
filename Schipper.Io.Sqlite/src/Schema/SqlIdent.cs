namespace Schipper.Io.Sqlite.Schema;

/// <summary>
/// Quotes SQLite identifiers. Table and column names are interpolated into DDL and DML; unquoted
/// <c>Order</c>, <c>From</c>, and the rest are keywords on the bundled engine.
/// </summary>
internal static class SqlIdent
{
    /// <summary>Wraps <paramref name="name"/> in double quotes, doubling any embedded quotes.</summary>
    public static string Quote(string name) =>
        "\"" + name.Replace("\"", "\"\"") + "\"";

    /// <summary>
    /// Whether two CREATE INDEX statements describe the same index. sqlite_master omits
    /// <c>IF NOT EXISTS</c>, may differ in whitespace, and may gain identifier quotes on the first
    /// quoted deploy. None of those is a real difference.
    /// </summary>
    public static bool IndexSqlEquals(string actual, string expected)
    {
        static string Canonical(string sql)
        {
            var compact = string.Concat(sql.Where(static c => !char.IsWhiteSpace(c) && c != '"'))
                .TrimEnd(';');
            return compact.Replace("IFNOTEXISTS", "", StringComparison.OrdinalIgnoreCase)
                .ToUpperInvariant();
        }

        return Canonical(actual) == Canonical(expected);
    }
}
