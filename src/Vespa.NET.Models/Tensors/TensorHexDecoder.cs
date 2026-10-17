using System.Globalization;
using System.Text.Json;

namespace Vespa.Models.Tensors;

/// <summary>Decodes hex-encoded tensor cells using their declared numeric type.</summary>
internal static class TensorHexDecoder
{
    /// <summary>
    /// Decodes the documented hex string form for dense tensor values: each cell is the
    /// big-endian bit pattern of its value — 2 hex digits for int8, 4 for bfloat16,
    /// 8 for float, 16 for double. A type specification is required to determine cell width.
    /// </summary>
    internal static Array Decode(string hex, string? tensorType, Type valueType)
    {
        if (tensorType is null)
            throw new JsonException(
                "Hex tensor values require a tensor type. Use [VespaTensor] on the model property, " +
                "configure VespaTensorConverter with a type, or request the self-describing 'hex' format instead of 'hex-value'.");

        if (valueType == typeof(sbyte))
        {
            var values = new sbyte[HexCellCount(hex, 2)];
            for (var i = 0; i < values.Length; i++)
                values[i] = unchecked((sbyte)ParseHexCell(hex, i, 2));
            return values;
        }

        // bfloat16 is stored in memory as float but hex-encoded as 2 bytes
        // (the upper half of the float32 bit pattern).
        if (tensorType?.Contains("<bfloat16>", StringComparison.Ordinal) == true)
        {
            var values = new float[HexCellCount(hex, 4)];
            for (var i = 0; i < values.Length; i++)
                values[i] = BitConverter.UInt32BitsToSingle((uint)ParseHexCell(hex, i, 4) << 16);
            return values;
        }

        if (valueType == typeof(float))
        {
            var values = new float[HexCellCount(hex, 8)];
            for (var i = 0; i < values.Length; i++)
                values[i] = BitConverter.UInt32BitsToSingle((uint)ParseHexCell(hex, i, 8));
            return values;
        }

        var doubles = new double[HexCellCount(hex, 16)];
        for (var i = 0; i < doubles.Length; i++)
            doubles[i] = BitConverter.UInt64BitsToDouble(ParseHexCell(hex, i, 16));
        return doubles;
    }

    private static int HexCellCount(string hex, int digitsPerCell) =>
        hex.Length % digitsPerCell == 0
            ? hex.Length / digitsPerCell
            : throw new JsonException(
                $"Hex tensor value length {hex.Length} is not a multiple of {digitsPerCell} hex digits per cell");

    private static ulong ParseHexCell(string hex, int index, int digitsPerCell) =>
        ulong.TryParse(hex.AsSpan(index * digitsPerCell, digitsPerCell),
            NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new JsonException(
                $"Invalid hex digits in tensor value: '{hex.Substring(index * digitsPerCell, digitsPerCell)}'");
}
