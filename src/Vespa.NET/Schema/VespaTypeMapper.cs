namespace Vespa.Models.Schema;

/// <summary>Maps CLR scalar types and identifies supported collection shapes for schema generation.</summary>
internal static class VespaTypeMapper
{
    private static readonly HashSet<string> _primitiveVespaTypes =
        ["bool", "string", "int", "long", "byte", "float", "double"];

    /// <summary>Vespa primitive types — the only types allowed as struct-field attributes and map keys.</summary>
    internal static bool IsPrimitiveVespaType(string vespaType) => _primitiveVespaTypes.Contains(vespaType);

    /// <summary>
    /// Unwraps single-dimensional arrays and the common (read-only) collection shapes
    /// — <c>List&lt;T&gt;</c>, <c>IList&lt;T&gt;</c>, <c>ICollection&lt;T&gt;</c>,
    /// <c>IEnumerable&lt;T&gt;</c>, <c>IReadOnlyList&lt;T&gt;</c>, <c>IReadOnlyCollection&lt;T&gt;</c>,
    /// <c>HashSet&lt;T&gt;</c>, <c>ISet&lt;T&gt;</c>, <c>IReadOnlySet&lt;T&gt;</c> — to their element type.
    /// </summary>
    internal static bool TryGetElementType(Type type, out Type elementType)
    {
        if (type.IsArray && type.GetArrayRank() == 1)
        {
            elementType = type.GetElementType()!;
            return true;
        }

        if (type.IsGenericType)
        {
            var gtd = type.GetGenericTypeDefinition();
            if (gtd == typeof(List<>) || gtd == typeof(IList<>) || gtd == typeof(ICollection<>)
                || gtd == typeof(IEnumerable<>) || gtd == typeof(IReadOnlyList<>) || gtd == typeof(IReadOnlyCollection<>)
                || gtd == typeof(HashSet<>) || gtd == typeof(ISet<>) || gtd == typeof(IReadOnlySet<>))
            {
                elementType = type.GetGenericArguments()[0];
                return true;
            }
        }

        elementType = null!;
        return false;
    }

    /// <summary>Unwraps <c>Dictionary&lt;K,V&gt;</c>, <c>IDictionary&lt;K,V&gt;</c> and <c>IReadOnlyDictionary&lt;K,V&gt;</c>.</summary>
    internal static bool TryGetDictionaryTypes(Type type, out Type keyType, out Type valueType)
    {
        if (type.IsGenericType)
        {
            var gtd = type.GetGenericTypeDefinition();
            if (gtd == typeof(Dictionary<,>) || gtd == typeof(IDictionary<,>) || gtd == typeof(IReadOnlyDictionary<,>))
            {
                var args = type.GetGenericArguments();
                keyType = args[0];
                valueType = args[1];
                return true;
            }
        }

        keyType = null!;
        valueType = null!;
        return false;
    }

    /// <summary>
    /// Maps a scalar CLR type to its Vespa type name, or returns null for
    /// non-scalar types (collections, structs). Types that would silently
    /// corrupt data throw instead of guessing:
    /// C# <c>byte</c> (0–255 does not fit Vespa's signed byte), <c>ulong</c>
    /// (no unsigned 64-bit in Vespa) and enums (serialized as numbers by default).
    /// <c>Guid</c> and <c>char</c> map to <c>string</c> because System.Text.Json
    /// serializes them as JSON strings.
    /// </summary>
    internal static string? MapScalar(Type type, string context)
    {
        if (type.IsEnum)
            throw new NotSupportedException(
                $"Cannot map enum type '{type.Name}' ({context}): System.Text.Json serializes enums " +
                "as numbers by default and Vespa has no enum type. Use an int/long property, or serialize " +
                "the enum as a string (JsonStringEnumConverter on the property) and declare the field with " +
                "FieldType = VespaFieldType.String.");

        if (type == typeof(byte))
            throw new NotSupportedException(
                $"Cannot map C# 'byte' ({context}): it is unsigned (0..255) while Vespa 'byte' is signed " +
                "(-128..127), so values 128..255 would fail at feed time. Use 'sbyte' for a Vespa byte, " +
                "or 'short'/'int' for the full 0..255 range.");

        if (type == typeof(ulong))
            throw new NotSupportedException(
                $"Cannot map 'ulong' ({context}): Vespa has no unsigned 64-bit type. " +
                "Use 'long' if the values fit, or a string field.");

        if (type == typeof(string)) return "string";
        if (type == typeof(int)) return "int";
        if (type == typeof(long)) return "long";
        if (type == typeof(double)) return "double";
        if (type == typeof(float)) return "float";
        if (type == typeof(bool)) return "bool";
        if (type == typeof(byte[])) return "raw"; // fed as base64, matching System.Text.Json
        if (type == typeof(decimal)) return "double";
        if (type == typeof(short) || type == typeof(ushort)) return "int";
        if (type == typeof(uint)) return "long";
        if (type == typeof(sbyte)) return "byte";
        if (type == typeof(Guid)) return "string"; // serialized as a JSON string
        if (type == typeof(char)) return "string"; // serialized as a JSON string

        return null;
    }
}
