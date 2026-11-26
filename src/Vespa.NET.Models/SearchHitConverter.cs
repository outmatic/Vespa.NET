using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Vespa.Models;

/// <summary>
/// Creates <see cref="SearchHitConverter{T}"/> instances for <see cref="SearchHit{T}"/>.
/// </summary>
public sealed class SearchHitConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(SearchHit<>);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        if (!RuntimeFeature.IsDynamicCodeSupported)
            throw new NotSupportedException(
                "Register the fields model in VespaClientOptions.JsonModels to read search hits in Native AOT.");

        return (JsonConverter)Activator.CreateInstance(
            typeof(SearchHitConverter<>).MakeGenericType(typeToConvert.GetGenericArguments()[0]))!;
    }
}

/// <summary>
/// Converter for <see cref="SearchHit{T}"/>. Vespa's default JSON renderer emits
/// <c>matchfeatures</c>/<c>summaryfeatures</c> as pseudo-fields inside <c>fields</c>,
/// not at hit level; this converter lifts them onto
/// <see cref="SearchHit{T}.MatchFeatures"/>/<see cref="SearchHit{T}.SummaryFeatures"/>.
/// Hit-level occurrences (custom renderers) are still read; <c>fields</c> wins when
/// both are present. The <c>fields</c> object is deserialized into <typeparamref name="T"/>
/// unchanged, so a <typeparamref name="T"/> declaring the pseudo-fields itself keeps working.
/// </summary>
internal sealed class SearchHitConverter<T> : JsonConverter<SearchHit<T>> where T : class
{
    public override SearchHit<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        return Read(document.RootElement, options);
    }

    internal static SearchHit<T> Read(
        JsonElement root, JsonSerializerOptions options, Func<JsonElement, string, T?>? readFields = null)
    {
        var fullId = string.Empty;
        double relevance = 0;
        string? source = null;
        T? fields = null;

        if (root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
            fullId = id.GetString()!;
        if (root.TryGetProperty("relevance", out var rel) && rel.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
            relevance = rel.Deserialize(GetTypeInfo<double>(options));
        if (root.TryGetProperty("source", out var src) && src.ValueKind == JsonValueKind.String)
            source = src.GetString();

        if (root.TryGetProperty("fields", out var fieldsElement) && fieldsElement.ValueKind != JsonValueKind.Null)
            fields = readFields is null
                ? fieldsElement.Deserialize(GetTypeInfo<T>(options))
                : readFields(fieldsElement, fullId);

        return new SearchHit<T>
        {
            FullId = fullId,
            Relevance = relevance,
            Source = source,
            Fields = fields,
            MatchFeatures = ReadFeatureMap(root, fieldsElement, "matchfeatures"),
            SummaryFeatures = ReadFeatureMap(root, fieldsElement, "summaryfeatures")
        };
    }

    public override void Write(Utf8JsonWriter writer, SearchHit<T> value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("id", value.FullId);
        writer.WritePropertyName("relevance");
        JsonSerializer.Serialize(writer, value.Relevance, GetTypeInfo<double>(options));
        if (value.Source is not null)
            writer.WriteString("source", value.Source);
        if (value.Fields is not null)
        {
            writer.WritePropertyName("fields");
            JsonSerializer.Serialize(writer, value.Fields, GetTypeInfo<T>(options));
        }
        WriteFeatureMap(writer, "matchfeatures", value.MatchFeatures);
        WriteFeatureMap(writer, "summaryfeatures", value.SummaryFeatures);
        writer.WriteEndObject();
    }

    private static JsonTypeInfo<TValue> GetTypeInfo<TValue>(JsonSerializerOptions options) =>
        (JsonTypeInfo<TValue>)options.GetTypeInfo(typeof(TValue));

    private static Dictionary<string, JsonElement>? ReadFeatureMap(JsonElement root, JsonElement fields, string name)
    {
        if (fields.ValueKind != JsonValueKind.Object || !fields.TryGetProperty(name, out var element) ||
            element.ValueKind != JsonValueKind.Object)
        {
            if (!root.TryGetProperty(name, out element) || element.ValueKind != JsonValueKind.Object)
                return null;
        }

        var map = new Dictionary<string, JsonElement>();
        foreach (var property in element.EnumerateObject())
            map[property.Name] = property.Value.Clone(); // must outlive the parsed document

        return map;
    }

    private static void WriteFeatureMap(Utf8JsonWriter writer, string name, Dictionary<string, JsonElement>? features)
    {
        if (features is null)
            return;

        writer.WriteStartObject(name);
        foreach (var (key, value) in features)
        {
            writer.WritePropertyName(key);
            value.WriteTo(writer);
        }
        writer.WriteEndObject();
    }
}
