; Diagnostic IDs are public API: once a build fails with SQLG002, that ID is what people search for
; and what they suppress. This file is how Roslyn's RS2008 keeps them tracked across releases.
; Move this table into AnalyzerReleases.Shipped.md under `## Release X.Y.Z` when a stable
; version ships — RS2007 rejects prerelease headers such as 0.1.0-alpha.

### New Rules

Rule ID | Category        | Severity | Notes
--------|-----------------|----------|----------------------------------------------------------
SQLG001 | Schipper.Io.Sqlite | Error    | Row type must derive from AuditedRow
SQLG002 | Schipper.Io.Sqlite | Error    | Row type has no [Key] property
SQLG003 | Schipper.Io.Sqlite | Error    | Row type has no mappable properties
SQLG004 | Schipper.Io.Sqlite | Error    | Property type has no SQLite mapping
SQLG005 | Schipper.Io.Sqlite | Error    | Key property is nullable
SQLG006 | Schipper.Io.Sqlite | Error    | Row type is abstract
SQLG007 | Schipper.Io.Sqlite | Error    | ulong has no lossless SQLite mapping
SQLG008 | Schipper.Io.Sqlite | Error    | Key property has an unsuitable type
SQLG009 | Schipper.Io.Sqlite | Error    | Conflicting [DatabaseNamespace] values
SQLG010 | Schipper.Io.Sqlite | Error    | [DatabaseNamespace] is not a valid namespace
SQLG011 | Schipper.Io.Sqlite | Error    | Row type has no public parameterless constructor
SQLG012 | Schipper.Io.Sqlite | Error    | Mapped property setter is not public or internal
SQLG013 | Schipper.Io.Sqlite | Error    | [Scale] is not an integer in 0..10
