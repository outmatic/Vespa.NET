using System.Text.Json;
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
    /// <exception cref="InvalidOperationException">A tensor property has no Vespa tensor converter.</exception>
    public VespaJsonModels With<T>(JsonTypeInfo<T> typeInfo, Func<T, string, T>? applyId = null) where T : class
    {
        ArgumentNullException.ThrowIfNull(typeInfo);

        foreach (var property in typeInfo.Properties)
        {
            if (property.PropertyType != typeof(VespaTensor) || (property.Get is null && property.Set is null))
                continue;

            var converter = property.CustomConverter;
            if (converter is null && typeInfo.Options.TryGetTypeInfo(typeof(VespaTensor), out var tensorInfo))
                converter = tensorInfo.Converter;

            if (converter is not VespaTensorConverter)
                throw new InvalidOperationException(
                    $"Tensor property '{property.Name}' on model '{typeof(T)}' requires VespaTensorConverter. " +
                    "Add [JsonConverter(typeof(VespaTensorConverter))] to the property or include " +
                    "VespaTensorConverter in the JSON context's Converters.");
        }

        return new VespaJsonModels(new Dictionary<Type, object>(_models)
        {
            [typeof(T)] = new ModelJson<T>(typeInfo, applyId)
        });
    }

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
