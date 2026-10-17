using System.Collections;
using System.Globalization;
using System.Text.Json;

namespace Vespa.Models.Tensors;

/// <summary>Writes Vespa tensors using the short form appropriate to their storage format.</summary>
internal static class TensorJsonWriter
{
    internal static void Write(Utf8JsonWriter writer, VespaTensor? value)
    {
        if (value == null)
        {
            writer.WriteNullValue();
            return;
        }

        switch (value.Format)
        {
            case TensorFormat.IndexedDense:
                WriteIndexedDense(writer, value);
                break;

            case TensorFormat.MappedSingle:
                WriteMappedSingle(writer, value);
                break;

            case TensorFormat.MixedSingleSparse:
                WriteMixedSingleSparse(writer, value);
                break;

            case TensorFormat.MixedMultiSparse:
                WriteMixedMultiSparse(writer, value);
                break;

            case TensorFormat.Verbose:
                WriteVerbose(writer, value);
                break;

            default:
                throw new JsonException($"Unsupported tensor format: {value.Format}");
        }
    }

    private static void WriteIndexedDense(Utf8JsonWriter writer, VespaTensor tensor)
    {
        if (tensor.InternalDenseValues == null)
            throw new JsonException("InternalDenseValues is null for IndexedDense format");

        writer.WriteStartArray();

        foreach (var val in tensor.InternalDenseValues)
            WriteNumber(writer, val);

        writer.WriteEndArray();
    }

    private static void WriteMappedSingle(Utf8JsonWriter writer, VespaTensor tensor)
    {
        if (tensor.InternalMappedValues == null)
            throw new JsonException("InternalMappedValues is null for MappedSingle format");

        writer.WriteStartObject();

        // Fast paths for the storage types produced by this converter
        if (tensor.InternalMappedValues is Dictionary<string, float> floatDict)
        {
            foreach (var kvp in floatDict)
                writer.WriteNumber(kvp.Key, kvp.Value);
        }
        else if (tensor.InternalMappedValues is Dictionary<string, double> doubleDict)
        {
            foreach (var kvp in doubleDict)
                writer.WriteNumber(kvp.Key, kvp.Value);
        }
        else if (tensor.InternalMappedValues is Dictionary<string, sbyte> sbyteDict)
        {
            foreach (var kvp in sbyteDict)
                writer.WriteNumber(kvp.Key, kvp.Value);
        }
        else if (tensor.InternalMappedValues is Dictionary<string, Half> halfDict)
        {
            foreach (var kvp in halfDict)
                writer.WriteNumber(kvp.Key, (float)kvp.Value);
        }
        else if (tensor.InternalMappedValues is IDictionary genericDict)
        {
            // Fallback for any other numeric element type set via SetMappedValues<T>
            foreach (DictionaryEntry entry in genericDict)
            {
                if (entry.Key is not string key)
                    throw new NotSupportedException(
                        $"Mapped tensor keys must be strings, got {entry.Key?.GetType().ToString() ?? "null"}.");
                writer.WritePropertyName(key);
                WriteNumber(writer, entry.Value!);
            }
        }
        else
            throw new NotSupportedException(
                $"Mapped tensor values of type {tensor.InternalMappedValues.GetType()} cannot be serialized.");

        writer.WriteEndObject();
    }

    private static void WriteMixedSingleSparse(Utf8JsonWriter writer, VespaTensor tensor)
    {
        if (tensor.InternalMixedSingleSparse == null)
            throw new JsonException("InternalMixedSingleSparse is null for MixedSingleSparse format");

        writer.WriteStartObject();

        // Fast paths for the storage types produced by this converter
        if (tensor.InternalMixedSingleSparse is Dictionary<string, float[]> floatDict)
        {
            foreach (var kvp in floatDict)
            {
                writer.WritePropertyName(kvp.Key);
                writer.WriteStartArray();
                foreach (var val in kvp.Value)
                    writer.WriteNumberValue(val);
                writer.WriteEndArray();
            }
        }
        else if (tensor.InternalMixedSingleSparse is Dictionary<string, double[]> doubleDict)
        {
            foreach (var kvp in doubleDict)
            {
                writer.WritePropertyName(kvp.Key);
                writer.WriteStartArray();
                foreach (var val in kvp.Value)
                    writer.WriteNumberValue(val);
                writer.WriteEndArray();
            }
        }
        else if (tensor.InternalMixedSingleSparse is Dictionary<string, sbyte[]> sbyteDict)
        {
            foreach (var kvp in sbyteDict)
            {
                writer.WritePropertyName(kvp.Key);
                writer.WriteStartArray();
                foreach (var val in kvp.Value)
                    writer.WriteNumberValue(val);
                writer.WriteEndArray();
            }
        }
        else if (tensor.InternalMixedSingleSparse is Dictionary<string, Half[]> halfDict)
        {
            foreach (var kvp in halfDict)
            {
                writer.WritePropertyName(kvp.Key);
                writer.WriteStartArray();
                foreach (var val in kvp.Value)
                    writer.WriteNumberValue((float)val);
                writer.WriteEndArray();
            }
        }
        else if (tensor.InternalMixedSingleSparse is IDictionary genericDict)
        {
            // Fallback for any other numeric element type set via SetMixedSingleSparse<T>
            foreach (DictionaryEntry entry in genericDict)
            {
                if (entry.Key is not string key)
                    throw new NotSupportedException(
                        $"Mixed tensor keys must be strings, got {entry.Key?.GetType().ToString() ?? "null"}.");
                if (entry.Value is not Array values)
                    throw new NotSupportedException(
                        $"Mixed tensor values must be arrays, got {entry.Value?.GetType().ToString() ?? "null"}.");
                writer.WritePropertyName(key);
                writer.WriteStartArray();
                foreach (var val in values)
                    WriteNumber(writer, val!);
                writer.WriteEndArray();
            }
        }
        else
            throw new NotSupportedException(
                $"Mixed tensor values of type {tensor.InternalMixedSingleSparse.GetType()} cannot be serialized.");

        writer.WriteEndObject();
    }

    private static void WriteMixedMultiSparse(Utf8JsonWriter writer, VespaTensor tensor)
    {
        if (tensor.Blocks == null)
            throw new JsonException("Blocks is null for MixedMultiSparse format");

        writer.WriteStartObject();
        writer.WritePropertyName("blocks");
        writer.WriteStartArray();

        foreach (var block in tensor.Blocks)
        {
            writer.WriteStartObject();

            writer.WritePropertyName("address");
            writer.WriteStartObject();
            foreach (var kvp in block.Address)
                writer.WriteString(kvp.Key, kvp.Value);
            writer.WriteEndObject();

            writer.WritePropertyName("values");
            writer.WriteStartArray();
            foreach (var val in block.InternalValues)
                WriteNumber(writer, val);
            writer.WriteEndArray();

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteVerbose(Utf8JsonWriter writer, VespaTensor tensor)
    {
        if (tensor.Cells == null)
            throw new JsonException("Cells is null for Verbose format");

        writer.WriteStartArray();

        foreach (var cell in tensor.Cells)
        {
            writer.WriteStartObject();

            writer.WritePropertyName("address");
            writer.WriteStartObject();
            foreach (var kvp in cell.Address)
                writer.WriteString(kvp.Key, kvp.Value);
            writer.WriteEndObject();

            writer.WritePropertyName("value");
            WriteNumber(writer, cell.Value);

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteNumber(Utf8JsonWriter writer, object value)
    {
        switch (value)
        {
            case float f:
                writer.WriteNumberValue(f);
                break;
            case double d:
                writer.WriteNumberValue(d);
                break;
            case sbyte sb:
                writer.WriteNumberValue(sb);
                break;
            case Half h:
                writer.WriteNumberValue((float)h);
                break;
            default:
                double converted;
                try
                {
                    converted = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                }
                catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException or ArgumentNullException)
                {
                    throw new NotSupportedException(
                        $"Tensor cell value of type {value?.GetType().ToString() ?? "null"} cannot be serialized as a number.", ex);
                }
                writer.WriteNumberValue(converted);
                break;
        }
    }
}
