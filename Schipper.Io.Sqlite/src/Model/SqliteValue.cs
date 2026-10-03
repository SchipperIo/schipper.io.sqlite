namespace Schipper.Io.Sqlite.Model;

/// <summary>
/// The encodings the generated code uses to move CLR values in and out of SQLite's five storage
/// classes (NULL, INTEGER, REAL, TEXT, BLOB). Everything beyond those five is a mapping decision, and
/// this is where each decision is made once and written down.
///
/// These are public because a hand-written query has to agree with generated code about how a column
/// is encoded. Reaching for <c>DateTimeOffset.Ticks</c> directly in one place and
/// <see cref="FromDateTimeOffset"/> in another is how two halves of a codebase come to disagree about
/// what a number means.
/// </summary>
public static class SqliteValue
{
    /// <summary>Powers of ten, for the scaled-integer decimal encoding. Index is the scale.</summary>
    private static readonly decimal[] Pow10 =
    [
        1m, 10m, 100m, 1_000m, 10_000m, 100_000m, 1_000_000m, 10_000_000m,
        100_000_000m, 1_000_000_000m, 10_000_000_000m,
    ];

    /// <summary>The scale used when a <c>decimal</c> property does not specify one.</summary>
    /// <remarks>
    /// Four digits covers money everywhere that matters, including the four-decimal unit prices this
    /// library was extracted from, and leaves room for about 9.2 x 10^14 in the integer part.
    /// </remarks>
    public const int DefaultScale = 4;

    /// <summary>Largest scale representable, bounded by <c>long</c> rather than by <c>decimal</c>.</summary>
    public const int MaxScale = 10;

    // --- decimal -> INTEGER, scaled ------------------------------------------------------------

    /// <summary>
    /// Encodes a decimal as a scaled integer. <b>Never REAL and never TEXT.</b>
    ///
    /// TEXT is the driver's own default, not something EF invented, and it compares
    /// lexicographically — which is how <c>"9" &gt; "10"</c> and a reorder-point check starts
    /// answering plausibly and wrongly. REAL cannot represent 0.1 exactly and would drift.
    ///
    /// Throws <see cref="OverflowException"/> rather than truncating when the scaled value will not
    /// fit a <c>long</c>. Loud beats lossy.
    /// </summary>
    public static long FromDecimal(decimal value, int scale = DefaultScale) =>
        checked((long)decimal.Round(value * Pow10[scale], 0, MidpointRounding.AwayFromZero));

    /// <summary>Decodes a scaled integer back to a decimal. Exact, because both sides are integers.</summary>
    public static decimal ToDecimal(long value, int scale = DefaultScale) => value / Pow10[scale];

    // --- Guid -> TEXT --------------------------------------------------------------------------

    /// <summary>
    /// Encodes a Guid as its canonical lowercase 8-4-4-4-12 form.
    ///
    /// BLOB would be 16 bytes rather than 36 and give smaller indexes. TEXT is chosen deliberately:
    /// a database you can read at a <c>sqlite3</c> prompt is worth more here than the bytes, and
    /// UUIDv7 keys sort chronologically as text, which is what makes keyset pagination work.
    /// </summary>
    public static string FromGuid(Guid value) => value.ToString("d");

    /// <inheritdoc cref="FromGuid"/>
    public static Guid ToGuid(string value) => Guid.Parse(value);

    // --- instants -> INTEGER ticks -------------------------------------------------------------

    /// <summary>
    /// Encodes an instant as UTC ticks.
    ///
    /// <b>The offset is not preserved.</b> That is the opinion: a value that carries both a moment
    /// and a viewpoint invites code that compares two of them and is quietly wrong. If the original
    /// offset matters, store it as its own column, where it is visible.
    ///
    /// TEXT — the driver's default — cannot be translated in a range filter and SQLite will not
    /// <c>ORDER BY</c> it usefully.
    /// </summary>
    public static long FromDateTimeOffset(DateTimeOffset value) => value.UtcTicks;

    /// <summary>Decodes UTC ticks. Always returns a UTC instant, offset zero.</summary>
    public static DateTimeOffset ToDateTimeOffset(long ticks) => new(ticks, TimeSpan.Zero);

    /// <summary>
    /// Encodes a DateTime as UTC ticks, converting first.
    ///
    /// A <c>DateTime</c> with <see cref="DateTimeKind.Unspecified"/> is treated as UTC rather than as
    /// local: guessing local makes the stored value depend on which machine wrote it, which is a bug
    /// that only appears after deployment. Prefer <see cref="DateTimeOffset"/>.
    /// </summary>
    public static long FromDateTime(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value.Ticks,
        DateTimeKind.Local => value.ToUniversalTime().Ticks,
        _ => value.Ticks,
    };

    /// <summary>Decodes UTC ticks. The returned DateTime is always <see cref="DateTimeKind.Utc"/>.</summary>
    public static DateTime ToDateTime(long ticks) => new(ticks, DateTimeKind.Utc);

    // --- calendar and duration -> INTEGER ------------------------------------------------------

    /// <summary>Encodes a date as its day number, so date arithmetic and ordering are integer work.</summary>
    public static long FromDateOnly(DateOnly value) => value.DayNumber;

    /// <inheritdoc cref="FromDateOnly"/>
    public static DateOnly ToDateOnly(long dayNumber) => DateOnly.FromDayNumber((int)dayNumber);

    /// <summary>Encodes a time of day as ticks since midnight.</summary>
    public static long FromTimeOnly(TimeOnly value) => value.Ticks;

    /// <inheritdoc cref="FromTimeOnly"/>
    public static TimeOnly ToTimeOnly(long ticks) => new(ticks);

    /// <summary>Encodes a duration as ticks.</summary>
    public static long FromTimeSpan(TimeSpan value) => value.Ticks;

    /// <inheritdoc cref="FromTimeSpan"/>
    public static TimeSpan ToTimeSpan(long ticks) => new(ticks);

    // --- char -> TEXT --------------------------------------------------------------------------

    /// <summary>Encodes a char as a one-character string; SQLite has no character type.</summary>
    public static string FromChar(char value) => value.ToString();

    /// <inheritdoc cref="FromChar"/>
    public static char ToChar(string value) => value.Length > 0
        ? value[0]
        : throw new FormatException("Cannot read a char from an empty TEXT value.");

    /// <summary>
    /// The value that may be passed to <c>AddWithValue</c>. Rejects <c>decimal</c>,
    /// <c>DateTimeOffset</c>, <c>DateTime</c>, <c>Guid</c> and the other types this class encodes,
    /// because the driver stores those as TEXT — which is how <c>"9" &gt; "10"</c> and a Guid cursor
    /// silently matches nothing. Convert through this type first.
    /// </summary>
    public static object Parameter(object? value) => value switch
    {
        null => DBNull.Value,
        decimal => throw CannotBind("decimal", nameof(FromDecimal)),
        DateTimeOffset => throw CannotBind("DateTimeOffset", nameof(FromDateTimeOffset)),
        DateTime => throw CannotBind("DateTime", nameof(FromDateTime)),
        Guid => throw CannotBind("Guid", nameof(FromGuid)),
        DateOnly => throw CannotBind("DateOnly", nameof(FromDateOnly)),
        TimeOnly => throw CannotBind("TimeOnly", nameof(FromTimeOnly)),
        TimeSpan => throw CannotBind("TimeSpan", nameof(FromTimeSpan)),
        _ => value,
    };

    private static ArgumentException CannotBind(string type, string method) =>
        new(
            $"{type} cannot be bound directly: Microsoft.Data.Sqlite stores it as TEXT, which compares " +
            $"lexicographically and is the mapping this library exists to prevent. Convert with " +
            $"SqliteValue.{method} first.");
}

/// <summary>
/// Sets the number of decimal digits a <c>decimal</c> column preserves. Defaults to
/// <see cref="SqliteValue.DefaultScale"/>.
/// </summary>
/// <remarks>
/// Changing this on a column that already holds data reinterprets every stored value — every row
/// silently changes magnitude by a power of ten. Treat it as part of the schema, not as a display
/// setting.
/// </remarks>
[AttributeUsage(AttributeTargets.Property)]
public sealed class ScaleAttribute(int digits) : Attribute
{
    /// <summary>Decimal digits preserved, 0 to <see cref="SqliteValue.MaxScale"/>.</summary>
    public int Digits { get; } = digits;
}
