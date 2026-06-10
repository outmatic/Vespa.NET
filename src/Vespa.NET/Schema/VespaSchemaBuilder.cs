using System.IO.Compression;
using System.Reflection;
using System.Text;
using Vespa.Models.Attributes;

namespace Vespa.Models.Schema;

/// <summary>
/// Generates Vespa schema (<c>.sd</c>) files and application packages from C# types
/// decorated with <see cref="VespaDocumentAttribute"/>, <see cref="VespaFieldAttribute"/>,
/// and <see cref="VespaTensorAttribute"/>.
/// </summary>
public static class VespaSchemaBuilder
{
    /// <summary>Generates a Vespa <c>.sd</c> schema string for <typeparamref name="T"/>.</summary>
    public static string GenerateSchema<T>() where T : class => GenerateSchema(typeof(T));

    /// <summary>Generates a Vespa <c>.sd</c> schema string for the given type.</summary>
    public static string GenerateSchema(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        var docAttr = type.GetCustomAttribute<VespaDocumentAttribute>()
            ?? throw new InvalidOperationException(
                $"Type '{type.Name}' must be decorated with [VespaDocument].");

        var docType = docAttr.DocumentType;
        var sb = new StringBuilder();
        var tensorInputs = new List<(string QueryName, string TensorType)>();

        var schemaInherits = docAttr.Inherits is not null ? $" inherits {docAttr.Inherits}" : "";
        sb.AppendLine($"schema {docType}{schemaInherits} {{");

        var docSelection = docAttr.Selection;
        sb.AppendLine($"    document {docType} {{");
        if (docSelection is not null)
            sb.AppendLine($"        selection: {docSelection}");

        // Collect and emit struct definitions
        var emittedStructs = new HashSet<Type>();
        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.GetCustomAttribute<VespaExtraFieldsAttribute>() is not null)
                continue;

            var structType = ResolveStructType(prop.PropertyType);
            if (structType is not null && emittedStructs.Add(structType))
                AppendStructDefinition(sb, structType);
        }

        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            // [VespaExtraFields] is a catch-all bag for unmapped fields — skip in schema generation
            if (prop.GetCustomAttribute<VespaExtraFieldsAttribute>() is not null)
                continue;

            var tensorAttr = prop.GetCustomAttribute<VespaTensorAttribute>();
            var fieldAttr = prop.GetCustomAttribute<VespaFieldAttribute>();

            if (tensorAttr is not null)
            {
                AppendTensorField(sb, prop, tensorAttr, fieldAttr);

                // Collect tensor inputs for rank-profile generation
                var fieldName = ResolveFieldName(prop, fieldAttr);
                tensorInputs.Add(($"query(query_{fieldName})", tensorAttr.GetTensorTypeString()));
                continue;
            }

            if (fieldAttr is not null)
            {
                AppendRegularField(sb, prop, fieldAttr);
            }
        }

        sb.AppendLine("    }");

        // Import fields (schema-level, outside document block)
        foreach (var impAttr in type.GetCustomAttributes<VespaImportFieldAttribute>())
            sb.AppendLine($"    import field {impAttr.ReferenceField}.{impAttr.ForeignField} as {impAttr.Alias} {{}}");

        // Constant tensors
        foreach (var constAttr in type.GetCustomAttributes<VespaConstantAttribute>())
        {
            sb.AppendLine($"    constant {constAttr.Name} {{");
            sb.AppendLine($"        file: {constAttr.File}");
            sb.AppendLine($"        type: {constAttr.TensorType}");
            sb.AppendLine("    }");
        }

        // ONNX models
        foreach (var onnxAttr in type.GetCustomAttributes<VespaOnnxModelAttribute>())
        {
            sb.AppendLine($"    onnx-model {onnxAttr.Name} {{");
            sb.AppendLine($"        file: {onnxAttr.File}");
            sb.AppendLine("    }");
        }

        // Fieldsets
        foreach (var fsAttr in type.GetCustomAttributes<VespaFieldSetAttribute>())
            sb.AppendLine($"    fieldset {fsAttr.Name} {{\n        fields: {string.Join(", ", fsAttr.Fields)}\n    }}");

        // Document summaries
        foreach (var dsAttr in type.GetCustomAttributes<VespaDocumentSummaryAttribute>())
        {
            var inherits = dsAttr.Inherits is not null ? $" inherits {dsAttr.Inherits}" : "";
            sb.AppendLine($"    document-summary {dsAttr.Name}{inherits} {{");
            foreach (var field in dsAttr.Fields)
                sb.AppendLine($"        summary {field} {{}}");
            sb.AppendLine("    }");
        }

        // Rank profiles — auto-generate a "default" with tensor inputs, unless the
        // user declared their own "default" (two same-name profiles fail deployment;
        // the inputs are merged into the user's profile below instead).
        var rankProfiles = type.GetCustomAttributes<VespaRankProfileAttribute>().ToList();
        var hasUserDefault = rankProfiles.Any(rp => rp.Name == "default");

        if (tensorInputs.Count > 0 && !hasUserDefault)
        {
            sb.AppendLine("    rank-profile default {");
            AppendRankProfileInputs(sb, tensorInputs);
            sb.AppendLine("    }");
        }

        // User-defined rank profiles
        foreach (var rpAttr in rankProfiles)
        {
            var inherits = rpAttr.Inherits is not null ? $" inherits {rpAttr.Inherits}" : "";
            sb.AppendLine($"    rank-profile {rpAttr.Name}{inherits} {{");

            if (rpAttr.Name == "default" && tensorInputs.Count > 0)
                AppendRankProfileInputs(sb, tensorInputs);

            if (rpAttr.FirstPhase is not null)
                sb.AppendLine($"        first-phase {{\n            expression: {rpAttr.FirstPhase}\n        }}");

            if (rpAttr.SecondPhase is not null)
            {
                sb.Append($"        second-phase {{\n            expression: {rpAttr.SecondPhase}\n");
                if (rpAttr.SecondPhaseRerankCount > 0)
                    sb.Append($"            rerank-count: {rpAttr.SecondPhaseRerankCount}\n");
                sb.AppendLine("        }");
            }

            if (rpAttr.GlobalPhase is not null)
            {
                sb.Append($"        global-phase {{\n            expression: {rpAttr.GlobalPhase}\n");
                if (rpAttr.GlobalPhaseRerankCount > 0)
                    sb.Append($"            rerank-count: {rpAttr.GlobalPhaseRerankCount}\n");
                sb.AppendLine("        }");
            }

            if (rpAttr.MatchFeatures is not null)
                sb.AppendLine($"        match-features: {rpAttr.MatchFeatures}");

            if (rpAttr.SummaryFeatures is not null)
                sb.AppendLine($"        summary-features: {rpAttr.SummaryFeatures}");

            if (rpAttr.DiversityAttribute is not null)
            {
                sb.AppendLine("        diversity {");
                sb.AppendLine($"            attribute: {rpAttr.DiversityAttribute}");
                if (rpAttr.DiversityMinGroups > 0)
                    sb.AppendLine($"            min-groups: {rpAttr.DiversityMinGroups}");
                sb.AppendLine("        }");
            }

            if (rpAttr.Functions is not null)
            {
                foreach (var funcDef in rpAttr.Functions.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var colonIdx = funcDef.IndexOf(':');
                    if (colonIdx > 0)
                    {
                        var funcSignature = funcDef[..colonIdx].Trim();
                        var funcBody = funcDef[(colonIdx + 1)..].Trim();
                        sb.AppendLine($"        function {funcSignature} {{\n            expression: {funcBody}\n        }}");
                    }
                }
            }

            sb.AppendLine("    }");
        }

        sb.AppendLine("}");

        return sb.ToString();
    }

    /// <summary>Generates a ZIP application package for <typeparamref name="T"/> and writes it to the output stream.</summary>
    public static void GenerateApplicationPackage<T>(Stream output, ApplicationPackageOptions? options = null)
        where T : class => GenerateApplicationPackage(output, typeof(T), options);

    /// <summary>Generates a ZIP application package for a single document type and writes it to the output stream.</summary>
    public static void GenerateApplicationPackage(Stream output, Type type, ApplicationPackageOptions? options = null)
        => GenerateApplicationPackage(output, [type], options);

    /// <summary>
    /// Generates a minimal Vespa application package ZIP containing
    /// <c>services.xml</c> and one <c>schemas/&lt;doctype&gt;.sd</c> per type.
    /// Files referenced by <see cref="VespaOnnxModelAttribute.SourcePath"/> and
    /// <see cref="VespaConstantAttribute.SourcePath"/> are streamed into the ZIP automatically.
    /// Additional files can be included via <see cref="ApplicationPackageOptions.AdditionalFiles"/>.
    /// </summary>
    public static void GenerateApplicationPackage(
        Stream output,
        IEnumerable<Type> documentTypes,
        ApplicationPackageOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(documentTypes);

        var opts = options ?? new ApplicationPackageOptions();

        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);

        var docInfos = new List<(string DocType, bool Global, DocumentMode Mode)>();
        var includedPaths = new HashSet<string>(StringComparer.Ordinal);

        foreach (var type in documentTypes)
        {
            var schema = GenerateSchema(type);
            var docAttr = type.GetCustomAttribute<VespaDocumentAttribute>()!;
            docInfos.Add((docAttr.DocumentType, docAttr.Global, docAttr.Mode));

            var entry = zip.CreateEntry($"schemas/{docAttr.DocumentType}.sd");
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            writer.Write(schema);

            // Auto-include files from ONNX model attributes
            foreach (var onnx in type.GetCustomAttributes<VespaOnnxModelAttribute>())
            {
                if (onnx.SourcePath is not null && includedPaths.Add(onnx.File))
                    CopyFileToZip(zip, onnx.File, onnx.SourcePath);
            }

            // Auto-include files from constant tensor attributes
            foreach (var constant in type.GetCustomAttributes<VespaConstantAttribute>())
            {
                if (constant.SourcePath is not null && includedPaths.Add(constant.File))
                    CopyFileToZip(zip, constant.File, constant.SourcePath);
            }
        }

        // Additional files (manual overrides / extra resources)
        if (opts.AdditionalFiles is not null)
        {
            foreach (var (zipPath, sourcePath) in opts.AdditionalFiles)
            {
                if (includedPaths.Add(zipPath))
                    CopyFileToZip(zip, zipPath, sourcePath);
            }
        }

        var servicesEntry = zip.CreateEntry("services.xml");
        using var servicesWriter = new StreamWriter(servicesEntry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        servicesWriter.Write(opts.CustomServicesXml ?? GenerateServicesXml(docInfos, opts));
    }

    private static void CopyFileToZip(ZipArchive zip, string entryPath, string sourcePath)
    {
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException(
                $"Source file '{sourcePath}' for ZIP entry '{entryPath}' was not found.", sourcePath);

        var entry = zip.CreateEntry(entryPath);
        using var source = File.OpenRead(sourcePath);
        using var dest = entry.Open();
        source.CopyTo(dest);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static void AppendTensorField(
        StringBuilder sb,
        PropertyInfo prop,
        VespaTensorAttribute tensorAttr,
        VespaFieldAttribute? fieldAttr)
    {
        var name = ResolveFieldName(prop, fieldAttr);
        var typeStr = tensorAttr.GetTensorTypeString();
        var metric = ToVespaMetricName(tensorAttr.DistanceMetric);

        sb.AppendLine($"        field {name} type {typeStr} {{");
        var indexingParts = new List<string> { "attribute" };
        if (tensorAttr.EnableIndex)
            indexingParts.Add("index");
        if (tensorAttr.IncludeInSummary)
            indexingParts.Add("summary");
        sb.AppendLine($"            indexing: {string.Join(" | ", indexingParts)}");

        sb.AppendLine("            attribute {");
        if (metric is not null)
            sb.AppendLine($"                distance-metric: {metric}");
        sb.AppendLine("            }");

        if (tensorAttr.EnableIndex)
        {
            sb.AppendLine("            index {");
            sb.AppendLine($"                hnsw {{");
            sb.AppendLine($"                    max-links-per-node: {tensorAttr.MaxLinksPerNode}");
            sb.AppendLine($"                    neighbors-to-explore-at-insert: {tensorAttr.NeighborsToExploreAtInsert}");
            sb.AppendLine("                }");
            sb.AppendLine("            }");
        }

        sb.AppendLine("        }");
    }

    private static void AppendRegularField(StringBuilder sb, PropertyInfo prop, VespaFieldAttribute fieldAttr)
    {
        var name = ResolveFieldName(prop, fieldAttr);

        if (name.Equals("id", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Field '{prop.Name}' on type '{prop.DeclaringType?.Name}' resolves to Vespa field name 'id', " +
                "which conflicts with the document identifier. Use [VespaId] for the document ID, " +
                "or choose a different field name.");
        var vespaType = fieldAttr.FieldType == VespaFieldType.Reference
            ? $"reference<{fieldAttr.ReferenceDocumentType ?? throw new InvalidOperationException(
                $"Field '{prop.Name}' has FieldType=Reference but ReferenceDocumentType is not set.")}>"
            : fieldAttr.FieldType != VespaFieldType.None
                ? ToVespaTypeName(fieldAttr.FieldType)
                : InferVespaType(prop.PropertyType);

        sb.AppendLine($"        field {name} type {vespaType} {{");

        if (fieldAttr.IndexingMode != IndexingMode.None)
        {
            var indexing = BuildIndexingString(fieldAttr.IndexingMode);
            sb.AppendLine($"            indexing: {indexing}");
        }

        if (fieldAttr.MatchMode != MatchMode.None)
        {
            var match = ToVespaMatchName(fieldAttr.MatchMode);
            if (match is not null)
                sb.AppendLine($"            match: {match}");
        }

        if (fieldAttr.Stemming != StemmingMode.None)
        {
            var stemming = ToVespaStemmingName(fieldAttr.Stemming);
            if (stemming is not null)
                sb.AppendLine($"            stemming: {stemming}");
        }

        if (fieldAttr.Bolding)
            sb.AppendLine("            bolding: on");

        if (fieldAttr.FastSearch)
            sb.AppendLine("            attribute: fast-search");

        if (fieldAttr.Normalizing is not null)
            sb.AppendLine($"            normalizing: {fieldAttr.Normalizing}");

        // Field-level rank: accepts only filter/normal; identity and tags belong
        // to the separate rank-type element.
        switch (fieldAttr.RankType)
        {
            case RankType.Filter:
                sb.AppendLine("            rank: filter");
                break;
            case RankType.Default:
                sb.AppendLine("            rank: normal");
                break;
            case RankType.Identity:
                sb.AppendLine("            rank-type: identity");
                break;
            case RankType.Tags:
                sb.AppendLine("            rank-type: tags");
                break;
        }

        sb.AppendLine("        }");
    }

    private static void AppendRankProfileInputs(StringBuilder sb, List<(string QueryName, string TensorType)> tensorInputs)
    {
        sb.AppendLine("        inputs {");
        foreach (var (queryName, tensorType) in tensorInputs)
            sb.AppendLine($"            {queryName} {tensorType}");
        sb.AppendLine("        }");
    }

    private static string BuildIndexingString(IndexingMode mode)
    {
        var parts = new List<string>();
        if ((mode & IndexingMode.Index) != 0) parts.Add("index");
        if ((mode & IndexingMode.Attribute) != 0) parts.Add("attribute");
        if ((mode & IndexingMode.Summary) != 0) parts.Add("summary");
        return string.Join(" | ", parts);
    }

    private static string ToVespaTypeName(VespaFieldType ft) => ft switch
    {
        VespaFieldType.Bool => "bool",
        VespaFieldType.Byte => "byte",
        VespaFieldType.Int => "int",
        VespaFieldType.Long => "long",
        VespaFieldType.Float => "float",
        VespaFieldType.Double => "double",
        VespaFieldType.String => "string",
        VespaFieldType.Raw => "raw",
        VespaFieldType.Uri => "uri",
        VespaFieldType.Predicate => "predicate",
        VespaFieldType.ArrayBool => "array<bool>",
        VespaFieldType.ArrayByte => "array<byte>",
        VespaFieldType.ArrayInt => "array<int>",
        VespaFieldType.ArrayLong => "array<long>",
        VespaFieldType.ArrayFloat => "array<float>",
        VespaFieldType.ArrayDouble => "array<double>",
        VespaFieldType.ArrayString => "array<string>",
        VespaFieldType.WeightedSetString => "weightedset<string>",
        VespaFieldType.WeightedSetInt => "weightedset<int>",
        VespaFieldType.MapStringString => "map<string,string>",
        VespaFieldType.MapStringInt => "map<string,int>",
        _ => throw new NotSupportedException($"VespaFieldType.{ft} is not supported for schema generation.")
    };

    private static string InferVespaType(Type clrType)
    {
        // Unwrap Nullable<T>
        var underlying = Nullable.GetUnderlyingType(clrType) ?? clrType;

        if (underlying == typeof(string)) return "string";
        if (underlying == typeof(int)) return "int";
        if (underlying == typeof(long)) return "long";
        if (underlying == typeof(double)) return "double";
        if (underlying == typeof(float)) return "float";
        if (underlying == typeof(bool)) return "bool";
        if (underlying == typeof(byte)) return "byte";
        if (underlying == typeof(decimal)) return "double";

        // Arrays / Lists
        if (underlying.IsArray)
            return $"array<{InferVespaType(underlying.GetElementType()!)}>";

        if (underlying.IsGenericType)
        {
            var gtd = underlying.GetGenericTypeDefinition();
            var args = underlying.GetGenericArguments();

            if (gtd == typeof(List<>) || gtd == typeof(IList<>) || gtd == typeof(IEnumerable<>))
                return $"array<{InferVespaType(args[0])}>";

            if ((gtd == typeof(Dictionary<,>) || gtd == typeof(IDictionary<,>)) && args.Length == 2)
            {
                var keyType = InferVespaType(args[0]);
                var valType = InferVespaType(args[1]);
                return (keyType, valType) switch
                {
                    ("string", "int") => "weightedset<string>",
                    ("string", "string") => "map<string,string>",
                    _ => $"map<{keyType},{valType}>"
                };
            }
        }

        // Check for [VespaStruct]-annotated types
        var structAttr = underlying.GetCustomAttribute<VespaStructAttribute>();
        if (structAttr is not null)
            return structAttr.Name ?? ToSnakeCase(underlying.Name);

        return "string"; // safe fallback
    }

    private static string? ToVespaMetricName(DistanceMetric m) => m switch
    {
        DistanceMetric.None => null,
        DistanceMetric.Euclidean => "euclidean",
        DistanceMetric.Angular => "angular",
        DistanceMetric.DotProduct => "dotproduct",
        DistanceMetric.Hamming => "hamming",
        DistanceMetric.Geodesic => "geodegrees",
        DistanceMetric.InnerProduct => "innerproduct",
        DistanceMetric.PrenormalizedAngular => "prenormalized-angular",
        _ => null
    };

    private static string? ToVespaStemmingName(StemmingMode s) => s switch
    {
        StemmingMode.None => null,
        StemmingMode.Best => "best",
        StemmingMode.Multiple => "multiple",
        StemmingMode.Off => "none",
        _ => null
    };

    private static string? ToVespaMatchName(MatchMode m) => m switch
    {
        MatchMode.None => null,
        MatchMode.Text => "text",
        MatchMode.Word => "word",
        MatchMode.Exact => "exact",
        MatchMode.Prefix => "prefix",
        MatchMode.Substring => "substring",
        MatchMode.Suffix => "suffix",
        MatchMode.Cased => "cased",
        MatchMode.Uncased => "uncased",
        MatchMode.Gram => "gram",
        _ => null
    };

    private static void AppendStructDefinition(StringBuilder sb, Type structType)
    {
        var structAttr = structType.GetCustomAttribute<VespaStructAttribute>()!;
        var structName = structAttr.Name ?? ToSnakeCase(structType.Name);

        sb.AppendLine($"        struct {structName} {{");
        foreach (var prop in structType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var fieldAttr = prop.GetCustomAttribute<VespaFieldAttribute>();
            var name = ResolveFieldName(prop, fieldAttr);
            var vespaType = fieldAttr?.FieldType is not null and not VespaFieldType.None
                ? ToVespaTypeName(fieldAttr.FieldType)
                : InferVespaType(prop.PropertyType);
            sb.AppendLine($"            field {name} type {vespaType} {{}}");
        }
        sb.AppendLine("        }");
    }

    /// <summary>
    /// Returns the struct type if the CLR type (or its element type for arrays/lists) has [VespaStruct], else null.
    /// </summary>
    private static Type? ResolveStructType(Type clrType)
    {
        var underlying = Nullable.GetUnderlyingType(clrType) ?? clrType;

        // Direct struct type
        if (underlying.GetCustomAttribute<VespaStructAttribute>() is not null)
            return underlying;

        // Array of structs
        if (underlying.IsArray)
        {
            var elem = underlying.GetElementType()!;
            if (elem.GetCustomAttribute<VespaStructAttribute>() is not null)
                return elem;
        }

        // List<StructType> / IList<StructType>
        if (underlying.IsGenericType)
        {
            var gtd = underlying.GetGenericTypeDefinition();
            if (gtd == typeof(List<>) || gtd == typeof(IList<>) || gtd == typeof(IEnumerable<>))
            {
                var elem = underlying.GetGenericArguments()[0];
                if (elem.GetCustomAttribute<VespaStructAttribute>() is not null)
                    return elem;
            }
        }

        return null;
    }

    internal static string ToSnakeCase(string name) => VespaNaming.ToSnakeCase(name);

    /// <summary>/
    /// Resolves the Vespa field name from <see cref="VespaFieldAttribute.Name"/>
    /// or falls back to snake_case of the property name (matching JSON serialization).
    /// </summary>
    private static string ResolveFieldName(PropertyInfo prop, VespaFieldAttribute? fieldAttr) =>
        fieldAttr?.Name ?? ToSnakeCase(prop.Name);

    private static string GenerateServicesXml(
        IEnumerable<(string DocType, bool Global, DocumentMode Mode)> docInfos,
        ApplicationPackageOptions opts)
    {
        var docs = string.Join("\n", docInfos.Select(d =>
        {
            var mode = d.Mode switch
            {
                DocumentMode.Streaming => "streaming",
                DocumentMode.StoreOnly => "store-only",
                _ => "index"
            };
            var global = d.Global ? " global=\"true\"" : "";
            return $"            <document type=\"{d.DocType}\" mode=\"{mode}\"{global}/>";
        }));

        var nodes = string.Join("\n", Enumerable.Range(0, opts.NodeCount).Select(i =>
            $"""      <node hostalias="node{i + 1}" distribution-key="{i}"/>"""));

        var containerExtras = "";
        if (opts.ContainerOptions is { Count: > 0 })
            containerExtras = "\n" + string.Join("\n", opts.ContainerOptions.Values.Select(v => $"    {v.Trim()}"));

        var contentExtras = "";
        if (opts.ContentOptions is { Count: > 0 })
            contentExtras = "\n" + string.Join("\n", opts.ContentOptions.Values.Select(v => $"    {v.Trim()}"));

        return $"""
<?xml version="1.0" encoding="UTF-8"?>
<services version="1.0">
  <container id="default" version="1.0">
    <search/>
    <document-api/>{containerExtras}
  </container>
  <content id="content" version="1.0">
    <redundancy>{opts.Redundancy}</redundancy>
    <documents>
{docs}
    </documents>
    <nodes>
{nodes}
    </nodes>{contentExtras}
  </content>
</services>
""";
    }
}
