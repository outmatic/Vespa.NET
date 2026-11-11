using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;
using MappedProperty = Vespa.VectorData.VespaCollectionModel.MappedProperty;

namespace Vespa.VectorData;

internal sealed class VespaVectorRecordMapper<TRecord> where TRecord : class
{
    internal VespaVectorRecordMapper(VectorStoreCollectionDefinition? definition, IReadOnlyCollection<string> requiredDataFields,
        IEmbeddingGenerator? defaultEmbeddingGenerator = null) =>
        Model = VespaCollectionModel.Build(typeof(TRecord), definition, requiredDataFields, defaultEmbeddingGenerator);

    internal VespaCollectionModel Model { get; }
    internal bool IsDynamic => Model.IsDynamic;
    internal Type KeyType => Model.Key.Type;
    internal IReadOnlyList<MappedProperty> Properties => Model.Properties;
    internal IReadOnlyList<MappedProperty> VectorProperties => Model.VectorProperties;

    internal MappedProperty ResolveVector(string? name = null)
    {
        if (name is not null)
            return VectorProperties.SingleOrDefault(p => p.Name == name)
                ?? throw new NotSupportedException($"Vector property '{name}' is not mapped.");
        return VectorProperties.Count switch
        {
            0 => throw new NotSupportedException("This collection has no vector properties; only document operations are available."),
            1 => VectorProperties[0],
            _ => throw new NotSupportedException("Select a VectorProperty when the collection has multiple vector fields.")
        };
    }

    internal string Projection(bool includeVectors) => string.Join(", ",
        ReadProperties(includeVectors).Select(p => p.StorageName).Prepend("documentid"));

    internal string FieldSet(string schema, bool includeVectors)
    {
        var fields = ReadProperties(includeVectors).Select(p => p.StorageName).ToArray();
        return fields.Length == 0 ? "[id]" : $"{schema}:{string.Join(',', fields)}";
    }

    private IEnumerable<MappedProperty> ReadProperties(bool includeVectors) =>
        Properties.Where(p => !p.IsVector || includeVectors && (IsDynamic || !p.GeneratesVectors));

    internal string GetKey(TRecord record) => FormatKey(GetValue(record, Model.Key), nameof(record));

    internal string FormatKey(object? key, string parameterName = "key")
    {
        if (key?.GetType() == KeyType)
        {
            if (key is string text && !string.IsNullOrWhiteSpace(text))
                return text;
            if (key is Guid guid)
                return guid.ToString("D");
            if (key is int or long or ulong)
                return ((IFormattable)key).ToString(null, CultureInfo.InvariantCulture);
        }
        throw new ArgumentException($"The key must be a nonempty value of type '{KeyType.Name}'.", parameterName);
    }

    private object ParseKey(string key)
    {
        if (KeyType == typeof(string))
            return key;
        object? parsed = KeyType == typeof(Guid) && Guid.TryParseExact(key, "D", out var guid) ? guid
            : KeyType == typeof(int) && int.TryParse(key, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer) ? integer
            : KeyType == typeof(long) && long.TryParse(key, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var longInteger) ? longInteger
            : KeyType == typeof(ulong) && ulong.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var unsignedInteger) ? unsignedInteger
            : null;
        if (parsed is null || FormatKey(parsed) != key)
            throw new VectorStoreException($"A {KeyType.Name} key requires its canonical invariant document ID representation.");
        return parsed;
    }

    internal Dictionary<string, object?> ToFields(TRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (IsDynamic)
            foreach (var name in ((Dictionary<string, object?>)(object)record).Keys)
                if (name != Model.Key.Name && !Properties.Any(p => p.Name == name))
                    throw new ArgumentException($"Record field '{name}' has no mapping in the collection definition.", nameof(record));
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in Properties)
        {
            var value = GetValue(record, property);
            if (value is null)
            {
                if (property.IsVector && !property.IsNullable)
                    throw new ArgumentException($"A vector or text to embed is required for '{property.Name}' when upserting a record.", nameof(record));
                if (Model.RequiredDataFields.Contains(property.StorageName))
                    throw new ArgumentException($"Required data field '{property.StorageName}' cannot be null or absent.", nameof(record));
                continue;
            }
            if (property.IsVector)
            {
                if (value is string && property.GeneratesVectors)
                {
                    fields.Add(property.StorageName, value);
                    continue;
                }
                var values = value switch
                {
                    float[] array => (float[])array.Clone(),
                    ReadOnlyMemory<float> memory => memory.ToArray(),
                    Embedding<float> embedding => embedding.Vector.ToArray(),
                    _ => throw new ArgumentException($"Vector field '{property.Name}' must contain a float vector or a configured string input.", nameof(record))
                };
                ValidateVector(property, values);
                value = Tensor(values);
            }
            else
                value = VespaCollectionModel.ToStorageValue(property.Type, value);
            fields.Add(property.StorageName, value);
        }
        return fields;
    }

    internal TRecord FromFields(string key, Dictionary<string, JsonElement> fields, bool includeVectors)
    {
        if (includeVectors)
            foreach (var property in VectorProperties.Where(p => !p.IsNullable && (IsDynamic || !p.GeneratesVectors)))
                if (!fields.TryGetValue(property.StorageName, out var tensor) || tensor.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                    throw new VectorStoreException($"Requested vector field '{property.StorageName}' is missing or null. Select a summary/field set containing the vector.");
        foreach (var name in Model.RequiredDataFields)
            if (!fields.TryGetValue(name, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                throw new VectorStoreException($"Required data field '{name}' is missing or null. Check the stored data and selected summary/field set.");
        var record = IsDynamic ? (TRecord)(object)new Dictionary<string, object?>(StringComparer.Ordinal) : (TRecord)Model.Constructor!.Invoke(null);
        SetValue(record, Model.Key, ParseKey(key));
        foreach (var property in Properties)
        {
            if (property.IsVector)
            {
                if (!includeVectors || !IsDynamic && property.GeneratesVectors)
                {
                    if (!IsDynamic)
                        SetValue(record, property, DefaultVectorValue(property));
                    continue;
                }
                if (!fields.TryGetValue(property.StorageName, out var tensor) || tensor.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                {
                    SetValue(record, property, null);
                    continue;
                }
                if (tensor.ValueKind == JsonValueKind.Object && !tensor.TryGetProperty("values", out tensor))
                    throw new VectorStoreException($"Expected a dense tensor values array for '{property.StorageName}'.");
                var vector = tensor.Deserialize<float[]>()
                    ?? throw new VectorStoreException($"Missing dense tensor values for '{property.StorageName}'.");
                try
                {
                    ValidateVector(property, vector);
                }
                catch (ArgumentException exception)
                {
                    throw new VectorStoreException($"Invalid stored vector in '{property.StorageName}'.", exception);
                }
                object mapped = property.Type == typeof(float[]) ? vector
                    : property.Type == typeof(Embedding<float>) ? new Embedding<float>(vector)
                    : new ReadOnlyMemory<float>(vector);
                SetValue(record, property, mapped);
            }
            else if (fields.TryGetValue(property.StorageName, out var value))
                SetValue(record, property, VespaCollectionModel.FromStorageValue(property.Type, value));
        }
        return record;
    }

    // An untyped null can select the float[] -> ReadOnlyMemory<float> conversion instead of a nullable null.
    private static ReadOnlyMemory<float>? DefaultVectorValue(MappedProperty property) =>
        property.Type == typeof(ReadOnlyMemory<float>) ? ReadOnlyMemory<float>.Empty : default(ReadOnlyMemory<float>?);

    internal static void ValidateVector(MappedProperty property, ReadOnlySpan<float> vector)
    {
        if (vector.Length != property.Dimensions)
            throw new ArgumentException($"Expected {property.Dimensions} vector dimensions for '{property.Name}', received {vector.Length}.", nameof(vector));
        var nonzero = false;
        foreach (var value in vector)
        {
            if (!float.IsFinite(value))
                throw new ArgumentException("Vectors must contain only finite values.", nameof(vector));
            nonzero |= value != 0;
        }
        if (!nonzero && property.DistanceFunction is DistanceFunction.CosineSimilarity or DistanceFunction.CosineDistance)
            throw new ArgumentException("A cosine vector cannot be zero.", nameof(vector));
    }

    internal MappedProperty DataProperty(string name) => Properties.SingleOrDefault(p => p.Name == name && !p.IsVector && p.IsFilterable)
        ?? throw new NotSupportedException($"Filtering on '{name}' is unsupported; use a data property marked IsIndexed.");

    internal MappedProperty Property(string name) => Properties.SingleOrDefault(p => p.Name == name)
        ?? throw new NotSupportedException($"Record field '{name}' is not mapped.");

    internal static Dictionary<string, object> Tensor(float[] values) => new() { ["values"] = values };

    private object? GetValue(TRecord record, MappedProperty property) => IsDynamic
        ? ((Dictionary<string, object?>)(object)record).GetValueOrDefault(property.Name)
        : property.Property!.GetValue(record);

    private void SetValue(TRecord record, MappedProperty property, object? value)
    {
        if (IsDynamic)
            ((Dictionary<string, object?>)(object)record)[property.Name] = value;
        else
            property.Property!.SetValue(record, value);
    }
}
