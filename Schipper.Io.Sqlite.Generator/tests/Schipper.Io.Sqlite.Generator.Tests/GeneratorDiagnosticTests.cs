using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Schipper.Io.Sqlite.Generator.Tests;

/// <summary>
/// The refusal paths. Each of these produced *nothing at all* before — no repository, no error —
/// which surfaced as a missing type and a confusing compile failure somewhere else entirely.
/// </summary>
[TestClass]
public sealed class GeneratorDiagnosticTests
{
    [TestMethod]
    public void RowTypeWithoutAuditedRow_IsRefused()
    {
        var result = GeneratorHarness.Run("""
            using Schipper.Io.Sqlite.Model;
            namespace Test;

            [Table("Widgets")]
            public sealed class WidgetRow
            {
                [Key] public string Id { get; set; } = "";
                public string Name { get; set; } = "";
            }
            """);

        CollectionAssert.Contains(result.Ids, "SQLG001");
        Assert.AreEqual(0, result.Sources.Count, "no repository should be emitted");
    }

    [TestMethod]
    public void RowTypeWithoutKey_IsRefused()
    {
        var result = GeneratorHarness.Run("""
            using Schipper.Io.Sqlite.Model;
            namespace Test;

            [Table("Widgets")]
            public sealed class WidgetRow : AuditedRow
            {
                public string Id { get; set; } = "";
                public string Name { get; set; } = "";
            }
            """);

        CollectionAssert.Contains(result.Ids, "SQLG002");
        Assert.AreEqual(0, result.Sources.Count);
    }

    [TestMethod]
    public void RowTypeWithNoProperties_IsRefused()
    {
        var result = GeneratorHarness.Run("""
            using Schipper.Io.Sqlite.Model;
            namespace Test;

            [Table("Widgets")]
            public sealed class WidgetRow : AuditedRow;
            """);

        CollectionAssert.Contains(result.Ids, "SQLG003");
    }

    /// <summary>
    /// Before this diagnostic existed an unmapped type fell through to <c>GetString</c>, so it was
    /// silently stored as TEXT and compared lexicographically — the '9' &gt; '10' defect,
    /// reintroduced by accident. Refusing is the whole point; see TypeMappingTests for what IS
    /// mapped.
    /// </summary>
    [TestMethod]
    public void UnmappedPropertyType_IsRefusedRatherThanStoredAsText()
    {
        var result = GeneratorHarness.Run("""
            using System;
            using Schipper.Io.Sqlite.Model;
            namespace Test;

            [Table("Widgets")]
            public sealed class WidgetRow : AuditedRow
            {
                [Key] public string Id { get; set; } = "";
                public System.Net.IPAddress? Address { get; set; }
            }
            """);

        CollectionAssert.Contains(result.Ids, "SQLG004");
    }

    [TestMethod]
    public void NullableKey_IsRefused()
    {
        var result = GeneratorHarness.Run("""
            using Schipper.Io.Sqlite.Model;
            namespace Test;

            [Table("Widgets")]
            public sealed class WidgetRow : AuditedRow
            {
                [Key] public string? Id { get; set; }
                public string Name { get; set; } = "";
            }
            """);

        CollectionAssert.Contains(result.Ids, "SQLG005");
    }

    [TestMethod]
    public void AbstractRowType_IsRefused()
    {
        var result = GeneratorHarness.Run("""
            using Schipper.Io.Sqlite.Model;
            namespace Test;

            [Table("Widgets")]
            public abstract class WidgetRow : AuditedRow
            {
                [Key] public string Id { get; set; } = "";
            }
            """);

        CollectionAssert.Contains(result.Ids, "SQLG006");
    }

    [TestMethod]
    public void ConflictingDatabaseNamespace_IsRefused()
    {
        var result = GeneratorHarness.Run("""
            using Schipper.Io.Sqlite.Model;
            namespace Test;

            [Table("Widgets")]
            [DatabaseNamespace("One")]
            public sealed class WidgetRow : AuditedRow
            {
                [Key] public string Id { get; set; } = "";
                public string Name { get; set; } = "";
            }

            [Table("Parts")]
            [DatabaseNamespace("Two")]
            public sealed class PartRow : AuditedRow
            {
                [Key] public string Id { get; set; } = "";
                public string Name { get; set; } = "";
            }
            """);

        CollectionAssert.Contains(result.Ids, "SQLG009");
        Assert.IsNull(result.Schema);
    }

    [TestMethod]
    public void InvalidDatabaseNamespace_IsRefused()
    {
        var result = GeneratorHarness.Run("""
            using Schipper.Io.Sqlite.Model;
            namespace Test;

            [Table("Widgets")]
            [DatabaseNamespace("1Store")]
            public sealed class WidgetRow : AuditedRow
            {
                [Key] public string Id { get; set; } = "";
                public string Name { get; set; } = "";
            }
            """);

        CollectionAssert.Contains(result.Ids, "SQLG010");
    }

    [TestMethod]
    public void RecordClass_EmitsACompilingRepository()
    {
        var result = GeneratorHarness.Run("""
            using Schipper.Io.Sqlite.Model;
            namespace Test;

            [Table("Widgets")]
            public sealed record WidgetRow : AuditedRow
            {
                [Key] public string Id { get; set; } = "";
                public string Name { get; set; } = "";
            }
            """);

        Assert.AreEqual(0, result.GeneratorDiagnostics.Length,
            string.Join("; ", result.GeneratorDiagnostics.Select(d => d.ToString())));
        Assert.IsTrue(result.Schema is not null && result.Schema.Contains("Widgets", StringComparison.Ordinal));
        _ = result.Only;
        Assert.IsFalse(result.CompileErrors.Any(d =>
            d.Location.GetLineSpan().Path.Contains("Repository.g.cs", StringComparison.Ordinal)
            || d.Location.GetLineSpan().Path.Contains("Database.g.cs", StringComparison.Ordinal)),
            string.Join("; ", result.CompileErrors.Select(d => d.ToString())));
    }

    [TestMethod]
    public void ParameterizedConstructor_IsRefused()
    {
        var result = GeneratorHarness.Run("""
            using Schipper.Io.Sqlite.Model;
            namespace Test;

            [Table("Widgets")]
            public sealed class WidgetRow : AuditedRow
            {
                public WidgetRow(string id) => Id = id;

                [Key] public string Id { get; set; } = "";
                public string Name { get; set; } = "";
            }
            """);

        CollectionAssert.Contains(result.Ids, "SQLG011");
        Assert.AreEqual(0, result.Sources.Count(s => s.Key.EndsWith("Repository.g.cs", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void PositionalRecord_WithoutParameterlessConstructor_IsRefused()
    {
        var result = GeneratorHarness.Run("""
            using Schipper.Io.Sqlite.Model;
            namespace Test;

            [Table("Widgets")]
            public sealed record WidgetRow([property: Key] string Id) : AuditedRow;
            """);

        CollectionAssert.Contains(result.Ids, "SQLG011");
        Assert.AreEqual(0, result.Sources.Count(s => s.Key.EndsWith("Repository.g.cs", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void PrivateSetter_IsRefused()
    {
        var result = GeneratorHarness.Run("""
            using Schipper.Io.Sqlite.Model;
            namespace Test;

            [Table("Widgets")]
            public sealed class WidgetRow : AuditedRow
            {
                [Key] public string Id { get; set; } = "";
                public string Name { get; private set; } = "";
            }
            """);

        CollectionAssert.Contains(result.Ids, "SQLG012");
        Assert.AreEqual(0, result.Sources.Count(s => s.Key.EndsWith("Repository.g.cs", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void InitSetter_Compiles()
    {
        var result = GeneratorHarness.Run("""
            using Schipper.Io.Sqlite.Model;
            namespace Test;

            [Table("Widgets")]
            public sealed class WidgetRow : AuditedRow
            {
                [Key] public string Id { get; init; } = "";
                public string Name { get; init; } = "";
            }
            """);

        Assert.AreEqual(0, result.GeneratorDiagnostics.Length,
            string.Join("; ", result.GeneratorDiagnostics.Select(d => d.ToString())));
        Assert.AreEqual(0, result.CompileErrors.Length,
            string.Join("; ", result.CompileErrors.Select(d => d.ToString())));
    }

    [TestMethod]
    public void CSharpKeywordProperty_Compiles()
    {
        var result = GeneratorHarness.Run("""
            using Schipper.Io.Sqlite.Model;
            namespace Test;

            [Table("Events")]
            public sealed class EventRow : AuditedRow
            {
                [Key] public string Id { get; set; } = "";
                public string @event { get; set; } = "";
            }
            """);

        Assert.AreEqual(0, result.CompileErrors.Length,
            string.Join("; ", result.CompileErrors.Select(d => d.ToString())));
        StringAssert.Contains(result.Only, "row.@event");
    }

    [TestMethod]
    public void ValidRowType_ProducesNoDiagnostics()
    {
        var result = GeneratorHarness.Run(Sample.Widget);

        Assert.AreEqual(0, result.GeneratorDiagnostics.Length,
            string.Join("; ", result.GeneratorDiagnostics.Select(d => d.ToString())));
    }

    /// <summary>
    /// The generated source has to actually compile. This is what catches a metadata-name drift
    /// between the library and the generator, which the compiler cannot catch on its own because the
    /// generator matches attributes by string.
    /// </summary>
    [TestMethod]
    public void GeneratedSource_Compiles()
    {
        var result = GeneratorHarness.Run(Sample.Widget);

        Assert.AreEqual(0, result.CompileErrors.Length,
            string.Join("; ", result.CompileErrors.Select(d => d.ToString())));
    }
}
