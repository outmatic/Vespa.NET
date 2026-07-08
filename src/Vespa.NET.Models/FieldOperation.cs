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
    /// Serializes as <c>{"modify":{"operation":…,"cells":[{"address":…,"value":…},…]}}</c>.
    /// </summary>
    /// <param name="operation">One of <c>"replace"</c>, <c>"add"</c>, or <c>"multiply"</c>.</param>
    /// <param name="cells">
    /// A list of cell updates, each with an <c>Address</c> dictionary and a <c>Value</c>.
    /// Example: <c>[new TensorCellUpdate(new() { ["x"] = "0" }, 5.0)]</c>
    /// </param>
    /// <param name="create">
    /// When <see langword="true"/>, treat the tensor as empty (all cells zero) if the field
    /// is not set, so the modify applies instead of being ignored. Serializes as
    /// <c>"create": true</c> inside the modify object.
    /// </param>
    public static FieldOperation Modify(string operation, IReadOnlyList<TensorCellUpdate> cells, bool create = false) =>
        new("modify", new ModifyPayload(operation, cells, null, null, create));

    /// <summary>
    /// Tensor cell-level modify operation using the short-form <c>cells</c> object for
    /// tensors with a single mapped dimension. Serializes as
    /// <c>{"modify":{"operation":…,"cells":{"label":value,…}}}</c>.
    /// </summary>
    /// <param name="operation">One of <c>"replace"</c>, <c>"add"</c>, or <c>"multiply"</c>.</param>
    /// <param name="cells">Map from the single mapped dimension's label to the cell value.</param>
    /// <param name="create">See <see cref="Modify(string, IReadOnlyList{TensorCellUpdate}, bool)"/>.</param>
    public static FieldOperation ModifyCells(string operation, IReadOnlyDictionary<string, double> cells, bool create = false) =>
        new("modify", new ModifyPayload(operation, null, cells, null, create));

    /// <summary>
    /// Tensor block-level modify operation for mixed tensors (one mapped plus indexed
    /// dimensions). Serializes as <c>{"modify":{"operation":…,"blocks":{"label":[…],…}}}</c>,
    /// replacing whole dense blocks addressed by the mapped dimension's label.
    /// </summary>
    /// <param name="operation">One of <c>"replace"</c>, <c>"add"</c>, or <c>"multiply"</c>.</param>
    /// <param name="blocks">Map from the mapped dimension's label to the dense block values.</param>
    /// <param name="create">See <see cref="Modify(string, IReadOnlyList{TensorCellUpdate}, bool)"/>.</param>
    public static FieldOperation ModifyBlocks(string operation, IReadOnlyDictionary<string, IReadOnlyList<double>> blocks, bool create = false) =>
        new("modify", new ModifyPayload(operation, null, null, blocks, create));

    /// <summary>
    /// Tensor add-cells update: adds cells to a tensor with mapped dimensions, keeping
    /// existing cells (new cells overwrite cells with the same address). Serializes as
    /// <c>{"add":{"cells":[{"address":…,"value":…},…]}}</c>.
    /// </summary>
    public static FieldOperation TensorAdd(IReadOnlyList<TensorCellUpdate> cells) =>
        new("add", new TensorAddPayload(cells));

    /// <summary>
    /// Tensor remove-cells update: removes the cells (or, for mixed tensors, the dense
    /// blocks) at the given addresses. Serializes as
    /// <c>{"remove":{"addresses":[{…},…]}}</c>.
    /// </summary>
    public static FieldOperation TensorRemove(IReadOnlyList<IReadOnlyDictionary<string, string>> addresses) =>
        new("remove", new TensorRemovePayload(addresses));
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
/// Internal payload for the "modify" operation (tensor cell/block-level updates).
/// Exactly one of <see cref="Cells"/>, <see cref="CellsShortForm"/>, <see cref="Blocks"/> is set.
/// </summary>
internal sealed record ModifyPayload(
    string Operation,
    IReadOnlyList<TensorCellUpdate>? Cells,
    IReadOnlyDictionary<string, double>? CellsShortForm,
    IReadOnlyDictionary<string, IReadOnlyList<double>>? Blocks,
    bool Create);

/// <summary>Internal payload for the tensor "add" update (<c>{"add":{"cells":[…]}}</c>).</summary>
internal sealed record TensorAddPayload(IReadOnlyList<TensorCellUpdate> Cells);

/// <summary>Internal payload for the tensor "remove" update (<c>{"remove":{"addresses":[…]}}</c>).</summary>
internal sealed record TensorRemovePayload(IReadOnlyList<IReadOnlyDictionary<string, string>> Addresses);

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
            if (modify.Create)
                writer.WriteBoolean("create", true);
            if (modify.Cells is not null)
            {
                writer.WritePropertyName("cells");
                WriteCellArray(writer, modify.Cells);
            }
            else if (modify.CellsShortForm is not null)
            {
                writer.WritePropertyName("cells");
                writer.WriteStartObject();
                foreach (var (label, cellValue) in modify.CellsShortForm)
                    writer.WriteNumber(label, cellValue);
                writer.WriteEndObject();
            }
            else if (modify.Blocks is not null)
            {
                writer.WritePropertyName("blocks");
                writer.WriteStartObject();
                foreach (var (label, blockValues) in modify.Blocks)
                {
                    writer.WritePropertyName(label);
                    writer.WriteStartArray();
                    foreach (var blockValue in blockValues)
                        writer.WriteNumberValue(blockValue);
                    writer.WriteEndArray();
                }
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }
        else if (value.Value is TensorAddPayload tensorAdd)
        {
            writer.WritePropertyName(value.Type);
            writer.WriteStartObject();
            writer.WritePropertyName("cells");
            WriteCellArray(writer, tensorAdd.Cells);
            writer.WriteEndObject();
        }
        else if (value.Value is TensorRemovePayload tensorRemove)
        {
            writer.WritePropertyName(value.Type);
            writer.WriteStartObject();
            writer.WritePropertyName("addresses");
            writer.WriteStartArray();
            foreach (var address in tensorRemove.Addresses)
                WriteAddress(writer, address);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        else
        {
            writer.WritePropertyName(value.Type);
            JsonSerializer.Serialize(writer, value.Value, options);
        }
    }

    private static void WriteCellArray(Utf8JsonWriter writer, IReadOnlyList<TensorCellUpdate> cells)
    {
        writer.WriteStartArray();
        foreach (var cell in cells)
        {
            writer.WriteStartObject();
            writer.WritePropertyName("address");
            WriteAddress(writer, cell.Address);
            writer.WriteNumber("value", cell.Value);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteAddress(Utf8JsonWriter writer, IReadOnlyDictionary<string, string> address)
    {
        writer.WriteStartObject();
        foreach (var (dimension, label) in address)
            writer.WriteString(dimension, label);
        writer.WriteEndObject();
    }
}
