# Schipper.Io.Sqlite

An opinionated bridge from .NET to SQLite. You declare a row type. The package generates the repository and the schema. There is no mapping file, no runtime SQL builder, and no `IQueryable`.

The library is Native AOT friendly: the generator runs at compile time, and the runtime assembly does not reflect over your rows.

## Guides

| Guide | What it covers |
| --- | --- |
| [Getting started](getting-started.md) | A row type, a session, and the first write |
| [Rows and repositories](rows-and-repositories.md) | Attributes, generated methods, version checks |
| [Queries](queries.md) | `SqlFilter`, keyset pages, `StreamAsync`, ad-hoc SQL |
| [Schema](schema.md) | `Database.ApplyAsync`, renames, rebuilds, diagnostics |
| [Values and sessions](values-and-sessions.md) | Type mapping, `SqliteValue`, transactions, metrics |

## Namespaces

| Namespace | Types |
| --- | --- |
| `Schipper.Io.Sqlite` | `SqliteDb`, `SqliteSession`, `SqliteConcurrencyException`, `SqliteMetrics` |
| `Schipper.Io.Sqlite.Model` | `AuditedRow`, `IAuditedRow`, `IRepository<>`, `IKeyedRepository<,>`, table attributes, `SqliteValue`, `ScaleAttribute` |
| `Schipper.Io.Sqlite.Query` | `SqlFilter`, `QueryPage<>` |
| `Schipper.Io.Sqlite.Schema` | `SchemaApplier`, `SchemaModel`, `SchemaUpdate`, `SchemaChange`, `SqliteSchemaException` |

The generator emits `Database` and one `{Row}Repository` per `[Table]` type, in your code's namespace. Those types are the ones application code calls.

## Package

```xml
<PackageReference Include="Schipper.Io.Sqlite" Version="0.1.0-dev" />
```

One package contains the runtime assembly and the Roslyn generator (`analyzers/dotnet/cs`). A consumer does not reference the generator project.

Dependencies: `Microsoft.Data.Sqlite` 10.0.10, with `SQLitePCLRaw.bundle_e_sqlite3` and `SQLitePCLRaw.lib.e_sqlite3` pinned to 2.1.12. Target framework: `net10.0`.

The API is at `0.1.0-dev` and will move before a stable release.
