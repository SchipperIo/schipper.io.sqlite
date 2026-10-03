using Schipper.Io.Sqlite.Model;

namespace Schipper.Io.Sqlite.Bench;

// Three shapes, chosen because they are the three the real schema is made of:
//
//   Widgets          a plain table with a mix of nullable and non-nullable columns
//   Parts            many-to-one  — many parts belong to one widget
//   WidgetTags       many-to-many — a join table with a COMPOSITE key
//
// The join table is the interesting one for the generator: a composite [Key] has to flow through
// GetAsync's parameter list, the WHERE predicate, ON CONFLICT, and DeleteAsync.

/// <summary>Plain table. Nullable columns here are what exercise the upsert's COALESCE path.</summary>
[Table("Widgets")]
[TableIndex("Category", "Id")]
public sealed class WidgetRow : AuditedRow
{
    [Key] public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public long Price { get; set; }
    public long Quantity { get; set; }

    /// <summary>Nullable, so upsert must preserve an existing value when this is null.</summary>
    public string? Note { get; set; }

    /// <summary>Nullable value type — the other half of the nullability rule.</summary>
    public long? RetiredAt { get; set; }

    public long UpdatedAt { get; set; }
}

/// <summary>Many-to-one: many parts belong to one widget.</summary>
[Table("Parts")]
[TableIndex("WidgetId", "Id")]
public sealed class PartRow : AuditedRow
{
    [Key] public string Id { get; set; } = "";
    [References("Widgets", Cascade = true)] public string WidgetId { get; set; } = "";
    public string Name { get; set; } = "";
    public long Cost { get; set; }
    public string? Supplier { get; set; }
}

/// <summary>One side of the many-to-many.</summary>
[Table("Tags")]
public sealed class TagRow : AuditedRow
{
    [Key] public string Id { get; set; } = "";
    [Unique] public string Name { get; set; } = "";
}

/// <summary>
/// The join table, with a composite primary key. Nothing but keys, which is the degenerate case
/// worth proving: every generated statement has to work when there are no non-key columns.
/// </summary>
[Table("WidgetTags")]
public sealed class WidgetTagRow : AuditedRow
{
    [Key][References("Widgets", Cascade = true)] public string WidgetId { get; set; } = "";
    [Key][References("Tags", Cascade = true)] public string TagId { get; set; } = "";
    public long AssignedAt { get; set; }
}

/// <summary>
/// One row of every mapped type, so the encodings are exercised by a real database rather than by a
/// string comparison against generated source. A Guid key also proves the cursor converts: the
/// column holds TEXT while QueryAsync's parameter is a Guid.
/// </summary>
[Table("Specimens")]
public sealed class SpecimenRow : AuditedRow
{
    [Key] public Guid Id { get; set; }

    public char Grade { get; set; }
    public bool Active { get; set; }
    public byte Tiny { get; set; }
    public short Small { get; set; }
    public int Medium { get; set; }
    public long Large { get; set; }
    public float Approximate { get; set; }
    public double Precise { get; set; }

    /// <summary>Default scale: four digits, the money case.</summary>
    public decimal Price { get; set; }

    /// <summary>An explicit scale, to prove the attribute is read.</summary>
    [Scale(2)] public decimal Weight { get; set; }

    public DateTimeOffset ObservedAt { get; set; }
    public DateTime RecordedAt { get; set; }
    public DateOnly Day { get; set; }
    public TimeOnly Hour { get; set; }
    public TimeSpan Duration { get; set; }
    public SpecimenState State { get; set; }
    public byte[] Payload { get; set; } = [];

    public Guid? Parent { get; set; }
    public decimal? Discount { get; set; }
    public DateTimeOffset? RetiredAt { get; set; }
    public SpecimenState? Previous { get; set; }
}

public enum SpecimenState
{
    Unknown = 0,
    Observed = 1,
    Archived = 2,
}
