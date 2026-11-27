using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Vespa.Models.Attributes;

namespace Vespa.Models.Schema;

/// <summary>
/// Generates Vespa schema (<c>.sd</c>) files and application packages from C# types
/// decorated with <see cref="VespaDocumentAttribute"/>, <see cref="VespaFieldAttribute"/>,
/// and <see cref="VespaTensorAttribute"/>.
/// </summary>
public static class VespaSchemaBuilder
{
    internal const string RuntimeTypesRequireReflection =
        "Schema generation discovers document and nested struct members at runtime. Use VespaSchemaMetadata in trimmed or Native AOT applications.";

    /// <summary>Generates a Vespa <c>.sd</c> schema string for <typeparamref name="T"/>.</summary>
    [RequiresUnreferencedCode(RuntimeTypesRequireReflection)]
    public static string GenerateSchema<T>() where T : class => GenerateSchema(typeof(T));

    /// <summary>Generates a Vespa <c>.sd</c> schema string for the given type.</summary>
    [RequiresUnreferencedCode(RuntimeTypesRequireReflection)]
    public static string GenerateSchema(Type type) =>
        SchemaEmitter.Emit(SchemaModelBuilder.Build(type, GetProperties));

    /// <summary>Generates the schema for <typeparamref name="T"/> with its configured runtime rank profiles.</summary>
    [RequiresUnreferencedCode(RuntimeTypesRequireReflection)]
    public static string GenerateSchema<T>(ApplicationPackageOptions options) where T : class =>
        GenerateSchema(typeof(T), options);

    /// <summary>Generates the schema for a type with its configured runtime rank profiles.</summary>
    [RequiresUnreferencedCode(RuntimeTypesRequireReflection)]
    public static string GenerateSchema(Type type, ApplicationPackageOptions options)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(options);
        return SchemaEmitter.Emit(SchemaModelBuilder.Build(type, GetProperties, options.RankProfiles?.GetValueOrDefault(type)));
    }

    /// <summary>Generates a ZIP application package for <typeparamref name="T"/> and writes it to the output stream.</summary>
    [RequiresUnreferencedCode(RuntimeTypesRequireReflection)]
    public static void GenerateApplicationPackage<T>(Stream output, ApplicationPackageOptions? options = null)
        where T : class => GenerateApplicationPackage(output, typeof(T), options);

    /// <summary>Generates a ZIP application package for a single document type and writes it to the output stream.</summary>
    [RequiresUnreferencedCode(RuntimeTypesRequireReflection)]
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
    [RequiresUnreferencedCode(RuntimeTypesRequireReflection)]
    public static void GenerateApplicationPackage(
        Stream output,
        IEnumerable<Type> documentTypes,
        ApplicationPackageOptions? options = null) =>
        ApplicationPackageWriter.Write(output, documentTypes, options, GetProperties);

    [RequiresUnreferencedCode(RuntimeTypesRequireReflection)]
    private static PropertyInfo[] GetProperties(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
}
