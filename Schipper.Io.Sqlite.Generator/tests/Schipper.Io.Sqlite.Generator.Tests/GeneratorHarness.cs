using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Schipper.Io.Sqlite.Generator.Emit;
using Schipper.Io.Sqlite.Model;

namespace Schipper.Io.Sqlite.Generator.Tests;

/// <summary>
/// Runs the generator over source compiled in memory.
///
/// This is the only way to test the failure paths at all: a row type that is *supposed* to produce
/// SQLG002 cannot live in a project that has to compile. It also means the assertions are about the
/// generator's actual output rather than about a checked-in copy of it.
/// </summary>
internal static class GeneratorHarness
{
    /// <summary>Everything the row types under test need to bind against.</summary>
    private static readonly MetadataReference[] References =
    [
        MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        MetadataReference.CreateFromFile(Assembly.Load("System.Runtime").Location),
        MetadataReference.CreateFromFile(typeof(AuditedRow).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(Microsoft.Data.Sqlite.SqliteConnection).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Data.Common.DbConnection).Assembly.Location),

        // SqliteCommand derives from DbCommand, which derives from Component. Omitting this produced
        // 31 CS0012s the first time the "generated source compiles" test ran — which is the test
        // working: a reference the generated code needs is exactly what it is there to notice.
        MetadataReference.CreateFromFile(typeof(System.ComponentModel.Component).Assembly.Location),
    ];

    public static Result Run(string source)
    {
        var compilation = CSharpCompilation.Create(
            "GeneratorTestAssembly",
            [CSharpSyntaxTree.ParseText(source)],
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        var driver = CSharpGeneratorDriver
            .Create(new RepositoryGenerator())
            .RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);

        var run = driver.GetRunResult();

        // Diagnostics from the generator itself, plus anything wrong with what it emitted. The
        // second half matters: generated code that does not compile is the failure mode a
        // string-comparison test would miss entirely.
        var emitted = output.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToImmutableArray();

        return new Result(
            run.Diagnostics,
            emitted,
            run.GeneratedTrees.ToDictionary(
                t => Path.GetFileName(t.FilePath),
                t => t.GetText().ToString()));
    }

    internal sealed record Result(
        ImmutableArray<Diagnostic> GeneratorDiagnostics,
        ImmutableArray<Diagnostic> CompileErrors,
        Dictionary<string, string> Sources)
    {
        public string[] Ids => [.. GeneratorDiagnostics.Select(d => d.Id)];

        /// <summary>
        /// The single generated repository. Named <c>Only</c> because a test that declares one row
        /// type expects one repository; the schema class is emitted alongside it for the whole
        /// compilation and is reached through <see cref="Schema"/>.
        /// </summary>
        public string Only
        {
            get
            {
                var repositories = Sources
                    .Where(s => s.Key.EndsWith("Repository.g.cs", StringComparison.Ordinal))
                    .ToList();

                return repositories.Count == 1
                    ? repositories[0].Value
                    : throw new InvalidOperationException(
                        $"Expected exactly one generated repository, got {repositories.Count}: " +
                        string.Join(", ", Sources.Keys));
            }
        }

        /// <summary>The generated <c>Database</c> type, or null when no row type was valid.</summary>
        public string? Schema =>
            Sources.TryGetValue("Database.g.cs", out var source) ? source : null;
    }
}
