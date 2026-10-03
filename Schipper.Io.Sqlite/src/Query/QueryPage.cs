using System.Collections;

namespace Schipper.Io.Sqlite.Query;

/// <summary>
/// One page of a keyset read. <see cref="HasMore"/> is the difference between "this is everything"
/// and "the next row exists and was not returned" — a list that happens to be 100 long cannot say
/// which, and treating it as "all matches" is a silent truncation.
/// </summary>
public sealed class QueryPage<T> : IReadOnlyList<T>
{
    /// <summary>Creates a page from the rows that were actually returned.</summary>
    public QueryPage(IReadOnlyList<T> items, bool hasMore)
    {
        Items = items;
        HasMore = hasMore;
    }

    /// <summary>The rows in this page, at most the requested limit.</summary>
    public IReadOnlyList<T> Items { get; }

    /// <summary>Whether at least one more row exists beyond this page.</summary>
    public bool HasMore { get; }

    /// <inheritdoc />
    public int Count => Items.Count;

    /// <inheritdoc />
    public T this[int index] => Items[index];

    /// <inheritdoc />
    public IEnumerator<T> GetEnumerator() => Items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
