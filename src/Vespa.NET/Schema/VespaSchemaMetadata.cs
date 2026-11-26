using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace Vespa.Models.Schema;

/// <summary>Registers the CLR types needed to generate a Vespa application package with trimming enabled.</summary>
public sealed class VespaSchemaMetadata
{
    private readonly Type[] _documents;
    private readonly Dictionary<Type, PropertyInfo[]> _properties;

    /// <summary>Creates an empty set of document and struct registrations.</summary>
    public VespaSchemaMetadata() : this([], []) { }

    private VespaSchemaMetadata(Type[] documents, Dictionary<Type, PropertyInfo[]> properties)
    {
        _documents = documents;
        _properties = properties;
    }

    /// <summary>Returns a new set that includes this document type in generated packages.</summary>
    public VespaSchemaMetadata WithDocument<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>() =>
        With(typeof(T), document: true);

    /// <summary>Returns a new set that includes metadata for this Vespa struct type.</summary>
    public VespaSchemaMetadata WithStruct<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>() =>
        With(typeof(T), document: false);

    /// <summary>Generates a schema for a registered document type.</summary>
    public string GenerateSchema<T>(ApplicationPackageOptions? options = null)
    {
        var type = typeof(T);
        if (!_documents.Contains(type))
            throw new InvalidOperationException($"Register document type '{type}' with WithDocument<T>().");

        return SchemaEmitter.Emit(SchemaModelBuilder.Build(type, GetProperties, options?.RankProfiles?.GetValueOrDefault(type)));
    }

    /// <summary>Writes a ZIP containing the registered documents, services and any package assets.</summary>
    public void GenerateApplicationPackage(Stream output, ApplicationPackageOptions? options = null) =>
        ApplicationPackageWriter.Write(output, _documents, options, GetProperties);

    private VespaSchemaMetadata With(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] Type type, bool document)
    {
        var documents = document && !_documents.Contains(type) ? [.. _documents, type] : _documents;
        var properties = new Dictionary<Type, PropertyInfo[]>(_properties)
        {
            [type] = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
        };
        return new VespaSchemaMetadata(documents, properties);
    }

    private PropertyInfo[] GetProperties(Type type) =>
        _properties.TryGetValue(type, out var properties) ? properties : throw new InvalidOperationException(
            $"Register struct type '{type}' with WithStruct<T>() before generating the schema.");
}
