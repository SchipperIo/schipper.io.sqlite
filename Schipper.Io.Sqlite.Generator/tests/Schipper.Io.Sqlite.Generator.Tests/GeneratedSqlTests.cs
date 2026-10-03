using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Schipper.Io.Sqlite.Generator.Tests;

/// <summary>
/// Assertions about the SQL that comes out.
///
/// These target the specific clauses that carry a decision, not the whole file — a full snapshot
/// would fail on every comment edit and teach everyone to re-baseline without reading. Each test
/// below corresponds to a rule that would produce a *plausible wrong answer* if it broke, which is
/// the failure mode worth spending a test on.
/// </summary>
[TestClass]
public sealed class GeneratedSqlTests
{
    [TestMethod]
    public void Update_NeverWritesCreated()
    {
        var update = Statement(Generate(Sample.Widget), "UPDATE \"Widgets\" SET");

        StringAssert.Contains(update, "\"Modified\" = $modified");
        Assert.IsFalse(update.Contains("Created", StringComparison.Ordinal),
            $"Created must not appear in the UPDATE SET clause: {update}");
    }

    [TestMethod]
    public void Update_IsVersionChecked_AndLetsTheDatabaseIncrement()
    {
        var update = Statement(Generate(Sample.Widget), "UPDATE \"Widgets\" SET");

        StringAssert.Contains(update, "\"Version\" = \"Version\" + 1");
        StringAssert.Contains(update, "WHERE \"Id\" = $id AND \"Version\" = $expectedVersion");
    }

    /// <summary>
    /// Created must survive the update half of an upsert too. It is absent from DO UPDATE rather
    /// than pinned to its old value, which is the only way it can mean "when this row first existed"
    /// after an arbitrary number of upserts.
    /// </summary>
    [TestMethod]
    public void Upsert_NeverWritesCreatedOnTheUpdateHalf()
    {
        var upsert = Statement(Generate(Sample.Widget), "INSERT INTO \"Widgets\"", containing: "ON CONFLICT");
        var doUpdate = upsert[upsert.IndexOf("DO UPDATE SET", StringComparison.Ordinal)..];

        Assert.IsFalse(doUpdate.Contains("Created", StringComparison.Ordinal),
            $"Created must not appear after DO UPDATE SET: {doUpdate}");
    }

    /// <summary>
    /// The rule the whole generator exists for: C# nullability decides which columns an upsert may
    /// leave alone. Both flavours of nullable have to behave the same.
    /// </summary>
    [TestMethod]
    public void Upsert_CoalescesNullableColumnsOnly()
    {
        var upsert = Statement(Generate(Sample.Widget), "INSERT INTO \"Widgets\"", containing: "ON CONFLICT");

        StringAssert.Contains(upsert, "\"Note\" = COALESCE(excluded.\"Note\", \"Widgets\".\"Note\")");
        StringAssert.Contains(upsert, "\"RetiredAt\" = COALESCE(excluded.\"RetiredAt\", \"Widgets\".\"RetiredAt\")");
        StringAssert.Contains(upsert, "\"Name\" = excluded.\"Name\"");
        StringAssert.Contains(upsert, "\"Price\" = excluded.\"Price\"");
    }

    /// <summary>
    /// Version 0 skips the check, so seeding and webhook replay keep make-it-so semantics; any
    /// supplied version is enforced. RETURNING is both the reported version and the conflict signal,
    /// because a DO UPDATE whose WHERE fails updates nothing and raises no error.
    /// </summary>
    [TestMethod]
    public void Upsert_ChecksVersionOnlyWhenOneWasSupplied()
    {
        var upsert = Statement(Generate(Sample.Widget), "INSERT INTO \"Widgets\"", containing: "ON CONFLICT");

        StringAssert.Contains(upsert, "WHERE $expectedVersion = 0 OR \"Widgets\".\"Version\" = $expectedVersion");
        StringAssert.Contains(upsert, "RETURNING \"Version\"");
    }

    [TestMethod]
    public void Query_OrdersByTheKey_SoIndexesMustEndWithIt()
    {
        var source = Generate(Sample.Widget);

        // The ORDER BY is assembled by SqlFilter.Compose; what the generator fixes is which column.
        StringAssert.Contains(source, "\"\\\"Id\\\" DESC\", fetch)");
    }

    [TestMethod]
    public void Query_RequiresALimit_AndReturnsAPage()
    {
        var source = Generate(Sample.Widget);

        StringAssert.Contains(source, "public async Task<QueryPage<WidgetRow>> QueryAsync(");
        StringAssert.Contains(source, "int limit,");
        StringAssert.Contains(source, "var hasMore = results.Count > limit;");
        Assert.IsFalse(source.Contains("int limit = 100", StringComparison.Ordinal),
            "A default page size is a silent truncation.");
    }

    [TestMethod]
    public void CompositeKey_QueryHasNoSingleColumnCursor()
    {
        var source = Generate(Sample.WidgetTag);
        var body = source[source.IndexOf("QueryAsync(", StringComparison.Ordinal)..];
        body = body[..body.IndexOf("CountAsync(", StringComparison.Ordinal)];

        StringAssert.Contains(body, "\"\\\"WidgetId\\\" DESC, \\\"TagId\\\" DESC\"");
        Assert.IsFalse(body.Contains("after", StringComparison.Ordinal),
            "A composite key has no single-value cursor; StreamAsync walks the whole match.");
    }

    [TestMethod]
    public void CompositeKey_FlowsThroughEveryStatement()
    {
        var source = Generate(Sample.WidgetTag);

        StringAssert.Contains(source, "WHERE \\\"WidgetId\\\" = $widgetId AND \\\"TagId\\\" = $tagId");
        StringAssert.Contains(source, "ON CONFLICT (\\\"WidgetId\\\", \\\"TagId\\\")");
        StringAssert.Contains(source, "DELETE FROM \\\"WidgetTags\\\" WHERE \\\"WidgetId\\\" = $widgetId AND \\\"TagId\\\" = $tagId");
    }

    [TestMethod]
    public void SingleKeyTable_ImplementsTheKeyedContract()
    {
        StringAssert.Contains(Generate(Sample.Widget), "IKeyedRepository<WidgetRow, string>");
    }

    /// <summary>A two-part key cannot be one TKey, so those tables get the key-free contract only.</summary>
    [TestMethod]
    public void CompositeKeyTable_ImplementsOnlyTheKeyFreeContract()
    {
        var source = Generate(Sample.WidgetTag);

        StringAssert.Contains(source, ": IRepository<WidgetTagRow>");
        Assert.IsFalse(source.Contains("IKeyedRepository", StringComparison.Ordinal));
    }

    [TestMethod]
    public void AuditColumnsAreAppendedToEveryTable()
    {
        StringAssert.Contains(Generate(Sample.Widget), "\\\"Created\\\", \\\"Modified\\\", \\\"User\\\", \\\"Version\\\"");
    }

    /// <summary>
    /// A row whose every column is part of the key contributes no assignments, so the audit ones
    /// have to be the whole SET clause rather than something appended to it.
    /// </summary>
    [TestMethod]
    public void AllKeyRow_WritesOnlyTheAuditColumns()
    {
        var source = Generate(Sample.Membership);

        StringAssert.Contains(
            Statement(source, "UPDATE \"Memberships\" SET"),
            "SET \"Modified\" = $modified, \"User\" = $user, \"Version\" = \"Version\" + 1");

        StringAssert.Contains(
            Statement(source, "INSERT INTO \"Memberships\"", containing: "ON CONFLICT"),
            "DO UPDATE SET \"Modified\" = excluded.\"Modified\"");
    }

    /// <summary>
    /// The assertion that actually catches this class of defect. Generated code that does not
    /// compile is caught by <c>GeneratedSource_Compiles</c>; generated <b>SQL</b> that does not
    /// parse is invisible to the compiler and shows up at the first write instead, so it is put in
    /// front of SQLite's own parser here.
    /// </summary>
    [TestMethod]
    public void AllKeyRow_StatementsParse()
    {
        var source = Generate("""
            using Schipper.Io.Sqlite.Model;
            namespace Test;

            [Table("Memberships")]
            public sealed class MembershipRow : AuditedRow
            {
                [Key] public string UserId { get; set; } = "";
                [Key] public string RoleId { get; set; } = "";
                public string From { get; set; } = "";
                public string To { get; set; } = "";
                public string Order { get; set; } = "";
            }
            """);

        using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        connection.Open();

        using (var create = connection.CreateCommand())
        {
            create.CommandText = """
                CREATE TABLE "Memberships" (
                    "UserId" TEXT NOT NULL,
                    "RoleId" TEXT NOT NULL,
                    "From" TEXT NOT NULL,
                    "To" TEXT NOT NULL,
                    "Order" TEXT NOT NULL,
                    "Created" INTEGER NOT NULL,
                    "Modified" INTEGER NOT NULL,
                    "User" TEXT NOT NULL,
                    "Version" INTEGER NOT NULL,
                    PRIMARY KEY ("UserId", "RoleId")
                );
                """;

            create.ExecuteNonQuery();
        }

        foreach (var sql in new[]
        {
            Statement(source, "SELECT \"UserId\"", containing: "WHERE"),
            Statement(source, "INSERT INTO \"Memberships\"", containing: "VALUES"),
            Statement(source, "UPDATE \"Memberships\" SET"),
            Statement(source, "INSERT INTO \"Memberships\"", containing: "ON CONFLICT"),
            Statement(source, "DELETE FROM \"Memberships\""),
        })
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Prepare();
        }
    }

    /// <summary>
    /// The count takes the same filter as the page and adds no ordering.
    ///
    /// Composed by hand rather than through <c>SqlFilter.Compose</c>, which always appends the
    /// keyset <c>ORDER BY</c> — sorting rows nobody will look at to produce a single number.
    /// </summary>
    [TestMethod]
    public void Count_TakesTheSameFilter_AndDoesNotOrder()
    {
        var source = Generate(Sample.Widget);

        StringAssert.Contains(source, "public async Task<int> CountAsync(");
        StringAssert.Contains(source, "\"SELECT COUNT(*) FROM \\\"Widgets\\\"\"");
        StringAssert.Contains(source, "SqlFilter? filter = null");

        var body = source[source.IndexOf("CountAsync(", StringComparison.Ordinal)..];
        body = body[..body.IndexOf("StreamAsync", StringComparison.Ordinal)];

        // Comments stripped: the generated code explains *why* it does not order, and matching that
        // sentence would make this pass for the wrong reason — or fail for saying so.
        var code = string.Join(
            "\n",
            body.Split('\n').Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

        Assert.IsFalse(code.Contains("ORDER BY", StringComparison.Ordinal), "A COUNT has nothing to order.");
        Assert.IsFalse(code.Contains("LIMIT", StringComparison.Ordinal), "A limited COUNT counts the limit.");
    }

    /// <summary>Ordinals resolved by name; positional reads are how same-typed columns get transposed.</summary>
    [TestMethod]
    public void MapResolvesOrdinalsByName()
    {
        StringAssert.Contains(Generate(Sample.Widget), "reader.GetOrdinal(\"Name\")");
    }

    [TestMethod]
    public void Database_LivesInTheRowNamespace_ByDefault()
    {
        var schema = GeneratorHarness.Run(Sample.Widget).Schema;

        Assert.IsNotNull(schema);
        StringAssert.Contains(schema, "namespace Test;");
        StringAssert.Contains(schema, "public static class Database");
    }

    [TestMethod]
    public void Database_HonoursDatabaseNamespaceAttribute()
    {
        var schema = GeneratorHarness.Run("""
            using Schipper.Io.Sqlite.Model;
            namespace Test;

            [Table("Widgets")]
            [DatabaseNamespace("Acme.Store")]
            public sealed class WidgetRow : AuditedRow
            {
                [Key] public string Id { get; set; } = "";
                public string Name { get; set; } = "";
            }
            """).Schema;

        Assert.IsNotNull(schema);
        StringAssert.Contains(schema, "namespace Acme.Store;");
        StringAssert.Contains(schema, "public static class Database");
    }

    private static string Generate(string source) => GeneratorHarness.Run(source).Only;

    /// <summary>
    /// Pulls one SQL literal out of the generated source, up to the closing quote.
    ///
    /// <paramref name="containing"/> disambiguates: the insert and the upsert both begin
    /// <c>INSERT INTO Widgets (</c>, so matching on the prefix alone silently returns the wrong one.
    /// </summary>
    private static string Statement(string generated, string startsWith, string? containing = null)
    {
        var encoded = startsWith.Replace("\\", "\\\\").Replace("\"", "\\\"");
        var index = -1;

        while ((index = generated.IndexOf(encoded, index + 1, StringComparison.Ordinal)) >= 0)
        {
            var end = index;
            var closed = -1;

            while (end < generated.Length)
            {
                if (generated[end] == '\\')
                {
                    end += 2;
                    continue;
                }

                if (generated[end] == '"')
                {
                    closed = end;
                    break;
                }

                end++;
            }

            if (closed < 0)
            {
                continue;
            }

            var candidate = generated[index..closed].Replace("\\\"", "\"").Replace("\\\\", "\\");

            if (containing is null || candidate.Contains(containing, StringComparison.Ordinal))
            {
                return candidate;
            }
        }

        Assert.Fail($"No statement starting with '{startsWith}'" +
                    (containing is null ? "" : $" and containing '{containing}'") + " in generated source.");
        return string.Empty;
    }
}
