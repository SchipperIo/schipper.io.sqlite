using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Schipper.Io.Sqlite.Generator.Emit;

/// <summary>
/// Emits a repository per row type marked <c>[Table("Name")]</c>.
///
/// The row class is the single source of truth. Three things are read off it directly, so none of
/// them can drift from the declaration:
///
///   * the columns          — the public settable properties it declares, in declaration order
///   * the key              — the property or properties marked <c>[Key]</c>
///   * the upsert semantics — <b>C# nullability</b>. A nullable property becomes
///                            <c>COALESCE(excluded.Col, Table.Col)</c>, so a null leaves the stored
///                            value alone; a non-nullable one is assigned unconditionally.
///
/// That last point is the reason this is a generator rather than a template: "update only the
/// non-null fields" is already written down in the type, and re-stating it in SQL by hand for
/// twenty-odd tables is twenty-odd chances to state it wrong.
///
/// The four audit columns come from <c>AuditedRow</c>, which every row type must derive from. They
/// are appended to the column list here rather than discovered, because Roslyn's <c>GetMembers()</c>
/// returns only declared members — so the base's properties are naturally excluded from the scan and
/// cannot be double-counted.
///
/// Generated methods: GetAsync, QueryAsync (filter + keyset cursor), StreamAsync (IAsyncEnumerable),
/// InsertAsync, UpdateAsync/TryUpdateAsync (full replace), UpsertAsync/TryUpsertAsync (non-null
/// only), DeleteAsync.
/// </summary>
[Generator]
public sealed class RepositoryGenerator : IIncrementalGenerator
{
    private const string TableAttribute = "Schipper.Io.Sqlite.Model.TableAttribute";
    private const string KeyAttribute = "Schipper.Io.Sqlite.Model.KeyAttribute";
    private const string AuditedRowType = "Schipper.Io.Sqlite.Model.AuditedRow";
    private const string ScaleAttribute = "Schipper.Io.Sqlite.Model.ScaleAttribute";
    private const string UniqueAttribute = "Schipper.Io.Sqlite.Model.UniqueAttribute";
    private const string IndexAttribute = "Schipper.Io.Sqlite.Model.IndexAttribute";
    private const string TableIndexAttribute = "Schipper.Io.Sqlite.Model.TableIndexAttribute";
    private const string ReferencesAttribute = "Schipper.Io.Sqlite.Model.ReferencesAttribute";
    private const string SqlDefaultAttribute = "Schipper.Io.Sqlite.Model.SqlDefaultAttribute";
    private const string RenameFromAttribute = "Schipper.Io.Sqlite.Model.RenameFromAttribute";
    private const string RebuildAttribute = "Schipper.Io.Sqlite.Model.RebuildAttribute";
    private const string DatabaseNamespaceAttribute = "Schipper.Io.Sqlite.Model.DatabaseNamespaceAttribute";

    private const int DefaultScale = 4;
    private const int MaxScale = 10;

    private static readonly string[] AuditColumns = ["Created", "Modified", "User", "Version"];

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var rows = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                TableAttribute,
                predicate: static (node, _) =>
                node is ClassDeclarationSyntax
                || (node is RecordDeclarationSyntax recordDecl
                    && !recordDecl.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword)),
                transform: static (ctx, _) => Describe(ctx));

        context.RegisterSourceOutput(rows, static (spc, result) =>
        {
            foreach (var diagnostic in result.Diagnostics)
            {
                spc.ReportDiagnostic(diagnostic);
            }

            if (result.Row is { } row)
            {
                spc.AddSource($"{row.TypeName}Repository.g.cs", SourceText.From(Emit(row), Encoding.UTF8));
            }
        });

        // The schema is one type for the whole compilation, so it needs every row type at once.
        // Collect() is the cost of that: this output re-runs whenever any row type changes, which is
        // correct and unavoidable — the hash covers all of them.
        var rootNamespace = context.AnalyzerConfigOptionsProvider.Select(static (options, _) =>
            options.GlobalOptions.TryGetValue("build_property.RootNamespace", out var value) &&
            !string.IsNullOrWhiteSpace(value)
                ? value
                : null);

        context.RegisterSourceOutput(
            rows.Collect().Combine(rootNamespace),
            static (spc, pair) =>
            {
                var models = pair.Left
                    .Select(r => r.Row)
                    .Where(r => r is not null)
                    .Select(r => r!)
                    .ToList();

                if (models.Count == 0)
                {
                    return;
                }

                var specified = models
                    .Where(m => m.DatabaseNamespace is not null)
                    .GroupBy(m => m.DatabaseNamespace, StringComparer.Ordinal)
                    .ToList();

                if (specified.Count > 1)
                {
                    var first = specified[0];
                    var second = specified[1];
                    spc.ReportDiagnostic(Diagnostic.Create(
                        Diagnostics.ConflictingDatabaseNamespace,
                        Location.None,
                        first.First().TypeName,
                        second.First().TypeName,
                        first.Key,
                        second.Key));
                    return;
                }

                // [DatabaseNamespace] wins; otherwise RootNamespace; otherwise the first row type.
                var ns = specified.Count == 1
                    ? specified[0].Key
                    : pair.Right ?? models[0].Namespace;

                spc.AddSource(SchemaClassEmitter.HintName,
                    SourceText.From(SchemaClassEmitter.Emit(ns, models), Encoding.UTF8));
            });
    }

    private static DescribeResult Describe(GeneratorAttributeSyntaxContext context)
    {
        if (context.TargetSymbol is not INamedTypeSymbol type)
        {
            return DescribeResult.Empty;
        }

        var diagnostics = new List<Diagnostic>();
        var location = type.Locations.Length > 0 ? type.Locations[0] : Location.None;

        if (type.IsAbstract)
        {
            diagnostics.Add(Diagnostic.Create(Diagnostics.RowTypeIsAbstract, location, type.Name));
            return new DescribeResult(null, diagnostics);
        }

        if (!HasPublicParameterlessConstructor(type))
        {
            diagnostics.Add(Diagnostic.Create(Diagnostics.NoPublicParameterlessConstructor, location, type.Name));
        }

        if (!DerivesFromAuditedRow(type))
        {
            diagnostics.Add(Diagnostic.Create(Diagnostics.NotAuditedRow, location, type.Name));
            return new DescribeResult(null, diagnostics);
        }

        var arguments = context.Attributes[0].ConstructorArguments;
        var table = arguments.Length > 0 && arguments[0].Value is string name ? name : type.Name;

        var columns = new List<ColumnModel>();

        // Declared members only. The inherited audit properties are appended below, so scanning the
        // base as well would list each of them twice.
        foreach (var member in type.GetMembers().OfType<IPropertySymbol>())
        {
            if (member.DeclaredAccessibility != Accessibility.Public || member.SetMethod is null || member.IsStatic)
            {
                continue;
            }

            var memberLocation = member.Locations.Length > 0 ? member.Locations[0] : location;

            var setterAccess = member.SetMethod.DeclaredAccessibility;
            if (setterAccess is not (Accessibility.Public or Accessibility.Internal))
            {
                diagnostics.Add(Diagnostic.Create(
                    Diagnostics.SetterNotAccessible, memberLocation, type.Name, member.Name));
                continue;
            }

            var isKey = member.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == KeyAttribute);

            // Nullability comes from the annotation for reference types, and from Nullable<T> for
            // value types. This is what drives COALESCE in the upsert.
            var nullable =
                member.NullableAnnotation == NullableAnnotation.Annotated ||
                member.Type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T;

            var underlying = Underlying(member.Type);

            if (TypeMap.IsUnsignedLong(underlying))
            {
                diagnostics.Add(Diagnostic.Create(
                    Diagnostics.UnsignedLong, memberLocation, type.Name, member.Name));
                continue;
            }

            if (!TryScaleOf(member, type, memberLocation, diagnostics, out var scale))
            {
                continue;
            }

            var mapping = TypeMap.Resolve(underlying, scale);

            if (mapping is null)
            {
                diagnostics.Add(Diagnostic.Create(
                    Diagnostics.UnsupportedType, memberLocation,
                    type.Name, member.Name, member.Type.ToDisplayString()));
                continue;
            }

            if (isKey && nullable)
            {
                diagnostics.Add(Diagnostic.Create(
                    Diagnostics.KeyIsNullable, memberLocation, type.Name, member.Name));
                continue;
            }

            if (isKey && mapping.Kind is MapKind.Real or MapKind.Bytes)
            {
                diagnostics.Add(Diagnostic.Create(
                    Diagnostics.KeyTypeUnsuitable, memberLocation,
                    type.Name, member.Name, member.Type.ToDisplayString()));
                continue;
            }

            columns.Add(new ColumnModel(
                member.Name,
                mapping,
                nullable,
                isKey,
                Unique: Has(member, UniqueAttribute),
                Indexed: Has(member, IndexAttribute),
                Default: DefaultOf(member),
                References: ForeignKeyOf(member),
                RenamedFrom: RenamedFrom(member.GetAttributes())));
        }

        if (columns.Count == 0)
        {
            diagnostics.Add(Diagnostic.Create(Diagnostics.NoColumns, location, type.Name));
            return new DescribeResult(null, diagnostics);
        }

        if (!columns.Any(c => c.IsKey))
        {
            diagnostics.Add(Diagnostic.Create(Diagnostics.NoKey, location, type.Name));
            return new DescribeResult(null, diagnostics);
        }

        // Every table carries the same four, from AuditedRow. Appended rather than declared per row
        // type so they cannot be forgotten, misspelled, or given different semantics per table.
        var ticks = new TypeMapping(MapKind.Integer, "long", "INTEGER");
        var text = new TypeMapping(MapKind.String, "string", "TEXT");

        columns.Add(new ColumnModel("Created", ticks, false, false));
        columns.Add(new ColumnModel("Modified", ticks, false, false));
        columns.Add(new ColumnModel("User", text, false, false, Default: "'System'"));
        columns.Add(new ColumnModel("Version", ticks, false, false, Default: "1"));

        var row = new RowModel(
            type.ContainingNamespace.ToDisplayString(),
            type.Name,
            table,
            columns,
            TableIndexesOf(context.Attributes[0].AttributeClass, type),
            RenamedFrom(type.GetAttributes()),
            type.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == RebuildAttribute),
            DatabaseNamespaceOf(type.GetAttributes(), type.Name, location, diagnostics));

        if (diagnostics.Any(static d => d.Id is "SQLG011" or "SQLG012" or "SQLG013"))
        {
            return new DescribeResult(null, diagnostics);
        }

        return new DescribeResult(row, diagnostics);
    }

    private static bool DerivesFromAuditedRow(INamedTypeSymbol type)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.ToDisplayString() == AuditedRowType)
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasPublicParameterlessConstructor(INamedTypeSymbol type) =>
        type.InstanceConstructors.Any(static c =>
            c.Parameters.Length == 0 && c.DeclaredAccessibility == Accessibility.Public);

    /// <summary>
    /// The namespace declared by <c>[DatabaseNamespace]</c>, or null to use RootNamespace.
    /// Invalid values are reported and ignored rather than emitted into a file that will not compile.
    /// </summary>
    private static string? DatabaseNamespaceOf(
        System.Collections.Immutable.ImmutableArray<AttributeData> attributes,
        string typeName,
        Location location,
        List<Diagnostic> diagnostics)
    {
        foreach (var attribute in attributes)
        {
            if (attribute.AttributeClass?.ToDisplayString() != DatabaseNamespaceAttribute)
            {
                continue;
            }

            var shown = attribute.ConstructorArguments.Length > 0
                ? attribute.ConstructorArguments[0].Value?.ToString() ?? ""
                : "";

            if (!IsValidNamespace(shown))
            {
                diagnostics.Add(Diagnostic.Create(
                    Diagnostics.InvalidDatabaseNamespace, location, typeName, shown));
                return null;
            }

            var value = shown;

            return value;
        }

        return null;
    }

    private static bool IsValidNamespace(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        foreach (var part in value.Split('.'))
        {
            if (part.Length == 0)
            {
                return false;
            }

            if (part[0] is not ('_' or (>= 'A' and <= 'Z') or (>= 'a' and <= 'z')))
            {
                return false;
            }

            for (var i = 1; i < part.Length; i++)
            {
                if (part[i] is not ('_' or (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9')))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>The previous name declared by <c>[RenameFrom]</c>, for the schema comparison.</summary>
    private static string? RenamedFrom(System.Collections.Immutable.ImmutableArray<AttributeData> attributes)
    {
        foreach (var attribute in attributes)
        {
            if (attribute.AttributeClass?.ToDisplayString() == RenameFromAttribute &&
                attribute.ConstructorArguments.Length > 0 &&
                attribute.ConstructorArguments[0].Value is string previous)
            {
                return previous;
            }
        }

        return null;
    }

    private static bool Has(IPropertySymbol member, string attribute) =>
        member.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == attribute);

    /// <summary>The literal SQL default declared by <c>[SqlDefault]</c>, emitted verbatim.</summary>
    private static string? DefaultOf(IPropertySymbol member)
    {
        foreach (var attribute in member.GetAttributes())
        {
            if (attribute.AttributeClass?.ToDisplayString() == SqlDefaultAttribute &&
                attribute.ConstructorArguments.Length > 0 &&
                attribute.ConstructorArguments[0].Value is string expression)
            {
                return expression;
            }
        }

        return null;
    }

    /// <summary>The foreign key declared by <c>[References]</c>.</summary>
    private static ForeignKey? ForeignKeyOf(IPropertySymbol member)
    {
        foreach (var attribute in member.GetAttributes())
        {
            if (attribute.AttributeClass?.ToDisplayString() != ReferencesAttribute)
            {
                continue;
            }

            var arguments = attribute.ConstructorArguments;
            var table = arguments.Length > 0 && arguments[0].Value is string t ? t : null;
            var column = arguments.Length > 1 && arguments[1].Value is string c ? c : "Id";

            if (table is null)
            {
                continue;
            }

            var cascade = attribute.NamedArguments
                .Any(n => n.Key == "Cascade" && n.Value.Value is true);

            return new ForeignKey(table, column, cascade);
        }

        return null;
    }

    /// <summary>Every <c>[TableIndex]</c> on the row type.</summary>
    private static List<TableIndex> TableIndexesOf(INamedTypeSymbol? _, INamedTypeSymbol type)
    {
        var indexes = new List<TableIndex>();

        foreach (var attribute in type.GetAttributes())
        {
            if (attribute.AttributeClass?.ToDisplayString() != TableIndexAttribute)
            {
                continue;
            }

            // params string[] arrives as a single array-typed argument.
            var columns = attribute.ConstructorArguments.Length > 0
                ? attribute.ConstructorArguments[0].Values
                    .Select(v => v.Value as string)
                    .Where(v => v is not null)
                    .Select(v => v!)
                    .ToArray()
                : [];

            if (columns.Length == 0)
            {
                continue;
            }

            var where = attribute.NamedArguments
                .FirstOrDefault(n => n.Key == "Where").Value.Value as string;

            var unique = attribute.NamedArguments
                .Any(n => n.Key == "Unique" && n.Value.Value is true);

            indexes.Add(new TableIndex(columns, where, unique));
        }

        return indexes;
    }

    /// <summary>The decimal scale declared by <c>[Scale(n)]</c>, or the library default.</summary>
    private static bool TryScaleOf(
        IPropertySymbol member,
        INamedTypeSymbol type,
        Location memberLocation,
        List<Diagnostic> diagnostics,
        out int scale)
    {
        scale = DefaultScale;

        foreach (var attribute in member.GetAttributes())
        {
            if (attribute.AttributeClass?.ToDisplayString() != ScaleAttribute)
            {
                continue;
            }

            if (attribute.ConstructorArguments.Length > 0 &&
                attribute.ConstructorArguments[0].Value is int digits &&
                digits >= 0 && digits <= MaxScale)
            {
                scale = digits;
                return true;
            }

            diagnostics.Add(Diagnostic.Create(
                Diagnostics.InvalidScale, memberLocation, type.Name, member.Name));
            return false;
        }

        return true;
    }

    private static ITypeSymbol Underlying(ITypeSymbol type) =>
        type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T && type is INamedTypeSymbol named
            ? named.TypeArguments[0]
            : type;

    private static string Emit(RowModel row)
    {
        var keys = row.Columns.Where(c => c.IsKey).ToList();
        var key = keys[0];                       // ordering/cursor column for single-key tables
        var all = row.Columns;
        var orderBy = string.Join(", ", keys.Select(k => SqlIdent.Quote(k.Name) + " DESC"));

        var dataColumns = all.Where(c => !AuditColumns.Contains(c.Name)).ToList();
        var dataNonKey = dataColumns.Where(c => !c.IsKey).ToList();

        var keyParameters = string.Join(", ", keys.Select(k => $"{k.Map.ClrType} {SqlIdent.CSharp(Camel(k.Name))}"));
        var keyPredicate = string.Join(" AND ", keys.Select(k => $"{SqlIdent.Quote(k.Name)} = ${Camel(k.Name)}"));
        var keyColumns = string.Join(", ", keys.Select(k => SqlIdent.Quote(k.Name)));

        var columnList = string.Join(", ", all.Select(c => SqlIdent.Quote(c.Name)));
        var valueList = string.Join(", ", all.Select(c => "$" + Camel(c.Name)));
        var quotedTable = SqlIdent.Quote(row.Table);

        // A composite key cannot be expressed as one TKey, so those tables implement only the
        // key-free interface. The generated methods are identical either way.
        var contract = keys.Count == 1
            ? $"IKeyedRepository<{row.TypeName}, {key.Map.ClrType}>"
            : $"IRepository<{row.TypeName}>";

        var sb = new StringBuilder();

        sb.AppendLine("// <auto-generated/> Generated by Schipper.Io.Sqlite.Generator.RepositoryGenerator.");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine("using System.Runtime.CompilerServices;");
        sb.AppendLine("using System.Threading;");
        sb.AppendLine("using System.Threading.Tasks;");
        sb.AppendLine("using Microsoft.Data.Sqlite;");
        sb.AppendLine("using Schipper.Io.Sqlite;");
        sb.AppendLine("using Schipper.Io.Sqlite.Model;");
        sb.AppendLine("using Schipper.Io.Sqlite.Query;");
        sb.AppendLine();
        sb.AppendLine($"namespace {row.Namespace};");
        sb.AppendLine();
        sb.AppendLine($"/// <summary>Generated persistence for <see cref=\"{row.TypeName}\"/> (table <c>{row.Table}</c>).</summary>");
        sb.AppendLine($"public sealed class {row.TypeName}Repository : {contract}");
        sb.AppendLine("{");
        sb.AppendLine($"    /// <summary>The table name, for hand-written SQL that has to agree with the generated kind.</summary>");
        sb.AppendLine($"    public const string Table = \"{row.Table}\";");
        sb.AppendLine();
        sb.AppendLine($"    /// <summary>Every column, in the order the generated statements use.</summary>");
        sb.AppendLine($"    public const string Columns = \"{Qs(columnList)}\";");
        sb.AppendLine();

        // --- Get -------------------------------------------------------------------------------
        sb.AppendLine("    /// <summary>Reads one row by key, or null.</summary>");
        sb.AppendLine($"    public async Task<{row.TypeName}?> GetAsync(SqliteSession session, {keyParameters}, CancellationToken cancellationToken = default)");
        sb.AppendLine("    {");
        sb.AppendLine($"        var command = session.Command(\"{Qs($"SELECT {columnList} FROM {quotedTable} WHERE {keyPredicate};")}\");");

        foreach (var k in keys)
        {
            sb.AppendLine($"        command.Parameters.AddWithValue(\"${Camel(k.Name)}\", {k.Map.Write(SqlIdent.CSharp(Camel(k.Name)))});");
        }
        sb.AppendLine();
        sb.AppendLine("        await using var reader = await session.ExecuteReaderAsync(command, cancellationToken);");
        sb.AppendLine("        return await reader.ReadAsync(cancellationToken) ? Map(reader) : null;");
        sb.AppendLine("    }");
        sb.AppendLine();

        // --- Query -----------------------------------------------------------------------------
        sb.AppendLine("    /// <summary>");
        sb.AppendLine("    /// Filtered read with keyset pagination. <paramref name=\"limit\"/> is required because a");
        sb.AppendLine("    /// default page size is a silent truncation: callers that treat the result as \"all matches\"");
        sb.AppendLine("    /// would drop every row past it. <see cref=\"StreamAsync\"/> is the unpaged path.");
        sb.AppendLine("    /// <see cref=\"QueryPage{T}.HasMore\"/> is true when at least one more row exists beyond this page.");
        sb.AppendLine("    /// </summary>");
        sb.AppendLine($"    public async Task<QueryPage<{row.TypeName}>> QueryAsync(");
        sb.AppendLine("        SqliteSession session,");
        sb.AppendLine("        int limit,");
        sb.AppendLine("        SqlFilter? filter = null,");

        if (keys.Count == 1)
        {
            sb.AppendLine($"        {key.Map.ClrType}? after = null,");
        }

        sb.AppendLine("        CancellationToken cancellationToken = default)");
        sb.AppendLine("    {");
        sb.AppendLine("        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);");
        sb.AppendLine();
        sb.AppendLine("        // Fetch one extra so HasMore is exact. int.MaxValue cannot be incremented.");
        sb.AppendLine("        var fetch = limit == int.MaxValue ? limit : limit + 1;");
        sb.AppendLine();
        sb.AppendLine("        await using var command = session.CreateCommand();");

        if (keys.Count == 1)
        {
            sb.AppendLine("        // The cursor is declared in CLR terms and bound in storage terms, so a Guid key");
            sb.AppendLine("        // pages against the same TEXT the column actually holds.");
            sb.AppendLine($"        command.CommandText = SqlFilter.Compose(\"{Qs($"SELECT {columnList} FROM {quotedTable}")}\", filter, command, after is null ? null : \"{Qs(SqlIdent.Quote(key.Name))}\", after is {{ }} __after ? (object){key.Map.Write("__after")} : null, \"{Qs(orderBy)}\", fetch);");
        }
        else
        {
            // Composite keys have no single-value cursor: paging on keys[0] alone cannot advance
            // when a filter holds that column constant. StreamAsync walks the whole match.
            sb.AppendLine($"        command.CommandText = SqlFilter.Compose(\"{Qs($"SELECT {columnList} FROM {quotedTable}")}\", filter, command, null, null, \"{Qs(orderBy)}\", fetch);");
        }

        sb.AppendLine();
        sb.AppendLine($"        var results = new List<{row.TypeName}>();");
        sb.AppendLine("        await using var reader = await session.ExecuteReaderAsync(command, cancellationToken);");
        sb.AppendLine();
        sb.AppendLine("        while (await reader.ReadAsync(cancellationToken))");
        sb.AppendLine("        {");
        sb.AppendLine("            results.Add(Map(reader));");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        var hasMore = results.Count > limit;");
        sb.AppendLine();
        sb.AppendLine("        if (hasMore)");
        sb.AppendLine("        {");
        sb.AppendLine("            results.RemoveAt(results.Count - 1);");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine($"        return new QueryPage<{row.TypeName}>(results, hasMore);");
        sb.AppendLine("    }");
        sb.AppendLine();

        if (keys.Count == 1)
        {
            // Public QueryAsync has an extra `after` parameter, which would not match the
            // interface. Explicit implementation keeps IRepository usable without ambiguity.
            sb.AppendLine($"        Task<QueryPage<{row.TypeName}>> IRepository<{row.TypeName}>.QueryAsync(");
            sb.AppendLine("            SqliteSession session, int limit, SqlFilter? filter, CancellationToken cancellationToken) =>");
            sb.AppendLine("            QueryAsync(session, limit, filter, after: default, cancellationToken);");
            sb.AppendLine();
        }

        // --- Count -----------------------------------------------------------------------------
        sb.AppendLine("    /// <summary>");
        sb.AppendLine("    /// How many rows the same filter matches, ignoring any cursor.");
        sb.AppendLine("    ///");
        sb.AppendLine("    /// Generated rather than hand-written per screen so that the count and the page cannot");
        sb.AppendLine("    /// come to disagree about what they are counting — they take the same SqlFilter. It is");
        sb.AppendLine("    /// a separate call rather than part of QueryAsync because it is a second scan: a caller");
        sb.AppendLine("    /// paging through results has no use for it, and one showing \"20 of 4,312\" does.");
        sb.AppendLine("    /// </summary>");
        sb.AppendLine("    public async Task<int> CountAsync(");
        sb.AppendLine("        SqliteSession session,");
        sb.AppendLine("        SqlFilter? filter = null,");
        sb.AppendLine("        CancellationToken cancellationToken = default)");
        sb.AppendLine("    {");
        sb.AppendLine("        await using var command = session.CreateCommand();");
        sb.AppendLine();
        sb.AppendLine("        // Composed here rather than through SqlFilter.Compose, which always appends an");
        sb.AppendLine("        // ORDER BY for the keyset cursor. Ordering a COUNT is work with no result.");
        sb.AppendLine($"        command.CommandText = \"SELECT COUNT(*) FROM {Qs(quotedTable)}\";");
        sb.AppendLine();
        sb.AppendLine("        if (filter is { IsEmpty: false })");
        sb.AppendLine("        {");
        sb.AppendLine("            command.CommandText += \" WHERE \" + filter.Clause;");
        sb.AppendLine("            filter.Bind(command);");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        command.CommandText += \";\";");
        sb.AppendLine();
        sb.AppendLine("        return Convert.ToInt32(await session.ExecuteScalarAsync(command, cancellationToken));");
        sb.AppendLine("    }");
        sb.AppendLine();

        // --- Stream ----------------------------------------------------------------------------
        sb.AppendLine("    /// <summary>The same read, streamed. Nothing is buffered; one row is live at a time.</summary>");
        sb.AppendLine($"    public async IAsyncEnumerable<{row.TypeName}> StreamAsync(");
        sb.AppendLine("        SqliteSession session,");
        sb.AppendLine("        SqlFilter? filter = null,");
        sb.AppendLine("        [EnumeratorCancellation] CancellationToken cancellationToken = default)");
        sb.AppendLine("    {");
        sb.AppendLine("        await using var command = session.CreateCommand();");
        sb.AppendLine($"        command.CommandText = SqlFilter.Compose(\"{Qs($"SELECT {columnList} FROM {quotedTable}")}\", filter, command, null, null, \"{Qs(orderBy)}\", null);");
        sb.AppendLine();
        sb.AppendLine("        await using var reader = await session.ExecuteReaderAsync(command, cancellationToken);");
        sb.AppendLine();
        sb.AppendLine("        while (await reader.ReadAsync(cancellationToken))");
        sb.AppendLine("        {");
        sb.AppendLine("            yield return Map(reader);");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine();

        // --- Insert ----------------------------------------------------------------------------
        sb.AppendLine("    /// <summary>Inserts a new row, stamping Created, Modified, User and Version.</summary>");
        sb.AppendLine($"    public async Task<int> InsertAsync(SqliteSession session, {row.TypeName} row, CancellationToken cancellationToken = default)");
        sb.AppendLine("    {");
        sb.AppendLine($"        var command = session.Command(\"{Qs($"INSERT INTO {quotedTable} ({columnList}) VALUES ({valueList});")}\");");
        sb.AppendLine();
        sb.AppendLine("        var now = DateTimeOffset.UtcNow.UtcTicks;");
        sb.AppendLine("        Bind(command, row);");
        sb.AppendLine("        command.Parameters.AddWithValue(\"$created\", now);");
        sb.AppendLine("        command.Parameters.AddWithValue(\"$modified\", now);");
        sb.AppendLine("        command.Parameters.AddWithValue(\"$user\", session.User);");
        sb.AppendLine("        command.Parameters.AddWithValue(\"$version\", 1L);");
        sb.AppendLine();
        sb.AppendLine("        // Keep the in-memory row consistent with what was written, so a caller that");
        sb.AppendLine("        // updates straight afterwards carries the right version.");
        sb.AppendLine("        row.Created = now;");
        sb.AppendLine("        row.Modified = now;");
        sb.AppendLine("        row.User = session.User;");
        sb.AppendLine("        row.Version = 1L;");
        sb.AppendLine();
        sb.AppendLine("        return await session.ExecuteNonQueryAsync(command, cancellationToken);");
        sb.AppendLine("    }");
        sb.AppendLine();

        // --- Update ----------------------------------------------------------------------------
        sb.AppendLine("    /// <summary>");
        sb.AppendLine("    /// Replaces every data column, so this is what sets a nullable column back to NULL.");
        sb.AppendLine("    ///");
        sb.AppendLine("    /// Optimistic concurrency: the WHERE carries the Version that was read, and the database");
        sb.AppendLine("    /// increments it. A row somebody else has written no longer matches, so nothing is");
        sb.AppendLine("    /// silently overwritten. Created is deliberately absent from the SET clause.");
        sb.AppendLine("    ///");
        sb.AppendLine("    /// Throws <see cref=\"SqliteConcurrencyException\"/> when no row matched. Use TryUpdateAsync");
        sb.AppendLine("    /// to handle that without an exception.");
        sb.AppendLine("    /// </summary>");
        sb.AppendLine($"    public async Task UpdateAsync(SqliteSession session, {row.TypeName} row, CancellationToken cancellationToken = default)");
        sb.AppendLine("    {");
        sb.AppendLine("        if (!await TryUpdateAsync(session, row, cancellationToken))");
        sb.AppendLine("        {");
        sb.AppendLine($"            throw new SqliteConcurrencyException(\"{row.Table}\", {KeyDescription(keys)});");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>Version-checked update. Returns false when the row was changed or removed.</summary>");
        sb.AppendLine($"    public async Task<bool> TryUpdateAsync(SqliteSession session, {row.TypeName} row, CancellationToken cancellationToken = default)");
        sb.AppendLine("    {");
        // The audit assignments are concatenated rather than appended to a joined string, because a
        // table whose every column is part of the key has no data assignments at all — a pure join
        // row like (UserId, RoleId). Appending would emit "SET , Modified = ..." and fail at the
        // first write with a SQLite syntax error rather than at build time.
        var updateSet = string.Join(", ", dataNonKey
            .Select(c => $"{SqlIdent.Quote(c.Name)} = ${Camel(c.Name)}")
            .Concat(new[]
            {
                $"{SqlIdent.Quote("Modified")} = $modified",
                $"{SqlIdent.Quote("User")} = $user",
                $"{SqlIdent.Quote("Version")} = {SqlIdent.Quote("Version")} + 1",
            }));

        sb.AppendLine($"        var command = session.Command(\"{Qs($"UPDATE {quotedTable} SET {updateSet} WHERE {keyPredicate} AND {SqlIdent.Quote("Version")} = $expectedVersion;")}\");");
        sb.AppendLine();
        sb.AppendLine("        var now = DateTimeOffset.UtcNow.UtcTicks;");
        sb.AppendLine("        Bind(command, row);");
        sb.AppendLine("        command.Parameters.AddWithValue(\"$modified\", now);");
        sb.AppendLine("        command.Parameters.AddWithValue(\"$user\", session.User);");
        sb.AppendLine("        command.Parameters.AddWithValue(\"$expectedVersion\", row.Version);");
        sb.AppendLine();
        sb.AppendLine("        if (await session.ExecuteNonQueryAsync(command, cancellationToken) == 0)");
        sb.AppendLine("        {");
        sb.AppendLine("            return false;");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        row.Modified = now;");
        sb.AppendLine("        row.User = session.User;");
        sb.AppendLine("        row.Version += 1;");
        sb.AppendLine("        return true;");
        sb.AppendLine("    }");
        sb.AppendLine();

        // --- Upsert ----------------------------------------------------------------------------
        // Same shape as the UPDATE above, and same reason: an all-key row contributes nothing here.
        var upsertSet = string.Join(", ", dataNonKey
            .Select(c => c.Nullable
                ? $"{SqlIdent.Quote(c.Name)} = COALESCE(excluded.{SqlIdent.Quote(c.Name)}, {quotedTable}.{SqlIdent.Quote(c.Name)})"
                : $"{SqlIdent.Quote(c.Name)} = excluded.{SqlIdent.Quote(c.Name)}")
            .Concat(new[]
            {
                $"{SqlIdent.Quote("Modified")} = excluded.{SqlIdent.Quote("Modified")}",
                $"{SqlIdent.Quote("User")} = excluded.{SqlIdent.Quote("User")}",
                $"{SqlIdent.Quote("Version")} = {quotedTable}.{SqlIdent.Quote("Version")} + 1",
            }));

        sb.AppendLine("    /// <summary>");
        sb.AppendLine("    /// Inserts, or updates only the columns whose incoming value is non-null.");
        sb.AppendLine("    ///");
        sb.AppendLine("    /// Nullable columns use COALESCE(excluded.X, X), so a null leaves the stored value alone.");
        sb.AppendLine("    /// The consequence is deliberate: <b>upsert can never clear a column to NULL</b> - use");
        sb.AppendLine("    /// UpdateAsync for that. Which columns behave this way is decided by the C# nullability on");
        sb.AppendLine("    /// the row type, not restated here.");
        sb.AppendLine("    ///");
        sb.AppendLine("    /// Created is absent from the DO UPDATE clause, so it survives every later upsert and");
        sb.AppendLine("    /// keeps meaning \"when this row first existed\". Modified, User and Version are stamped on");
        sb.AppendLine("    /// both halves.");
        sb.AppendLine("    ///");
        sb.AppendLine("    /// Optimistic concurrency is OPTIONAL here, which is what makes upsert still usable for");
        sb.AppendLine("    /// its real purpose. Stored versions start at 1, so <c>Version == 0</c> means \"I never read");
        sb.AppendLine("    /// this row\" and the check is skipped — seeding, webhook replay and reconciliation pass a");
        sb.AppendLine("    /// freshly constructed row and get make-it-so semantics. Any non-zero Version is enforced");
        sb.AppendLine("    /// exactly as UpdateAsync enforces it, so a read-modify-write is protected.");
        sb.AppendLine("    ///");
        sb.AppendLine("    /// Throws <see cref=\"SqliteConcurrencyException\"/> when a supplied version did not match.");
        sb.AppendLine("    /// </summary>");
        sb.AppendLine($"    public async Task UpsertAsync(SqliteSession session, {row.TypeName} row, CancellationToken cancellationToken = default)");
        sb.AppendLine("    {");
        sb.AppendLine("        if (!await TryUpsertAsync(session, row, cancellationToken))");
        sb.AppendLine("        {");
        sb.AppendLine($"            throw new SqliteConcurrencyException(\"{row.Table}\", {KeyDescription(keys)});");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>Upsert that returns false instead of throwing when a supplied version was stale.</summary>");
        sb.AppendLine($"    public async Task<bool> TryUpsertAsync(SqliteSession session, {row.TypeName} row, CancellationToken cancellationToken = default)");
        sb.AppendLine("    {");
        sb.AppendLine("        // RETURNING gives the resulting version exactly, which matters because the caller may");
        sb.AppendLine("        // not know which half ran. It also IS the conflict signal: a DO UPDATE whose WHERE");
        sb.AppendLine("        // fails updates nothing and returns no row, without raising an error.");
        sb.AppendLine($"        var command = session.Command(\"{Qs($"INSERT INTO {quotedTable} ({columnList}) VALUES ({valueList}) ON CONFLICT ({keyColumns}) DO UPDATE SET {upsertSet} WHERE $expectedVersion = 0 OR {quotedTable}.{SqlIdent.Quote("Version")} = $expectedVersion RETURNING {SqlIdent.Quote("Version")};")}\");");
        sb.AppendLine();
        sb.AppendLine("        var now = DateTimeOffset.UtcNow.UtcTicks;");
        sb.AppendLine("        Bind(command, row);");
        sb.AppendLine("        command.Parameters.AddWithValue(\"$created\", now);");
        sb.AppendLine("        command.Parameters.AddWithValue(\"$modified\", now);");
        sb.AppendLine("        command.Parameters.AddWithValue(\"$user\", session.User);");
        sb.AppendLine("        command.Parameters.AddWithValue(\"$version\", 1L);");
        sb.AppendLine("        command.Parameters.AddWithValue(\"$expectedVersion\", row.Version);");
        sb.AppendLine();
        sb.AppendLine("        if (await session.ExecuteScalarAsync(command, cancellationToken) is not long version)");
        sb.AppendLine("        {");
        sb.AppendLine("            return false;");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        row.Modified = now;");
        sb.AppendLine("        row.User = session.User;");
        sb.AppendLine("        row.Version = version;");
        sb.AppendLine("        return true;");
        sb.AppendLine("    }");
        sb.AppendLine();

        // --- Delete ----------------------------------------------------------------------------
        sb.AppendLine("    /// <summary>Deletes by key. Returns the number of rows removed.</summary>");
        sb.AppendLine($"    public async Task<int> DeleteAsync(SqliteSession session, {keyParameters}, CancellationToken cancellationToken = default)");
        sb.AppendLine("    {");
        sb.AppendLine($"        var command = session.Command(\"{Qs($"DELETE FROM {quotedTable} WHERE {keyPredicate};")}\");");

        foreach (var k in keys)
        {
            sb.AppendLine($"        command.Parameters.AddWithValue(\"${Camel(k.Name)}\", {k.Map.Write(SqlIdent.CSharp(Camel(k.Name)))});");
        }
        sb.AppendLine();
        sb.AppendLine("        return await session.ExecuteNonQueryAsync(command, cancellationToken);");
        sb.AppendLine("    }");
        sb.AppendLine();

        // --- Bind ------------------------------------------------------------------------------
        sb.AppendLine($"    private static void Bind(SqliteCommand command, {row.TypeName} row)");
        sb.AppendLine("    {");

        // Data columns only. Created/Modified/User/Version are supplied by each write method,
        // because their values come from the clock and the session rather than from the row.
        foreach (var column in dataColumns)
        {
            var local = "__" + Camel(column.Name);
            var rowMember = "row." + SqlIdent.CSharp(column.Name);

            sb.AppendLine(column.Nullable
                ? $"        command.Parameters.AddWithValue(\"${Camel(column.Name)}\", {rowMember} is {{ }} {local} ? (object){column.Map.Write(local)} : DBNull.Value);"
                : $"        command.Parameters.AddWithValue(\"${Camel(column.Name)}\", {column.Map.Write(rowMember)});");
        }

        sb.AppendLine("    }");
        sb.AppendLine();

        // --- Map -------------------------------------------------------------------------------
        sb.AppendLine("    /// <summary>Ordinals resolved by NAME. Hard-coded positions are how same-typed columns get transposed silently.</summary>");
        sb.AppendLine($"    public static {row.TypeName} Map(SqliteDataReader reader)");
        sb.AppendLine("    {");

        foreach (var column in all)
        {
            sb.AppendLine($"        var o{column.Name} = reader.GetOrdinal(\"{Qs(column.Name)}\");");
        }

        sb.AppendLine();
        sb.AppendLine($"        return new {row.TypeName}");
        sb.AppendLine("        {");

        foreach (var column in all)
        {
            sb.AppendLine($"            {SqlIdent.CSharp(column.Name)} = {Read(column)},");
        }

        sb.AppendLine("        };");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        return sb.ToString();
    }

    private static string Read(ColumnModel column)
    {
        var getter = column.Map.Read("o" + column.Name);

        return column.Nullable
            ? $"reader.IsDBNull(o{column.Name}) ? null : {getter}"
            : getter;
    }

    /// <summary>Interpolation that names the key value(s) in a concurrency failure.</summary>
    private static string KeyDescription(List<ColumnModel> keys) =>
        keys.Count == 1
            ? (keys[0].Map.Kind == MapKind.String
                ? $"row.{SqlIdent.CSharp(keys[0].Name)}"
                : $"row.{SqlIdent.CSharp(keys[0].Name)}.ToString()")
            : "string.Join(\"/\", new object?[] { " + string.Join(", ", keys.Select(k => $"row.{SqlIdent.CSharp(k.Name)}")) + " })";

    private static string Camel(string name) => char.ToLowerInvariant(name[0]) + name.Substring(1);

    /// <summary>Escapes a SQL fragment so it can live inside a generated C# string literal.</summary>
    private static string Qs(string sql) => sql.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private sealed record DescribeResult(RowModel? Row, List<Diagnostic> Diagnostics)
    {
        public static readonly DescribeResult Empty = new(null, []);
    }

}
