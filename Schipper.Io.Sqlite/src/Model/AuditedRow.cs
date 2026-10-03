namespace Schipper.Io.Sqlite.Model;

/// <summary>
/// The four columns every table carries. Declared on a base type rather than generated into each row
/// so they cannot be forgotten, misspelled, or given different semantics per table.
///
/// The generator <b>requires</b> this base, and that requirement is what buys the rest of the
/// library its vocabulary: because every row is known to have a version and an author, generic code
/// can be written over rows at all. Without it, an outbox writer or an audit-trail snapshotter would
/// need either reflection or its own code generation.
///
/// Costs the single inheritance slot. For row types that is close to free — they are data carriers,
/// not domain objects. Where it is not free, implement <see cref="IAuditedRow"/> by hand instead;
/// library APIs constrain on the interface.
/// </summary>
public abstract class AuditedRow : IAuditedRow
{
    /// <summary>
    /// When the row was first written, UTC ticks. Set by Insert and by the insert half of Upsert;
    /// never changed by an update, so it keeps meaning "when this row first existed".
    /// </summary>
    public long Created { get; set; }

    /// <summary>When the row was last written, UTC ticks. Set by every write.</summary>
    public long Modified { get; set; }

    /// <summary>
    /// Who last wrote the row, taken from <see cref="SqliteSession.User"/>.
    /// <see cref="SqliteSession.SystemUser"/> when the session was opened without one.
    /// </summary>
    public string User { get; set; } = SqliteSession.SystemUser;

    /// <summary>
    /// Optimistic concurrency token. The <i>database</i> increments it, so it is monotonic without
    /// depending on a clock or on two writers agreeing about one. Update requires the value that was
    /// read; Upsert enforces it only when non-zero.
    /// </summary>
    public long Version { get; set; }

    /// <summary><see cref="Created"/> as an instant. Always UTC — see the type mapping notes.</summary>
    public DateTimeOffset CreatedUtc => new(Created, TimeSpan.Zero);

    /// <summary><see cref="Modified"/> as an instant. Always UTC.</summary>
    public DateTimeOffset ModifiedUtc => new(Modified, TimeSpan.Zero);
}

/// <summary>
/// The audit contract, separately from <see cref="AuditedRow"/>, so that library and application
/// APIs can constrain on behaviour rather than on inheritance.
/// </summary>
public interface IAuditedRow
{
    /// <inheritdoc cref="AuditedRow.Created"/>
    long Created { get; set; }

    /// <inheritdoc cref="AuditedRow.Modified"/>
    long Modified { get; set; }

    /// <inheritdoc cref="AuditedRow.User"/>
    string User { get; set; }

    /// <inheritdoc cref="AuditedRow.Version"/>
    long Version { get; set; }
}
