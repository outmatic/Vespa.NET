using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Text.Json.Serialization;
using Vespa.Models.Attributes;

namespace Vespa.Documents;

/// <summary>Builds native Vespa document conditions from typed predicates.</summary>
public static class DocumentCondition
{
    /// <summary>
    /// Translates a predicate using the model's document type and field names.
    /// The result is a write condition without a document-type filter.
    /// Use <see cref="DocumentSelection"/> for a selection restricted to the model's exact document type.
    /// </summary>
    /// <remarks>
    /// Supports scalar comparisons, boolean fields, null checks, and logical operators.
    /// Values may be constants or captured local fields. Unsupported expressions throw
    /// <see cref="NotSupportedException"/> without compiling or executing the predicate.
    /// Conditions are evaluated by Vespa against the stored document.
    /// </remarks>
    public static string For<T>(Expression<Func<T, bool>> condition) where T : class
    {
        ArgumentNullException.ThrowIfNull(condition);
        var (documentType, _) = VespaDocumentMeta.For<T>();
        return Render(documentType, condition);
    }

    internal static string Render<T>(string documentType, Expression<Func<T, bool>> condition, bool escapeBackslashesAsHex = false) where T : class
    {
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentType);
        ValidateIdentifier(documentType, isDocumentType: true);
        return new Renderer<T>(documentType, condition.Parameters[0], escapeBackslashesAsHex).Predicate(condition.Body);
    }

    private sealed class Renderer<T>(string documentType, ParameterExpression parameter, bool escapeBackslashesAsHex) where T : class
    {
        internal string Predicate(Expression expression) => expression switch
        {
            BinaryExpression { NodeType: ExpressionType.AndAlso or ExpressionType.OrElse, Method: null } binary =>
                $"({Predicate(binary.Left)} {(binary.NodeType == ExpressionType.AndAlso ? "and" : "or")} {Predicate(binary.Right)})",
            UnaryExpression { NodeType: ExpressionType.Not, Method: null } unary =>
                $"not ({Predicate(unary.Operand)})",
            BinaryExpression binary when IsComparison(binary.NodeType) => Comparison(binary),
            MemberExpression { Member.Name: nameof(Nullable<int>.HasValue), Expression: { } target }
                when Nullable.GetUnderlyingType(target.Type) is not null => $"({Field(target)} != null)",
            MemberExpression member when member.Type == typeof(bool) && member.Expression == parameter =>
                $"({Field(member)} == true)",
            _ => Value(expression) is bool value ? value ? "true" : "false" : throw Unsupported(expression)
        };

        private string Comparison(BinaryExpression binary)
        {
            var operatorMethod = binary.NodeType switch
            {
                ExpressionType.Equal => "op_Equality",
                ExpressionType.NotEqual => "op_Inequality",
                ExpressionType.GreaterThan => "op_GreaterThan",
                ExpressionType.GreaterThanOrEqual => "op_GreaterThanOrEqual",
                ExpressionType.LessThan => "op_LessThan",
                ExpressionType.LessThanOrEqual => "op_LessThanOrEqual",
                _ => throw Unsupported(binary)
            };
            if (binary.Method is { } method &&
                (method.Name != operatorMethod || (method.DeclaringType != typeof(string) && method.DeclaringType != typeof(decimal))))
                throw Unsupported(binary);

            var left = Operand(binary.Left);
            var right = Operand(binary.Right);
            if ((left.IsNull || right.IsNull) && binary.NodeType is not (ExpressionType.Equal or ExpressionType.NotEqual))
                return binary.IsLifted && !binary.IsLiftedToNull ? "false" : throw Unsupported(binary);
            if (!(left.IsNull || right.IsNull) && (!IsScalar(left.Type) || !IsScalar(right.Type)))
                throw Unsupported(binary);
            if (!(left.IsNull || right.IsNull) && binary.Method is null && left.Type == typeof(string) && right.Type == typeof(string))
                throw Unsupported(binary); // Reference equality cannot be expressed by a selector.
            if (!left.IsField && !right.IsField &&
                (Underlying(left.Type) == typeof(decimal) || Underlying(right.Type) == typeof(decimal)))
                throw Unsupported(binary); // Evaluate constant decimal predicates in C# before capturing the resulting bool.

            var op = binary.NodeType switch
            {
                ExpressionType.Equal => "==",
                ExpressionType.NotEqual => "!=",
                ExpressionType.GreaterThan => ">",
                ExpressionType.GreaterThanOrEqual => ">=",
                ExpressionType.LessThan => "<",
                ExpressionType.LessThanOrEqual => "<=",
                _ => throw Unsupported(binary)
            };
            var comparison = $"({left.Text} {op} {right.Text})";

            // Lifted C# ordering comparisons return false for nullable operands that
            // are absent. Guard them so a surrounding NOT preserves that behavior.
            if (binary.IsLifted && binary.NodeType is not (ExpressionType.Equal or ExpressionType.NotEqual))
            {
                if (left.IsNullableField)
                    comparison = $"({left.Text} != null and {comparison})";
                if (right.IsNullableField)
                    comparison = $"({right.Text} != null and {comparison})";
            }
            return comparison;
        }

        private RenderedOperand Operand(Expression expression)
        {
            var unwrapped = Unwrap(expression);
            if (unwrapped is MemberExpression { Expression: var target } && target == parameter)
            {
                if (Underlying(expression.Type) == typeof(decimal) && Underlying(unwrapped.Type) != typeof(decimal))
                    throw Unsupported(expression); // Native mixed numeric comparisons round decimal fractions to double.
                return new(Field(unwrapped), expression.Type, false, Nullable.GetUnderlyingType(expression.Type) is not null, true);
            }
            var value = Value(expression);
            return new(Literal(value, escapeBackslashesAsHex), expression.Type, value is null, false, false);
        }

        private string Field(Expression expression)
        {
            expression = Unwrap(expression);
            if (expression is not MemberExpression { Member: PropertyInfo property, Expression: var target } || target != parameter)
                throw Unsupported(expression);
            var body = expression.Type.IsValueType ? Expression.Convert(expression, typeof(object)) : expression;
            var selector = Expression.Lambda<Func<T, object?>>(body, parameter);
            var name = DocumentFieldSelector.FieldName(selector);
            var valueType = Underlying(expression.Type);
            if (valueType.IsEnum &&
                (DocumentFieldSelector.EffectiveProperty<T>(property).IsDefined(typeof(JsonConverterAttribute), inherit: false) ||
                 valueType.IsDefined(typeof(JsonConverterAttribute), inherit: false)))
                throw Unsupported(expression, "Enum fields with JSON converters require rawCondition: to match their stored representation.");
            ValidateIdentifier(name, isDocumentType: false);
            return $"{documentType}.{name}";
        }
    }

    private readonly record struct RenderedOperand(string Text, Type Type, bool IsNull, bool IsNullableField, bool IsField);

    private static Type Underlying(Type type) => Nullable.GetUnderlyingType(type) ?? type;

    private static bool IsComparison(ExpressionType type) => type is
        ExpressionType.Equal or ExpressionType.NotEqual or ExpressionType.GreaterThan or
        ExpressionType.GreaterThanOrEqual or ExpressionType.LessThan or ExpressionType.LessThanOrEqual;

    private static bool IsScalar(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type.IsEnum)
            type = Enum.GetUnderlyingType(type);
        return type == typeof(string) || type == typeof(bool) || IsNumber(type);
    }

    private static bool IsNumber(Type type) => type == typeof(byte) || type == typeof(sbyte) ||
        type == typeof(short) || type == typeof(ushort) || type == typeof(int) || type == typeof(uint) ||
        type == typeof(long) || type == typeof(float) || type == typeof(double) || type == typeof(decimal);

    private static Expression Unwrap(Expression expression)
    {
        while (expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert)
        {
            ValidateConversion(convert);
            expression = convert.Operand;
        }
        return expression;
    }

    private static object? Value(Expression expression) => expression switch
    {
        ConstantExpression constant => constant.Value,
        // Read captured locals, but do not invoke property getters or methods.
        MemberExpression { Member: FieldInfo field } member when member.Expression is not null =>
            field.GetValue(Value(member.Expression)),
        UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert => ConvertedValue(convert),
        _ => throw Unsupported(expression)
    };

    private static object? ConvertedValue(UnaryExpression convert)
    {
        ValidateConversion(convert);
        var value = Value(convert.Operand);
        var target = Underlying(convert.Type);
        return value is null || Underlying(convert.Operand.Type) == target ? value :
            Convert.ChangeType(value, target, CultureInfo.InvariantCulture);
    }

    private static void ValidateConversion(UnaryExpression convert)
    {
        var source = Nullable.GetUnderlyingType(convert.Operand.Type) ?? convert.Operand.Type;
        var target = Nullable.GetUnderlyingType(convert.Type) ?? convert.Type;
        var sameType = source == target;
        if (source.IsEnum)
            source = Enum.GetUnderlyingType(source);
        var removesNullability = Nullable.GetUnderlyingType(convert.Operand.Type) is not null &&
            Nullable.GetUnderlyingType(convert.Type) is null;
        var safe = sameType || source == target ||
            (source == typeof(int) && (target == typeof(long) || target == typeof(double) || target == typeof(decimal))) ||
            (source == typeof(float) && target == typeof(double)) ||
            ((source == typeof(byte) || source == typeof(sbyte) || source == typeof(short) || source == typeof(ushort)) &&
             (target == typeof(int) || target == typeof(long) || target == typeof(float) || target == typeof(double) || target == typeof(decimal))) ||
            (source == typeof(uint) && (target == typeof(long) || target == typeof(double) || target == typeof(decimal))) ||
            (source == typeof(long) && target == typeof(decimal));
        var decimalPromotion = convert.Method?.DeclaringType == typeof(decimal) && convert.Method.Name == "op_Implicit";
        if ((!decimalPromotion && convert.Method is not null) || removesNullability || !safe)
            throw Unsupported(convert);
    }

    private static string Literal(object? value, bool escapeBackslashesAsHex) => value switch
    {
        null => "null",
        bool boolean => boolean ? "true" : "false",
        string text => Quote(text, escapeBackslashesAsHex),
        Enum number => Literal(Convert.ChangeType(number, Enum.GetUnderlyingType(number.GetType()), CultureInfo.InvariantCulture), escapeBackslashesAsHex),
        long number when number == long.MinValue => "0x8000000000000000",
        byte or sbyte or short or ushort or int or uint or long => Convert.ToString(value, CultureInfo.InvariantCulture)!,
        decimal number => DecimalLiteral(number),
        float number when float.IsFinite(number) => FloatingLiteral(number),
        double number when double.IsFinite(number) => FloatingLiteral(number),
        _ => throw new NotSupportedException($"Condition values of type '{value.GetType()}' are unsupported. Use strings, booleans, finite numbers, or null.")
    };

    private static string FloatingLiteral(double value)
    {
        // A float must widen before formatting to retain the exact stored value.
        // Vespa requires a decimal point, including in scientific notation.
        var text = value.ToString("R", CultureInfo.InvariantCulture);
        var exponent = text.IndexOf('E');
        return text.Contains('.') ? text : exponent >= 0 ? text.Insert(exponent, ".0") : text + ".0";
    }

    private static string DecimalLiteral(decimal value)
    {
        var text = value.ToString(CultureInfo.InvariantCulture);
        if (value == long.MinValue && !text.Contains('.'))
            return "0x8000000000000000";
        // A whole decimal outside the signed 64-bit range must be parsed as a
        // floating-point literal rather than overflowing Vespa's integer parser.
        return (value < long.MinValue || value > long.MaxValue) && !text.Contains('.') ? text + ".0" : text;
    }

    private static string Quote(string text, bool escapeBackslashesAsHex)
    {
        // Document selectors use byte escapes (\xHH), not JSON's Unicode escapes.
        // Encode non-ASCII characters as UTF-8 bytes so exact equality round-trips.
        var result = new StringBuilder("\"");
        foreach (var value in new UTF8Encoding(false, true).GetBytes(text))
            switch (value)
            {
                case (byte)'"': result.Append("\\\""); break;
                // The visitor's Java lexer can swallow the next literal after a
                // terminal backslash. Field values are evaluated by C++, which
                // accepts this byte escape; Java does not use them for routing.
                case (byte)'\\': result.Append(escapeBackslashesAsHex ? "\\x5C" : "\\\\"); break;
                case (byte)'\n': result.Append("\\n"); break;
                case (byte)'\r': result.Append("\\r"); break;
                case (byte)'\t': result.Append("\\t"); break;
                case (byte)'\f': result.Append("\\f"); break;
                case >= 32 and <= 126: result.Append((char)value); break;
                default: result.Append("\\x").Append(value.ToString("X2", CultureInfo.InvariantCulture)); break;
            }
        return result.Append('"').ToString();
    }

    private static void ValidateIdentifier(string name, bool isDocumentType)
    {
        if (isDocumentType)
        {
            VespaIdentifier.ValidateDocumentType(name);
            return;
        }
        var valid = name.Length > 0 && (char.IsAsciiLetter(name[0]) || name[0] == '_') &&
            name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
        if (name.ToLowerInvariant() is "true" or "false" or "and" or "or" or "not" or "null")
            valid = false;
        if (!valid)
            throw new ArgumentException($"'{name}' is not a valid document condition field name.");
    }

    private static NotSupportedException Unsupported(Expression expression, string? explanation = null) => new(
        $"Unsupported document condition expression '{expression.NodeType}'. " +
        (explanation ?? "Use direct scalar properties, constants or captured locals, comparisons, &&, ||, !, and null checks. Use a string condition for other native selector features."));
}
