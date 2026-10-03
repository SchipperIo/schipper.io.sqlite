using Microsoft.CodeAnalysis;

namespace Schipper.Io.Sqlite.Generator;

/// <summary>
/// What the generator refuses, and says so.
///
/// A generator that silently emits nothing is worse than one that fails: the consumer gets a missing
/// type and a compile error hundreds of lines away, with no hint that a row declaration was the
/// cause. Every path that produces no repository reports one of these instead.
/// </summary>
internal static class Diagnostics
{
    private const string Category = "Schipper.Io.Sqlite";

    public static readonly DiagnosticDescriptor NotAuditedRow = new(
        "SQLG001",
        "Row type must derive from AuditedRow",
        "'{0}' is marked [Table] but does not derive from Schipper.Io.Sqlite.Model.AuditedRow. The generated SQL stamps Created, Modified, User and Version on every write, so the base type is required.",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor NoKey = new(
        "SQLG002",
        "Row type has no [Key] property",
        "'{0}' is marked [Table] but has no property marked [Key]. Every generated read, update, upsert and delete addresses rows by key; apply [Key] to one property, or to several for a composite key.",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor NoColumns = new(
        "SQLG003",
        "Row type has no mappable properties",
        "'{0}' is marked [Table] but declares no public settable properties. A row type's own properties are its columns; the four audit columns are supplied by AuditedRow and are not enough on their own.",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor UnsupportedType = new(
        "SQLG004",
        "Property type has no SQLite mapping",
        "'{0}.{1}' is of type '{2}', which Schipper.Io.Sqlite does not map. Supported: string, char, bool, the integral types, float, double, decimal, Guid, DateTime, DateTimeOffset, DateOnly, TimeOnly, TimeSpan, enums, byte[], and their nullable forms. Storing an unmapped type would silently round-trip through TEXT and compare lexicographically — which is how '9' ends up greater than '10'.",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor KeyIsNullable = new(
        "SQLG005",
        "Key property is nullable",
        "'{0}.{1}' is marked [Key] but is nullable. A primary key column cannot be NULL, and a nullable key would make the keyset cursor ambiguous.",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor RowTypeIsAbstract = new(
        "SQLG006",
        "Row type is abstract",
        "'{0}' is marked [Table] but is abstract, so no repository can construct it. Mark the concrete type instead.",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor UnsignedLong = new(
        "SQLG007",
        "ulong has no lossless SQLite mapping",
        "'{0}.{1}' is a ulong. SQLite's INTEGER is 64-bit SIGNED, so any value above long.MaxValue would round-trip as a negative number and compare wrongly — silently, and only for large values. Use long, or store it as a string if the full range is genuinely needed.",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor KeyTypeUnsuitable = new(
        "SQLG008",
        "Key property has an unsuitable type",
        "'{0}.{1}' is marked [Key] but is of type '{2}'. A floating-point key cannot be compared exactly, and a BLOB key cannot serve the keyset cursor. Use a string, Guid or integral key.",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor ConflictingDatabaseNamespace = new(
        "SQLG009",
        "Conflicting [DatabaseNamespace] values",
        "'{0}' and '{1}' specify different [DatabaseNamespace] values ('{2}' and '{3}'). There is one generated Database type; every [DatabaseNamespace] in the compilation must name the same namespace.",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor InvalidDatabaseNamespace = new(
        "SQLG010",
        "[DatabaseNamespace] is not a valid namespace",
        "[DatabaseNamespace] on '{0}' is '{1}', which is not a valid C# namespace",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor NoPublicParameterlessConstructor = new(
        "SQLG011",
        "Row type has no public parameterless constructor",
        "'{0}' is marked [Table] but has no public parameterless constructor. The generated Map method constructs the row with an object initializer, so a constructor that requires arguments cannot be called.",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor SetterNotAccessible = new(
        "SQLG012",
        "Mapped property setter is not public or internal",
        "'{0}.{1}' is mapped as a column but its setter is not public or internal. The generated repository is a separate class and cannot assign a private or protected setter. Use public or internal set, or an init accessor.",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor InvalidScale = new(
        "SQLG013",
        "[Scale] is not an integer in 0..10",
        "'{0}.{1}' has [Scale] whose argument is not an int in 0..10. Omit [Scale] to use the default of 4, or pass an integer in that range. An out-of-range value is not silently replaced with 4, because that would store a different magnitude than the attribute says.",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);
}
