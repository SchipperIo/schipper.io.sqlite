using Microsoft.CodeAnalysis;

namespace Schipper.Io.Sqlite.Generator;

/// <summary>How a CLR type is carried in one of SQLite's five storage classes.</summary>
internal enum MapKind
{
    /// <summary>TEXT, verbatim.</summary>
    String,

    /// <summary>INTEGER 0/1.</summary>
    Bool,

    /// <summary>INTEGER. Covers every integral width; SQLite stores them all as 64-bit anyway.</summary>
    Integer,

    /// <summary>REAL.</summary>
    Real,

    /// <summary>INTEGER, scaled by a power of ten. Never REAL, never TEXT.</summary>
    Decimal,

    /// <summary>TEXT, canonical lowercase 8-4-4-4-12.</summary>
    Guid,

    /// <summary>INTEGER, UTC ticks. The offset is not preserved.</summary>
    DateTimeOffset,

    /// <summary>INTEGER, UTC ticks.</summary>
    DateTime,

    /// <summary>INTEGER, day number.</summary>
    DateOnly,

    /// <summary>INTEGER, ticks since midnight.</summary>
    TimeOnly,

    /// <summary>INTEGER, ticks.</summary>
    TimeSpan,

    /// <summary>INTEGER, the underlying value.</summary>
    Enum,

    /// <summary>BLOB.</summary>
    Bytes,

    /// <summary>TEXT, one character.</summary>
    Char,
}

/// <summary>
/// A resolved column mapping: what the C# type is, how it is stored, and the expressions that move a
/// value each way.
/// </summary>
/// <param name="Kind">Which encoding applies.</param>
/// <param name="ClrType">Fully qualified C# type, used in generated signatures and casts.</param>
/// <param name="Storage">SQLite column type, for the DDL.</param>
/// <param name="Scale">Decimal digits preserved; meaningful only for <see cref="MapKind.Decimal"/>.</param>
internal sealed record TypeMapping(MapKind Kind, string ClrType, string Storage, int Scale = 0)
{
    /// <summary>Expression reading a non-null value out of a reader at ordinal <paramref name="ordinal"/>.</summary>
    public string Read(string ordinal) => Kind switch
    {
        MapKind.String => $"reader.GetString({ordinal})",
        MapKind.Bool => $"reader.GetBoolean({ordinal})",
        MapKind.Integer => ClrType == "long" ? $"reader.GetInt64({ordinal})" : $"({ClrType})reader.GetInt64({ordinal})",
        MapKind.Real => ClrType == "double" ? $"reader.GetDouble({ordinal})" : $"({ClrType})reader.GetDouble({ordinal})",
        MapKind.Decimal => $"SqliteValue.ToDecimal(reader.GetInt64({ordinal}), {Scale})",
        MapKind.Guid => $"SqliteValue.ToGuid(reader.GetString({ordinal}))",
        MapKind.DateTimeOffset => $"SqliteValue.ToDateTimeOffset(reader.GetInt64({ordinal}))",
        MapKind.DateTime => $"SqliteValue.ToDateTime(reader.GetInt64({ordinal}))",
        MapKind.DateOnly => $"SqliteValue.ToDateOnly(reader.GetInt64({ordinal}))",
        MapKind.TimeOnly => $"SqliteValue.ToTimeOnly(reader.GetInt64({ordinal}))",
        MapKind.TimeSpan => $"SqliteValue.ToTimeSpan(reader.GetInt64({ordinal}))",
        MapKind.Enum => $"({ClrType})reader.GetInt64({ordinal})",
        MapKind.Bytes => $"reader.GetFieldValue<byte[]>({ordinal})",
        MapKind.Char => $"SqliteValue.ToChar(reader.GetString({ordinal}))",
        _ => throw new InvalidOperationException($"Unmapped kind reached Read: {Kind}"),
    };

    /// <summary>Expression converting <paramref name="value"/> into something the driver can bind.</summary>
    public string Write(string value) => Kind switch
    {
        MapKind.String or MapKind.Bool or MapKind.Bytes => value,
        MapKind.Integer => ClrType == "long" ? value : $"(long){value}",
        MapKind.Real => ClrType == "double" ? value : $"(double){value}",
        MapKind.Decimal => $"SqliteValue.FromDecimal({value}, {Scale})",
        MapKind.Guid => $"SqliteValue.FromGuid({value})",
        MapKind.DateTimeOffset => $"SqliteValue.FromDateTimeOffset({value})",
        MapKind.DateTime => $"SqliteValue.FromDateTime({value})",
        MapKind.DateOnly => $"SqliteValue.FromDateOnly({value})",
        MapKind.TimeOnly => $"SqliteValue.FromTimeOnly({value})",
        MapKind.TimeSpan => $"SqliteValue.FromTimeSpan({value})",
        MapKind.Enum => $"(long){value}",
        MapKind.Char => $"SqliteValue.FromChar({value})",
        _ => throw new InvalidOperationException($"Unmapped kind reached Write: {Kind}"),
    };
}

/// <summary>
/// Resolves a property type to its storage mapping, or refuses.
///
/// Refusing is the point. The previous version fell through to <c>GetString</c> for anything it did
/// not recognise, so a <c>Guid</c> or a <c>decimal</c> was silently stored as TEXT and compared
/// lexicographically — plausible wrong answers rather than an error, which is the failure mode this
/// library exists to remove.
/// </summary>
internal static class TypeMap
{
    public static TypeMapping? Resolve(ITypeSymbol type, int scale)
    {
        if (type.TypeKind == TypeKind.Enum)
        {
            return new TypeMapping(MapKind.Enum, Display(type), "INTEGER");
        }

        if (type is IArrayTypeSymbol { ElementType.SpecialType: SpecialType.System_Byte })
        {
            return new TypeMapping(MapKind.Bytes, "byte[]", "BLOB");
        }

        var mapping = type.SpecialType switch
        {
            SpecialType.System_String => new TypeMapping(MapKind.String, "string", "TEXT"),
            SpecialType.System_Boolean => new TypeMapping(MapKind.Bool, "bool", "INTEGER"),
            SpecialType.System_Char => new TypeMapping(MapKind.Char, "char", "TEXT"),

            SpecialType.System_Byte => new TypeMapping(MapKind.Integer, "byte", "INTEGER"),
            SpecialType.System_SByte => new TypeMapping(MapKind.Integer, "sbyte", "INTEGER"),
            SpecialType.System_Int16 => new TypeMapping(MapKind.Integer, "short", "INTEGER"),
            SpecialType.System_UInt16 => new TypeMapping(MapKind.Integer, "ushort", "INTEGER"),
            SpecialType.System_Int32 => new TypeMapping(MapKind.Integer, "int", "INTEGER"),
            SpecialType.System_UInt32 => new TypeMapping(MapKind.Integer, "uint", "INTEGER"),
            SpecialType.System_Int64 => new TypeMapping(MapKind.Integer, "long", "INTEGER"),

            SpecialType.System_Single => new TypeMapping(MapKind.Real, "float", "REAL"),
            SpecialType.System_Double => new TypeMapping(MapKind.Real, "double", "REAL"),
            SpecialType.System_Decimal => new TypeMapping(MapKind.Decimal, "decimal", "INTEGER", scale),

            SpecialType.System_DateTime => new TypeMapping(MapKind.DateTime, "System.DateTime", "INTEGER"),
            _ => null,
        };

        if (mapping is not null)
        {
            return mapping;
        }

        // Types with no SpecialType. Matched by full name rather than by symbol identity because the
        // generator targets netstandard2.0 and cannot reference them.
        return Display(type) switch
        {
            "System.Guid" => new TypeMapping(MapKind.Guid, "System.Guid", "TEXT"),
            "System.DateTimeOffset" => new TypeMapping(MapKind.DateTimeOffset, "System.DateTimeOffset", "INTEGER"),
            "System.DateOnly" => new TypeMapping(MapKind.DateOnly, "System.DateOnly", "INTEGER"),
            "System.TimeOnly" => new TypeMapping(MapKind.TimeOnly, "System.TimeOnly", "INTEGER"),
            "System.TimeSpan" => new TypeMapping(MapKind.TimeSpan, "System.TimeSpan", "INTEGER"),
            _ => null,
        };
    }

    /// <summary>
    /// <c>ulong</c> is refused rather than mapped. SQLite's INTEGER is 64-bit <b>signed</b>, so any
    /// value above <c>long.MaxValue</c> would round-trip as a negative number and compare wrongly —
    /// silently, and only for large values, which is the worst possible time to find out.
    /// </summary>
    public static bool IsUnsignedLong(ITypeSymbol type) => type.SpecialType == SpecialType.System_UInt64;

    private static string Display(ITypeSymbol type) =>
        type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat.WithGlobalNamespaceStyle(
            SymbolDisplayGlobalNamespaceStyle.Omitted));
}
