namespace Schipper.Io.Sqlite;

/// <summary>
/// Raised when a version-checked write matched no row.
///
/// Either the row is gone, or somebody else has written it since this copy was read — the write's
/// WHERE clause carries the version that was read, so a changed row no longer matches. The two
/// cases are not distinguished, because telling them apart costs a second query and the caller's
/// response is the same either way: re-read and reconsider.
///
/// This throws rather than returning a count because silently discarding a lost update is precisely
/// the plausible-wrong-answer failure this library exists to prevent. Callers that want to handle it
/// without an exception use <c>TryUpdateAsync</c> / <c>TryUpsertAsync</c>.
/// </summary>
/// <remarks>
/// Named for SQLite rather than shortened to <c>DbConcurrencyException</c> on purpose:
/// <c>System.Data.DBConcurrencyException</c> already exists, and two types differing only in the
/// casing of "Db" is the kind of near-collision that produces a confidently wrong <c>catch</c>.
/// </remarks>
public sealed class SqliteConcurrencyException(string table, string key)
    : Exception($"{table} {key} was not updated: it has been changed or removed since it was read.")
{
    /// <summary>The table the write targeted.</summary>
    public string Table { get; } = table;

    /// <summary>The key value(s) that did not match, joined with <c>/</c> for a composite key.</summary>
    public string Key { get; } = key;
}
