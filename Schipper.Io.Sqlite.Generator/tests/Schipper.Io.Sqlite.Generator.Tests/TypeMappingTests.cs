using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Schipper.Io.Sqlite.Generator.Tests;

/// <summary>
/// What the generator emits for each mapped type.
///
/// The round-trip through a real database lives in the bench's audit mode, where it runs under
/// Native AOT. These tests cover what that cannot: the encodings it *refuses*, and the fact that a
/// column's storage form is chosen rather than defaulted.
/// </summary>
[TestClass]
public sealed class TypeMappingTests
{
    private static string Row(string properties) => $$"""
        using System;
        using Schipper.Io.Sqlite.Model;
        namespace Test;

        public enum Colour { Red = 0, Green = 1 }

        [Table("Things")]
        public sealed class ThingRow : AuditedRow
        {
            [Key] public string Id { get; set; } = "";
            {{properties}}
        }
        """;

    /// <summary>
    /// The defect this library was extracted from. TEXT is the *driver's* default for decimal, not
    /// something EF invented, and it compares lexicographically — so a stock check would rank "9"
    /// above "10". Scaled integers compare numerically.
    /// </summary>
    [TestMethod]
    public void Decimal_IsScaledInteger_NeverTextOrReal()
    {
        var source = GeneratorHarness.Run(Row("public decimal Price { get; set; }")).Only;

        StringAssert.Contains(source, "SqliteValue.FromDecimal(row.Price, 4)");
        StringAssert.Contains(source, "SqliteValue.ToDecimal(reader.GetInt64(oPrice), 4)");
        Assert.IsFalse(source.Contains("GetDecimal", StringComparison.Ordinal),
            "decimal must not be read as a native decimal — that is the TEXT path");
    }

    [TestMethod]
    public void Decimal_HonoursAnExplicitScale()
    {
        var source = GeneratorHarness.Run(Row("[Scale(2)] public decimal Weight { get; set; }")).Only;

        StringAssert.Contains(source, "SqliteValue.FromDecimal(row.Weight, 2)");
        StringAssert.Contains(source, "SqliteValue.ToDecimal(reader.GetInt64(oWeight), 2)");
    }

    [TestMethod]
    public void ScaleOutOfRange_IsRefusedRatherThanDefaulted()
    {
        var high = GeneratorHarness.Run(Row("[Scale(11)] public decimal Price { get; set; }"));
        CollectionAssert.Contains(high.Ids, "SQLG013");
        Assert.AreEqual(0, high.Sources.Count(s => s.Key.EndsWith("Repository.g.cs", StringComparison.Ordinal)));
        Assert.IsFalse(high.Sources.Values.Any(s => s.Contains("FromDecimal(row.Price, 4)", StringComparison.Ordinal)));

        var low = GeneratorHarness.Run(Row("[Scale(-1)] public decimal Price { get; set; }"));
        CollectionAssert.Contains(low.Ids, "SQLG013");
        Assert.AreEqual(0, low.Sources.Count(s => s.Key.EndsWith("Repository.g.cs", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Scale_IsPartOfTheSchemaHash()
    {
        string HashOf(int scale)
        {
            var schema = GeneratorHarness.Run(Row($"[Scale({scale})] public decimal Price {{ get; set; }}")).Schema;
            Assert.IsNotNull(schema);
            const string marker = "public const string Hash = \"";
            var start = schema.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
            var end = schema.IndexOf('"', start);
            return schema[start..end];
        }

        Assert.AreNotEqual(HashOf(4), HashOf(2), "a scale-only edit must not take the applied-hash fast path");
    }

    [TestMethod]
    public void Guid_RoundTripsThroughText()
    {
        var source = GeneratorHarness.Run(Row("public Guid Owner { get; set; }")).Only;

        StringAssert.Contains(source, "SqliteValue.FromGuid(row.Owner)");
        StringAssert.Contains(source, "SqliteValue.ToGuid(reader.GetString(oOwner))");
    }

    /// <summary>
    /// A Guid key means the method signature is a Guid while the column is TEXT. If the cursor did
    /// not convert, paging would bind a Guid against a TEXT column and silently match nothing.
    /// </summary>
    [TestMethod]
    public void GuidKey_ConvertsInSignatureAndCursor()
    {
        var source = GeneratorHarness.Run("""
            using System;
            using Schipper.Io.Sqlite.Model;
            namespace Test;

            [Table("Things")]
            public sealed class ThingRow : AuditedRow
            {
                [Key] public Guid Id { get; set; }
                public string Name { get; set; } = "";
            }
            """).Only;

        StringAssert.Contains(source, "IKeyedRepository<ThingRow, System.Guid>");
        StringAssert.Contains(source, "GetAsync(SqliteSession session, System.Guid id");
        StringAssert.Contains(source, "System.Guid? after = null");
        StringAssert.Contains(source, "after is { } __after ? (object)SqliteValue.FromGuid(__after) : null");
    }

    [TestMethod]
    public void Instants_AreTicks_NotText()
    {
        var source = GeneratorHarness.Run(Row("public DateTimeOffset At { get; set; }")).Only;

        StringAssert.Contains(source, "SqliteValue.FromDateTimeOffset(row.At)");
        StringAssert.Contains(source, "SqliteValue.ToDateTimeOffset(reader.GetInt64(oAt))");
    }

    [TestMethod]
    public void Enum_IsStoredAsItsUnderlyingInteger()
    {
        var source = GeneratorHarness.Run(Row("public Colour Shade { get; set; }")).Only;

        StringAssert.Contains(source, "(long)row.Shade");
        StringAssert.Contains(source, "(Test.Colour)reader.GetInt64(oShade)");
    }

    [TestMethod]
    public void ByteArray_IsReadAsABlob()
    {
        var source = GeneratorHarness.Run(Row("public byte[] Payload { get; set; } = [];")).Only;

        StringAssert.Contains(source, "reader.GetFieldValue<byte[]>(oPayload)");
    }

    [TestMethod]
    public void NarrowIntegrals_WidenToInt64()
    {
        var source = GeneratorHarness.Run(Row("public byte Tiny { get; set; }")).Only;

        StringAssert.Contains(source, "(long)row.Tiny");
        StringAssert.Contains(source, "(byte)reader.GetInt64(oTiny)");
    }

    /// <summary>
    /// Nullable value types need the conversion applied to the unwrapped value, which the old
    /// <c>(object?)row.X ?? DBNull.Value</c> form could not express.
    /// </summary>
    [TestMethod]
    public void NullableConvertedTypes_ConvertOnlyWhenPresent()
    {
        var source = GeneratorHarness.Run(Row("public Guid? Owner { get; set; }")).Only;

        StringAssert.Contains(source, "row.Owner is { } __owner ? (object)SqliteValue.FromGuid(__owner) : DBNull.Value");
        StringAssert.Contains(source, "reader.IsDBNull(oOwner) ? null : SqliteValue.ToGuid(reader.GetString(oOwner))");
    }

    /// <summary>
    /// SQLite's INTEGER is 64-bit *signed*. A ulong above long.MaxValue would round-trip negative and
    /// compare wrongly — silently, and only for large values.
    /// </summary>
    [TestMethod]
    public void UnsignedLong_IsRefused()
    {
        var result = GeneratorHarness.Run(Row("public ulong Counter { get; set; }"));

        CollectionAssert.Contains(result.Ids, "SQLG007");
    }

    [TestMethod]
    public void FloatingPointKey_IsRefused()
    {
        var result = GeneratorHarness.Run("""
            using Schipper.Io.Sqlite.Model;
            namespace Test;

            [Table("Things")]
            public sealed class ThingRow : AuditedRow
            {
                [Key] public double Id { get; set; }
                public string Name { get; set; } = "";
            }
            """);

        CollectionAssert.Contains(result.Ids, "SQLG008");
    }

    [TestMethod]
    public void GenuinelyUnmappedType_IsStillRefused()
    {
        var result = GeneratorHarness.Run(Row("public Uri? Link { get; set; }"));

        CollectionAssert.Contains(result.Ids, "SQLG004");
    }

    /// <summary>Every mapped type together, compiling — the check a per-type assertion cannot make.</summary>
    [TestMethod]
    public void EveryMappedType_GeneratesCompilingCode()
    {
        var result = GeneratorHarness.Run(Row("""
            public char Grade { get; set; }
                public bool Active { get; set; }
                public byte Tiny { get; set; }
                public sbyte Signed { get; set; }
                public short Small { get; set; }
                public ushort USmall { get; set; }
                public int Medium { get; set; }
                public uint UMedium { get; set; }
                public long Large { get; set; }
                public float Approximate { get; set; }
                public double Precise { get; set; }
                public decimal Price { get; set; }
                public Guid Owner { get; set; }
                public DateTime Recorded { get; set; }
                public DateTimeOffset Observed { get; set; }
                public DateOnly Day { get; set; }
                public TimeOnly Hour { get; set; }
                public TimeSpan Duration { get; set; }
                public Colour Shade { get; set; }
                public byte[] Payload { get; set; } = [];
                public Guid? MaybeOwner { get; set; }
                public decimal? MaybePrice { get; set; }
                public Colour? MaybeShade { get; set; }
                public string? MaybeText { get; set; }
            """));

        Assert.AreEqual(0, result.GeneratorDiagnostics.Length,
            string.Join("; ", result.GeneratorDiagnostics.Select(d => d.ToString())));

        Assert.AreEqual(0, result.CompileErrors.Length,
            string.Join("; ", result.CompileErrors.Select(d => d.ToString())));
    }
}
