using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Vespa.Models;
using Vespa.Models.Tensors;

namespace Vespa;

/// <summary>
/// Stores JSON metadata for the application models used by a Vespa client.
/// </summary>
public sealed class VespaJsonModels
{
    private readonly Dictionary<Type, object> _models;

    /// <summary>Creates an empty set of model registrations.</summary>
    public VespaJsonModels() : this([]) { }

    private VespaJsonModels(Dictionary<Type, object> models) => _models = models;

    /// <summary>Registers the field dictionaries used by the VectorData and Agent Framework providers.</summary>
    public VespaJsonModels WithFieldMaps() =>
        With(VespaProtocolJsonContext.Default.WriteFields).With(VespaProtocolJsonContext.Default.ReadFields);

    /// <summary>
    /// Returns a new set with the supplied metadata for <typeparamref name="T"/>.
    /// Configure the wire names and any ignored properties in the JSON context.
    /// </summary>
    /// <param name="typeInfo">JSON metadata for the fields model.</param>
    /// <param name="applyId">Optional function that applies Vespa's short ID to the model returned by a read.</param>
    /// <exception cref="InvalidOperationException">A tensor reachable through the registered metadata has no Vespa tensor converter.</exception>
    public VespaJsonModels With<T>(JsonTypeInfo<T> typeInfo, Func<T, string, T>? applyId = null) where T : class
    {
        ArgumentNullException.ThrowIfNull(typeInfo);

        ValidateTensorConverters(typeInfo);

        return new VespaJsonModels(new Dictionary<Type, object>(_models)
        {
            [typeof(T)] = new ModelJson<T>(typeInfo, applyId)
        });
    }

    private static void ValidateTensorConverters(JsonTypeInfo root)
    {
        var visited = new HashSet<Type>();
        Visit(root, string.Empty);

        void Visit(JsonTypeInfo metadata, string path)
        {
            if (!visited.Add(metadata.Type))
                return;

            if (metadata.Type == typeof(VespaTensor))
            {
                RequireConverter(metadata.Converter, path.Length == 0 ? metadata.Type.Name : path);
                return;
            }

            foreach (var property in metadata.Properties)
            {
                if (property.Get is null && property.Set is null)
                    continue;

                var propertyPath = path.Length == 0 ? property.Name : path + "." + property.Name;
                if (property.CustomConverter is { } converter)
                {
                    if (property.PropertyType == typeof(VespaTensor))
                        RequireConverter(converter, propertyPath);
                    continue;
                }

                VisitType(property.PropertyType, metadata.Options, propertyPath);
            }

            if (GetCollectionElementType(metadata) is { } elementType)
                VisitType(elementType, metadata.Options, path + "[]");
            if (Nullable.GetUnderlyingType(metadata.Type) is { } underlyingType)
                VisitType(underlyingType, metadata.Options, path);
            if (metadata.PolymorphismOptions is { } polymorphism)
                foreach (var derived in polymorphism.DerivedTypes)
                    VisitType(derived.DerivedType, metadata.Options, path);
        }

        void VisitType(Type type, JsonSerializerOptions options, string path)
        {
            if (type == typeof(object) || type == typeof(JsonElement) || typeof(JsonNode).IsAssignableFrom(type) ||
                type.IsInterface || type.IsAbstract || visited.Contains(type))
                return;

            if (options.TryGetTypeInfo(type, out var metadata))
                Visit(metadata, path);
            else if (type == typeof(VespaTensor))
                RequireConverter(null, path);
        }

        void RequireConverter(JsonConverter? converter, string path)
        {
            if (converter is not VespaTensorConverter)
                throw new InvalidOperationException(
                    $"Tensor property '{path}' on model '{root.Type}' requires VespaTensorConverter. " +
                    "Add [JsonConverter(typeof(VespaTensorConverter))] to the property or include " +
                    "VespaTensorConverter in the JSON context's Converters.");
        }
    }

#if NET8_0
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2075",
        Justification = "The registered JSON collection converter already uses the collection's enumerable or dictionary interface.")]
    private static Type? GetCollectionElementType(JsonTypeInfo metadata)
    {
        if (metadata.Kind is not (JsonTypeInfoKind.Enumerable or JsonTypeInfoKind.Dictionary))
            return null;

        var type = metadata.Type;
        if (type.IsArray)
            return type.GetElementType();
        if (type.IsGenericType)
        {
            var definition = type.GetGenericTypeDefinition();
            if (definition == typeof(Memory<>) || definition == typeof(ReadOnlyMemory<>))
                return type.GetGenericArguments()[0];
        }

        foreach (var contract in type.GetInterfaces())
        {
            if (!contract.IsGenericType)
                continue;

            var contractType = contract.GetGenericTypeDefinition();
            if (metadata.Kind == JsonTypeInfoKind.Dictionary)
            {
                if (contractType == typeof(IDictionary<,>) || contractType == typeof(IReadOnlyDictionary<,>))
                    return contract.GetGenericArguments()[1];
            }
            else if (contractType == typeof(IEnumerable<>))
            {
                return contract.GetGenericArguments()[0];
            }
        }

        return null;
    }
#else
    private static Type? GetCollectionElementType(JsonTypeInfo metadata) => metadata.ElementType;
#endif

    internal static ModelJson<T>? Resolve<T>(VespaJsonModels? registrations, string operation) where T : class
    {
        if (registrations is null && VespaJsonReflection.IsEnabled)
            return null;
        if (registrations is not null && registrations._models.TryGetValue(typeof(T), out var model))
            return (ModelJson<T>)model;

        throw new InvalidOperationException(
            $"Register JSON metadata for '{typeof(T)}' in VespaClientOptions.JsonModels to use {operation}.");
    }
}

internal sealed record ModelJson<T>(JsonTypeInfo<T> TypeInfo, Func<T, string, T>? ApplyId) where T : class
{
    internal T? ReadFields(JsonElement json, string fullId)
    {
        if (json.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;

        var fields = json.Deserialize(TypeInfo);
        if (fields is null || ApplyId is null || string.IsNullOrEmpty(fullId))
            return fields;

        var shortId = VespaDocumentId.GetUserSpecified(fullId);
        return shortId.Length == 0 ? fields : ApplyId(fields, shortId);
    }
}
