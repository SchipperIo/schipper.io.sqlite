namespace Schipper.Io.Sqlite.Generator.Tests;

/// <summary>Row declarations the SQL tests assert against, kept in one place so they stay comparable.</summary>
internal static class Sample
{
    /// <summary>
    /// A single-key table with both flavours of nullable — a reference type and a
    /// <c>Nullable&lt;T&gt;</c> — because they take different paths through the generator and only
    /// one of them was right the first time.
    /// </summary>
    public const string Widget = """
        using Schipper.Io.Sqlite.Model;
        namespace Test;

        [Table("Widgets")]
        public sealed class WidgetRow : AuditedRow
        {
            [Key] public string Id { get; set; } = "";
            public string Name { get; set; } = "";
            public long Price { get; set; }
            public string? Note { get; set; }
            public long? RetiredAt { get; set; }
        }
        """;

    /// <summary>
    /// A pure join row: <b>every</b> column belongs to the key, so there is nothing left for an
    /// UPDATE or an ON CONFLICT DO UPDATE to assign.
    ///
    /// This shape is ordinary — role membership, tags, any many-to-many with no payload — and it
    /// emitted <c>SET , Modified = ...</c>, which is a SQLite syntax error raised at the first write
    /// rather than at build time. The generated code compiled perfectly.
    /// </summary>
    public const string Membership = """
        using Schipper.Io.Sqlite.Model;
        namespace Test;

        [Table("Memberships")]
        public sealed class MembershipRow : AuditedRow
        {
            [Key] public string UserId { get; set; } = "";
            [Key] public string RoleId { get; set; } = "";
        }
        """;

    /// <summary>A composite key, which has to flow through GetAsync, WHERE, ON CONFLICT and DELETE.</summary>
    public const string WidgetTag = """
        using Schipper.Io.Sqlite.Model;
        namespace Test;

        [Table("WidgetTags")]
        public sealed class WidgetTagRow : AuditedRow
        {
            [Key] public string WidgetId { get; set; } = "";
            [Key] public string TagId { get; set; } = "";
            public long AssignedAt { get; set; }
        }
        """;
}
