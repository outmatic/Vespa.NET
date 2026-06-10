using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vespa.Models;

/// <summary>
/// Represents a Vespa field-level update operation (e.g., assign, increment, add)
/// </summary>
[JsonConverter(typeof(FieldOperationConverter))]
public sealed record FieldOperation(string Type, object Value);

/// <summary>
/// Factory methods for common Vespa field update operations
/// </summary>
public static class FieldOp
{
    public static FieldOperation Assign(object value) => new("assign", value);
    public static FieldOperation Increment(double delta = 1) => new("increment", delta);
    public static FieldOperation Decrement(double delta = 1) => new("decrement", delta);
    public static FieldOperation Multiply(double factor) => new("multiply", factor);
    public static FieldOperation Divide(double divisor) => new("divide", divisor);
    public static FieldOperation Add(object element) => new("add", element);
    public static FieldOperation Remove(object element) => new("remove", element);
    public static FieldOperation Match(object key, FieldOperation innerOp) =>
        new("match", new MatchPayload(key, innerOp));

    /// <summary>
    /// Clears a field by assigning <c>null</c>.
    /// </summary>
    public static FieldOperation ClearField() => new("assign", ClearFieldSentinel.Instance);

    /// <summary>
    /// Tensor cell-level modify operation (replace, add, or multiply individual cells).
    /// </summary>
    /// <param name="operation">One of <c>"replace"</c>, <c>"add"</c>, or <c>"multiply"</c>.</param>
    /// <param name="cells">
    /// A list of cell updates, each with an <c>Address</c> dictionary and a <c>Value</c>.
    /// Example: <c>[new TensorCellUpdate(new() { ["x"] = "0" }, 5.0)]</c>
    /// </param>
    public static FieldOperation Modify(string operation, IReadOnlyList<TensorCellUpdate> cells) =>
        new("modify", new ModifyPayload(operation, cells));
}

/// <summary>
/// Internal payload for the "match" operation that targets a specific key in a map/weighted set
/// </summary>
internal sealed record MatchPayload(object Key, FieldOperation InnerOp);

/// <summary>
/// Represents a single tensor cell update with an address and a value.
/// </summary>
public sealed record TensorCellUpdate(Dictionary<string, string> Address, double Value);

/// <summary>
/// Internal payload for the "modify" operation (tensor cell-level updates).
/// </summary>
internal sealed record ModifyPayload(string Operation, IReadOnlyList<TensorCellUpdate> Cells);

/// <summary>
/// Helper for constructing Vespa fieldpath syntax keys for nested updates.
/// These keys are used in the field operations dictionary.
/// </summary>
public static class FieldPath
{
    /// <summary>Access a sub-field of a struct: <c>"address.city"</c></summary>
    public static string Struct(string field, string subField) => $"{field}.{subField}";

    /// <summary>Access a map entry by key: <c>"tags{mykey}"</c></summary>
    public static string Map(string field, string key) => $"{field}{{{key}}}";

    /// <summary>Access an array element by index: <c>"items[0]"</c></summary>
    public static string Array(string field, int index) => $"{field}[{index}]";

    /// <summary>Combine paths for deeply nested structures: <c>"address.lines[0]"</c></summary>
    public static string Combine(params string[] parts) => string.Join(".", parts);
}

/// <summary>Sentinel used to serialize <c>null</c> for ClearField.</summary>
internal sealed class ClearFieldSentinel
{
    internal static readonly ClearFieldSentinel Instance = new();
    private ClearFieldSentinel() { }
}

/// <summary>
/// Serializes FieldOperation as { "type": value } for the Vespa Document API
/// </summary>
file sealed class FieldOperationConverter : JsonConverter<FieldOperation>
{
    public override FieldOperation Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException("FieldOperation deserialization is not supported.");

    public override void Write(Utf8JsonWriter writer, FieldOperation value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        WriteOperation(writer, value, options);
        writer.WriteEndObject();
    }

    private static void WriteOperation(Utf8JsonWriter writer, FieldOperation value, JsonSerializerOptions options)
    {
        if (value.Value is MatchPayload match)
        {
            // Vespa format: {"match":{"element":<key>,"<innerOp>":<value>}}
            writer.WritePropertyName(value.Type);
            writer.WriteStartObject();
            writer.WritePropertyName("element");
            JsonSerializer.Serialize(writer, match.Key, options);
            WriteOperation(writer, match.InnerOp, options);
            writer.WriteEndObject();
        }
        else if (value.Value is ClearFieldSentinel)
        {
            writer.WritePropertyName(value.Type);
            writer.WriteNullValue();
        }
        else if (value.Value is ModifyPayload modify)
        {
            writer.WritePropertyName(value.Type);
            writer.WriteStartObject();
            writer.WriteString("operation", modify.Operation);
            writer.WritePropertyName("cells");
            writer.WriteStartArray();
            foreach (var cell in modify.Cells)
            {
                writer.WriteStartObject();
                writer.WritePropertyName("address");
                writer.WriteStartObject();
                foreach (var (dim, label) in cell.Address)
                    writer.WriteString(dim, label);
                writer.WriteEndObject();
                writer.WriteNumber("value", cell.Value);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        else
        {
            writer.WritePropertyName(value.Type);
            JsonSerializer.Serialize(writer, value.Value, options);
        }
    }
}
