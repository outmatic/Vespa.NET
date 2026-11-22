using System.IO.Compression;
using System.Reflection;
using System.Text;
using Vespa.Models.Attributes;

namespace Vespa.Models.Schema;

/// <summary>Writes schemas, configuration and supporting files to an application package ZIP.</summary>
internal static class ApplicationPackageWriter
{
    internal static void Write(
        Stream output,
        IEnumerable<Type> documentTypes,
        ApplicationPackageOptions? options)
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
            var model = SchemaModelBuilder.Build(type);
            var schema = SchemaEmitter.Emit(model);
            var docAttr = type.GetCustomAttribute<VespaDocumentAttribute>()!;
            // ZipArchive happily writes duplicate entries, producing an ambiguous package
            if (!seenDocTypes.Add(docAttr.DocumentType))
                throw new InvalidOperationException(
                    $"Duplicate document type '{docAttr.DocumentType}': two of the supplied types map to the same schema file.");
            docInfos.Add((docAttr.DocumentType, docAttr.Global, docAttr.Mode, docAttr.Selection));

            var schemaPath = $"schemas/{docAttr.DocumentType}.sd";
            if (includedPaths.Contains(schemaPath))
                throw new InvalidOperationException($"SourcePath entry '{schemaPath}' collides with a generated package file.");
            generatedPaths.Add(schemaPath);
            var entry = zip.CreateEntry(schemaPath);
            using (var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
                writer.Write(schema);

            // ONNX assets keep precedence over constants sharing the same path.
            var assets = model.OnnxModels.Select(onnx => (onnx.File, onnx.SourcePath))
                .Concat(model.Constants.Select(constant => (constant.File, constant.SourcePath)));
            foreach (var (file, sourcePath) in assets)
            {
                if (sourcePath is null)
                    continue;
                if (generatedPaths.Contains(file))
                    throw new InvalidOperationException($"SourcePath entry '{file}' collides with a generated package file.");
                if (includedPaths.Add(file))
                    CopyFileToZip(zip, file, sourcePath);
            }
        }

        // Additional files do not replace automatically included assets.
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
            hostsWriter.Write(ApplicationPackageXml.GenerateHostsXml(opts));
        }

        var servicesEntry = zip.CreateEntry("services.xml");
        using var servicesWriter = new StreamWriter(servicesEntry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        servicesWriter.Write(opts.CustomServicesXml ?? ApplicationPackageXml.GenerateServicesXml(docInfos, opts));
    }

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
}
