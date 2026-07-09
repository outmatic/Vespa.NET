using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Security;
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
        var model = SchemaModelBuilder.Build(type);
        var writer = new SchemaWriter();
        EmitSchema(writer, model);
        return writer.ToString();
    }

    /// <summary>Generates a ZIP application package for <typeparamref name="T"/> and writes it to the output stream.</summary>
    public static void GenerateApplicationPackage<T>(Stream output, ApplicationPackageOptions? options = null)
        where T : class => GenerateApplicationPackage(output, typeof(T), options);

    /// <summary>Generates a ZIP application package for a single document type and writes it to the output stream.</summary>
    public static void GenerateApplicationPackage(Stream output, Type type, ApplicationPackageOptions? options = null)
        => GenerateApplicationPackage(output, [type], options);

    /// <summary>
    /// Generates a minimal Vespa application package ZIP containing
    /// <c>services.xml</c> and one <c>schemas/&lt;doctype&gt;.sd</c> per type
    /// (plus <c>hosts.xml</c> when <see cref="ApplicationPackageOptions.NodeCount"/> &gt; 1).
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
        ValidatePackageOptions(opts);

        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);

        var docInfos = new List<(string DocType, bool Global, DocumentMode Mode, string? Selection)>();
        var multiNode = opts.NodeCount > 1;

        // Paths of generated entries — AdditionalFiles must not clobber them.
        var generatedPaths = new HashSet<string>(StringComparer.Ordinal) { "services.xml" };
        if (multiNode)
            generatedPaths.Add("hosts.xml");

        var includedPaths = new HashSet<string>(StringComparer.Ordinal);
        var seenDocTypes = new HashSet<string>(StringComparer.Ordinal);

        foreach (var type in documentTypes)
        {
            var schema = GenerateSchema(type);
            var docAttr = type.GetCustomAttribute<VespaDocumentAttribute>()!;
            // ZipArchive happily writes duplicate entries, producing an ambiguous package
            if (!seenDocTypes.Add(docAttr.DocumentType))
                throw new InvalidOperationException(
                    $"Duplicate document type '{docAttr.DocumentType}': two of the supplied types map to the same schema file.");
            docInfos.Add((docAttr.DocumentType, docAttr.Global, docAttr.Mode, docAttr.Selection));

            var schemaPath = $"schemas/{docAttr.DocumentType}.sd";
            generatedPaths.Add(schemaPath);
            var entry = zip.CreateEntry(schemaPath);
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
                if (generatedPaths.Contains(zipPath))
                    throw new InvalidOperationException(
                        $"AdditionalFiles entry '{zipPath}' collides with a generated package file. " +
                        "Use ApplicationPackageOptions.CustomServicesXml to override services.xml, " +
                        "or pick a different entry path.");
                if (includedPaths.Add(zipPath))
                    CopyFileToZip(zip, zipPath, sourcePath);
            }
        }

        if (multiNode)
        {
            var hostsEntry = zip.CreateEntry("hosts.xml");
            using var hostsWriter = new StreamWriter(hostsEntry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            hostsWriter.Write(GenerateHostsXml(opts));
        }

        var servicesEntry = zip.CreateEntry("services.xml");
        using var servicesWriter = new StreamWriter(servicesEntry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        servicesWriter.Write(opts.CustomServicesXml ?? GenerateServicesXml(docInfos, opts));
    }

    // ── Schema emission ──────────────────────────────────────────────────────

    private static void EmitSchema(SchemaWriter w, SchemaModel model)
    {
        // A schema inheriting another schema must also declare the document
        // inheritance explicitly, otherwise Vespa rejects the package at deploy.
        var inherits = model.Inherits is not null ? $" inherits {model.Inherits}" : "";
        w.Open($"schema {model.DocType}{inherits}");
        w.Open($"document {model.DocType}{inherits}");

        foreach (var s in model.Structs)
        {
            w.Open($"struct {s.Name}");
            foreach (var (name, vespaType) in s.Fields)
                w.Line($"field {name} type {vespaType} {{}}");
            w.Close();
        }

        foreach (var field in model.Fields)
            EmitField(w, field);

        w.Close(); // document

        // Import fields (schema-level, outside document block)
        foreach (var imp in model.ImportFields)
            w.Line($"import field {imp.ReferenceField}.{imp.ForeignField} as {imp.Alias} {{}}");

        foreach (var constant in model.Constants)
        {
            w.Open($"constant {constant.Name}");
            w.Line($"file: {constant.File}");
            w.Line($"type: {constant.TensorType}");
            w.Close();
        }

        foreach (var onnx in model.OnnxModels)
        {
            w.Open($"onnx-model {onnx.Name}");
            w.Line($"file: {onnx.File}");
            w.Close();
        }

        foreach (var fieldSet in model.FieldSets)
        {
            w.Open($"fieldset {fieldSet.Name}");
            w.Line($"fields: {string.Join(", ", fieldSet.Fields)}");
            w.Close();
        }

        foreach (var summary in model.DocumentSummaries)
        {
            var summaryInherits = summary.Inherits is not null ? $" inherits {summary.Inherits}" : "";
            w.Open($"document-summary {summary.Name}{summaryInherits}");
            foreach (var field in summary.Fields)
                w.Line($"summary {field} {{}}");
            w.Close();
        }

        foreach (var profile in model.RankProfiles)
            EmitRankProfile(w, profile, model.TensorInputs);

        w.Close(); // schema
    }

    private static void EmitField(SchemaWriter w, SchemaFieldModel field)
    {
        w.Open($"field {field.Name} type {field.VespaType}");

        if (field.Indexing is not null)
            w.Line($"indexing: {field.Indexing}");

        if (field.IsTensor)
        {
            if (field.DistanceMetric is not null)
            {
                w.Open("attribute");
                w.Line($"distance-metric: {field.DistanceMetric}");
                w.Close();
            }

            if (field.HnswIndex)
            {
                w.Open("index");
                w.Open("hnsw");
                w.Line($"max-links-per-node: {field.MaxLinksPerNode}");
                w.Line($"neighbors-to-explore-at-insert: {field.NeighborsToExploreAtInsert}");
                w.Close();
                w.Close();
            }
        }

        if (field.PredicateArity is { } arity)
        {
            w.Open("index");
            w.Line($"arity: {arity}");
            if (field.PredicateLowerBound is { } lower)
                w.Line($"lower-bound: {lower}");
            if (field.PredicateUpperBound is { } upper)
                w.Line($"upper-bound: {upper}");
            w.Close();
        }

        if (field.Match is not null)
            w.Line($"match: {field.Match}");
        if (field.Stemming is not null)
            w.Line($"stemming: {field.Stemming}");
        if (field.Bolding)
            w.Line("bolding: on");
        if (field.FastSearch)
            w.Line("attribute: fast-search");
        if (field.Normalizing is not null)
            w.Line($"normalizing: {field.Normalizing}");

        // Field-level rank accepts only filter/normal; identity/about/tags/empty
        // belong to the separate rank-type element.
        if (field.Rank is not null)
            w.Line($"rank: {field.Rank}");
        if (field.RankTypeName is not null)
            w.Line($"rank-type: {field.RankTypeName}");

        foreach (var (path, indexing) in field.StructFields)
            w.Line($"struct-field {path} {{ indexing: {indexing} }}");

        w.Close();
    }

    private static void EmitRankProfile(
        SchemaWriter w,
        RankProfileModel profile,
        IReadOnlyList<(string QueryName, string TensorType)> tensorInputs)
    {
        var inherits = profile.Inherits is not null ? $" inherits {profile.Inherits}" : "";
        w.Open($"rank-profile {profile.Name}{inherits}");

        if (profile.EmitInputs && tensorInputs.Count > 0)
        {
            w.Open("inputs");
            foreach (var (queryName, tensorType) in tensorInputs)
                w.Line($"{queryName} {tensorType}");
            w.Close();
        }

        if (profile.FirstPhase is not null)
        {
            w.Open("first-phase");
            w.Line($"expression: {profile.FirstPhase}");
            w.Close();
        }

        if (profile.SecondPhase is not null)
        {
            w.Open("second-phase");
            w.Line($"expression: {profile.SecondPhase}");
            if (profile.SecondPhaseRerankCount > 0)
                w.Line($"rerank-count: {profile.SecondPhaseRerankCount}");
            w.Close();
        }

        if (profile.GlobalPhase is not null)
        {
            w.Open("global-phase");
            w.Line($"expression: {profile.GlobalPhase}");
            if (profile.GlobalPhaseRerankCount > 0)
                w.Line($"rerank-count: {profile.GlobalPhaseRerankCount}");
            w.Close();
        }

        if (profile.MatchFeatures is not null)
            w.Line($"match-features: {profile.MatchFeatures}");
        if (profile.SummaryFeatures is not null)
            w.Line($"summary-features: {profile.SummaryFeatures}");

        if (profile.DiversityAttribute is not null)
        {
            w.Open("diversity");
            w.Line($"attribute: {profile.DiversityAttribute}");
            if (profile.DiversityMinGroups > 0)
                w.Line($"min-groups: {profile.DiversityMinGroups}");
            w.Close();
        }

        foreach (var function in profile.Functions)
        {
            // Vespa's schema grammar requires a parameter list even for zero-argument
            // functions ("function name() { … }") — a bare name fails deployment.
            var signature = function.Signature.Contains('(')
                ? function.Signature
                : $"{function.Signature}()";
            w.Open($"function {signature}");
            w.Line($"expression: {function.Expression}");
            w.Close();
        }

        w.Close();
    }

    // ── Package plumbing ─────────────────────────────────────────────────────

    private static void ValidatePackageOptions(ApplicationPackageOptions opts)
    {
        if (opts.NodeCount < 1)
            throw new ArgumentException("ApplicationPackageOptions.NodeCount must be at least 1.", nameof(opts));
        if (opts.Redundancy < 1)
            throw new ArgumentException("ApplicationPackageOptions.Redundancy must be at least 1.", nameof(opts));
        if (opts.Redundancy > opts.NodeCount)
            throw new ArgumentException(
                $"ApplicationPackageOptions.Redundancy ({opts.Redundancy}) cannot exceed NodeCount ({opts.NodeCount}).",
                nameof(opts));
        if (opts.HostNames is not null && opts.HostNames.Count != opts.NodeCount)
            throw new ArgumentException(
                $"ApplicationPackageOptions.HostNames has {opts.HostNames.Count} entries but NodeCount is {opts.NodeCount}; the counts must match.",
                nameof(opts));
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

    private static string XmlEscape(string value) => SecurityElement.Escape(value)!;

    private static string GenerateServicesXml(
        IReadOnlyList<(string DocType, bool Global, DocumentMode Mode, string? Selection)> docInfos,
        ApplicationPackageOptions opts)
    {
        var multiNode = opts.NodeCount > 1;
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        sb.Append("<services version=\"1.0\">\n");
        sb.Append("  <container id=\"default\" version=\"1.0\">\n");
        sb.Append("    <search/>\n");
        sb.Append("    <document-api/>\n");

        if (opts.ContainerOptions is { Count: > 0 })
        {
            foreach (var fragment in opts.ContainerOptions.Values)
                sb.Append("    ").Append(fragment.Trim()).Append('\n');
        }

        if (multiNode)
        {
            sb.Append("    <nodes>\n");
            for (var i = 0; i < opts.NodeCount; i++)
                sb.Append(CultureInfo.InvariantCulture, $"      <node hostalias=\"node{i + 1}\"/>\n");
            sb.Append("    </nodes>\n");
        }

        sb.Append("  </container>\n");
        sb.Append("  <content id=\"content\" version=\"1.0\">\n");
        sb.Append(CultureInfo.InvariantCulture, $"    <redundancy>{opts.Redundancy}</redundancy>\n");

        // Any per-document selection implies garbage collection: documents no longer
        // matching their selection are removed by the content nodes.
        var garbageCollection = docInfos.Any(d => d.Selection is not null)
            ? " garbage-collection=\"true\""
            : "";
        sb.Append(CultureInfo.InvariantCulture, $"    <documents{garbageCollection}>\n");

        foreach (var (docType, global, docMode, selection) in docInfos)
        {
            var mode = docMode switch
            {
                DocumentMode.Streaming => "streaming",
                DocumentMode.StoreOnly => "store-only",
                _ => "index"
            };
            var globalAttr = global ? " global=\"true\"" : "";
            var selectionAttr = selection is not null ? $" selection=\"{XmlEscape(selection)}\"" : "";
            sb.Append(CultureInfo.InvariantCulture, $"      <document type=\"{XmlEscape(docType)}\" mode=\"{mode}\"{globalAttr}{selectionAttr}/>\n");
        }

        sb.Append("    </documents>\n");
        sb.Append("    <nodes>\n");
        for (var i = 0; i < opts.NodeCount; i++)
            sb.Append(CultureInfo.InvariantCulture, $"      <node hostalias=\"node{i + 1}\" distribution-key=\"{i}\"/>\n");
        sb.Append("    </nodes>\n");

        if (opts.ContentOptions is { Count: > 0 })
        {
            foreach (var fragment in opts.ContentOptions.Values)
                sb.Append("    ").Append(fragment.Trim()).Append('\n');
        }

        sb.Append("  </content>\n");
        sb.Append("</services>\n");
        return sb.ToString();
    }

    private static string GenerateHostsXml(ApplicationPackageOptions opts)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        sb.Append("<hosts>\n");
        for (var i = 0; i < opts.NodeCount; i++)
        {
            var alias = $"node{i + 1}";
            var hostName = opts.HostNames is not null ? opts.HostNames[i] : alias;
            sb.Append(CultureInfo.InvariantCulture, $"  <host name=\"{XmlEscape(hostName)}\">\n");
            sb.Append(CultureInfo.InvariantCulture, $"    <alias>{XmlEscape(alias)}</alias>\n");
            sb.Append("  </host>\n");
        }

        sb.Append("</hosts>\n");
        return sb.ToString();
    }
}
