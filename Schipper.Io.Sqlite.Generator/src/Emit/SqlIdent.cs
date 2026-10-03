using Microsoft.CodeAnalysis.CSharp;

namespace Schipper.Io.Sqlite.Generator.Emit;

/// <summary>
/// Quotes SQLite identifiers and prefixes C# reserved keywords so generated repositories compile
/// when a property is named <c>event</c> and so DDL survives <c>Order</c> / <c>From</c> / <c>Group</c>.
/// </summary>
internal static class SqlIdent
{
    /// <summary>Wraps <paramref name="name"/> in double quotes, doubling any embedded quotes.</summary>
    public static string Quote(string name) =>
        "\"" + name.Replace("\"", "\"\"") + "\"";

    /// <summary>A C# identifier, with <c>@</c> when <paramref name="name"/> is a reserved keyword.</summary>
    public static string CSharp(string name)
    {
        var kind = SyntaxFacts.GetKeywordKind(name);
        return kind != SyntaxKind.None && SyntaxFacts.IsReservedKeyword(kind)
            ? "@" + name
            : name;
    }
}
