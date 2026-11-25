using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Text.Json.Serialization;
using System.Xml;
using Vespa.Models.Attributes;
using Vespa.Models.Schema;

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
        => Render(documentType, condition, escapeBackslashesAsHex ? RenderContext.Visitor : RenderContext.Write);

    internal static string RenderForPackage<T>(string documentType, Expression<Func<T, bool>> selection) where T : class
    {
        var rendered = Render(documentType, selection, RenderContext.Package);
        try
        {
            return XmlConvert.VerifyXmlChars(rendered);
        }
        catch (XmlException exception)
        {
            throw new NotSupportedException("Package selections require valid XML characters. Use DocumentSelections for native expressions.", exception);
        }
    }

    private enum RenderContext { Write, Visitor, Package }

    private static string Render<T>(string documentType, Expression<Func<T, bool>> condition, RenderContext context) where T : class
    {
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentType);
        ValidateIdentifier(documentType, isDocumentType: true);
        return new Renderer<T>(documentType, condition.Parameters[0], context).Predicate(condition.Body);
    }

    private sealed class Renderer<T>(string documentType, ParameterExpression parameter, RenderContext context) where T : class
    {
        internal string Predicate(Expression expression) => expression switch
        {
            BinaryExpression { NodeType: ExpressionType.AndAlso or ExpressionType.OrElse, Method: null } binary =>
                $"({Predicate(binary.Left)} {(binary.NodeType == ExpressionType.AndAlso ? "and" : "or")} {Predicate(binary.Right)})",
            UnaryExpression { NodeType: ExpressionType.Not, Method: null } unary =>
                $"not ({Predicate(unary.Operand)})",
            BinaryExpression binary when IsComparison(binary.NodeType) => Comparison(binary),
            MemberExpression { Member.Name: nameof(Nullable<int>.HasValue), Expression: { } target }
                when Nullable.GetUnderlyingType(target.Type) is not null => context == RenderContext.Package
                    ? NullComparison(Operand(target), equalsNull: false)
                    : $"({Field(target)} != null)",
            MemberExpression member when member.Type == typeof(bool) && member.Expression == parameter =>
                $"({Field(member)} == true)",
            _ => Value(expression) is bool value ? value ? "true" : "false" : throw Unsupported(expression)
        };

        private string Comparison(BinaryExpression binary)
        {
            var (operatorMethod, token) = binary.NodeType switch
            {
                ExpressionType.Equal => ("op_Equality", "=="),
                ExpressionType.NotEqual => ("op_Inequality", "!="),
                ExpressionType.GreaterThan => ("op_GreaterThan", ">"),
                ExpressionType.GreaterThanOrEqual => ("op_GreaterThanOrEqual", ">="),
                ExpressionType.LessThan => ("op_LessThan", "<"),
                ExpressionType.LessThanOrEqual => ("op_LessThanOrEqual", "<="),
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

            if (context == RenderContext.Package && !(left.IsNull || right.IsNull))
            {
                if (left.IsField && right.IsField && binary.NodeType is not (ExpressionType.Equal or ExpressionType.NotEqual))
                    throw Unsupported(binary, "Package routing does not support ordering two fields. Compare a field with a literal or a captured value.");
                // Java routing compares numbers as doubles; C++ GC keeps integral precision.
                // A bounded operand prevents rounding from changing an integral comparison.
                if (left.IsIntegral && right.IsIntegral && !left.HasExactIntegerRange && !right.HasExactIntegerRange)
                    throw Unsupported(binary, "Package integer comparisons need a byte/int field or a value strictly between -2^53 and 2^53. Use DocumentSelections for native expressions.");
            }

            if (context == RenderContext.Package && (left.IsNull || right.IsNull))
                return NullComparison(left.IsNull ? right : left, binary.NodeType == ExpressionType.Equal);

            var comparison = $"({left.Text} {token} {right.Text})";

            // Lifted C# ordering comparisons return false for nullable operands that
            // are absent. Guard them so a surrounding NOT preserves that behavior.
            if (binary.IsLifted && binary.NodeType is not (ExpressionType.Equal or ExpressionType.NotEqual))
            {
                if (left.IsNullableField)
                    comparison = context == RenderContext.Package
                        ? $"({NullComparison(left, equalsNull: false)} and {comparison})"
                        : $"({left.Text} != null and {comparison})";
                if (right.IsNullableField)
                    comparison = context == RenderContext.Package
                        ? $"({NullComparison(right, equalsNull: false)} and {comparison})"
                        : $"({right.Text} != null and {comparison})";
            }
            return comparison;
        }

        private string NullComparison(RenderedOperand operand, bool equalsNull)
        {
            if (context != RenderContext.Package || !operand.IsField)
                return $"({operand.Text} {(equalsNull ? "==" : "!=")} null)";
            if (!IsScalar(operand.Type))
                throw new NotSupportedException("Package null checks require scalar fields. Use DocumentSelections for native expressions.");
            // ID-only operations cannot inspect fields. Keep Java's INVALID result
            // so reads, updates and removes still route to the content cluster.
            var field = operand.Text;
            var isNull = $"(({field} == null) or (({field} == {field}) and ({field} != {field})))";
            return equalsNull ? isNull : $"not ({isNull})";
        }

        private RenderedOperand Operand(Expression expression)
        {
            var unwrapped = Unwrap(expression);
            if (unwrapped is MemberExpression { Member: PropertyInfo property, Expression: var target } && target == parameter)
            {
                if (Underlying(expression.Type) == typeof(decimal) && Underlying(unwrapped.Type) != typeof(decimal))
                    throw Unsupported(expression); // Native mixed numeric comparisons round decimal fractions to double.
                var (integral, bounded) = context == RenderContext.Package ? IntegerFieldInfo(property) : (false, false);
                return new(Field(unwrapped), expression.Type, false, Nullable.GetUnderlyingType(expression.Type) is not null, true, integral, bounded);
            }
            var value = Value(expression);
            var literal = Literal(value, context);
            if (context == RenderContext.Package && literal == "0x8000000000000000")
                throw Unsupported(expression, "Vespa rewrites the minimum signed 64-bit literal into a form its GC parser rejects. Use DocumentSelections for native expressions.");
            var integer = context == RenderContext.Package && value is not null &&
                (IsIntegral(expression.Type) || value is decimal && !literal.Contains('.'));
            return new(literal, expression.Type, value is null, false, false, integer,
                integer && Convert.ToDecimal(value, CultureInfo.InvariantCulture) is > -9007199254740992m and < 9007199254740992m);
        }

        private static (bool Integral, bool Bounded) IntegerFieldInfo(PropertyInfo property)
        {
            property = DocumentFieldSelector.EffectiveProperty<T>(property);
            var fieldType = property.GetCustomAttribute<VespaFieldAttribute>()?.FieldType ?? VespaFieldType.None;
            if (fieldType != VespaFieldType.None)
                return (fieldType is VespaFieldType.Byte or VespaFieldType.Int or VespaFieldType.Long,
                    fieldType is VespaFieldType.Byte or VespaFieldType.Int);
            var type = Underlying(property.PropertyType);
            return (IsIntegral(type), type == typeof(sbyte) || type == typeof(short) || type == typeof(ushort) || type == typeof(int));
        }

        private string Field(Expression expression)
        {
            expression = Unwrap(expression);
            if (expression is not MemberExpression { Member: PropertyInfo property, Expression: var target } || target != parameter)
                throw Unsupported(expression);
            var body = expression.Type.IsValueType ? Expression.Convert(expression, typeof(object)) : expression;
            var selector = Expression.Lambda<Func<T, object?>>(body, parameter);
            var name = DocumentFieldSelector.FieldName(selector);
            var effectiveProperty = DocumentFieldSelector.EffectiveProperty<T>(property);
            if (context == RenderContext.Package && !IsPackageScalar(effectiveProperty))
                throw Unsupported(expression, "Package fields must use matching native scalar categories (string, boolean or numeric). Use DocumentSelections for other native field types.");
            var valueType = Underlying(expression.Type);
            if (valueType.IsEnum &&
                (effectiveProperty.IsDefined(typeof(JsonConverterAttribute), inherit: false) ||
                 valueType.IsDefined(typeof(JsonConverterAttribute), inherit: false)))
                throw Unsupported(expression, "Enum fields with JSON converters require rawCondition: to match their stored representation.");
            ValidateIdentifier(name, isDocumentType: false);
            return $"{documentType}.{name}";
        }

        private static bool IsPackageScalar(PropertyInfo property)
        {
            if (property.GetCustomAttribute<VespaTensorAttribute>() is not null)
                return false;
            var type = Underlying(property.PropertyType);
            if (type.IsEnum)
                type = Enum.GetUnderlyingType(type);
            var field = property.GetCustomAttribute<VespaFieldAttribute>();
            if (field is null)
                return false;
            return field.FieldType switch
            {
                VespaFieldType.None => IsScalar(type),
                VespaFieldType.String => type == typeof(string),
                VespaFieldType.Bool => type == typeof(bool),
                VespaFieldType.Byte or VespaFieldType.Int or VespaFieldType.Long or VespaFieldType.Float or VespaFieldType.Double => IsNumber(type),
                _ => false
            };
        }
    }

    private readonly record struct RenderedOperand(string Text, Type Type, bool IsNull, bool IsNullableField, bool IsField, bool IsIntegral = false, bool HasExactIntegerRange = false);

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

    private static bool IsIntegral(Type type)
    {
        type = Underlying(type);
        if (type.IsEnum)
            type = Enum.GetUnderlyingType(type);
        return type == typeof(byte) || type == typeof(sbyte) || type == typeof(short) || type == typeof(ushort) ||
            type == typeof(int) || type == typeof(uint) || type == typeof(long);
    }

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

    private static string Literal(object? value, RenderContext context) => value switch
    {
        null => "null",
        bool boolean => boolean ? "true" : "false",
        string text => context == RenderContext.Package ? QuoteForPackage(text) : Quote(text, context == RenderContext.Visitor),
        Enum number => Literal(Convert.ChangeType(number, Enum.GetUnderlyingType(number.GetType()), CultureInfo.InvariantCulture), context),
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
        // Keep whole-valued floating-point numbers in floating-point syntax.
        // The inserted .0 preserves that form in exponent notation too.
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

    private static string QuoteForPackage(string text)
    {
        if (text.EndsWith('\\'))
            throw new NotSupportedException("Package string literals ending in a backslash are ambiguous to the routing parser. Use DocumentSelections for native expressions.");
        // Java rewrites this literal before passing it to C++ GC. Its Unicode
        // escapes are not accepted there, so keep literals in the shared subset.
        var result = new StringBuilder("\"");
        foreach (var value in text)
            switch (value)
            {
                case '"': result.Append("\\\""); break;
                case '\\': result.Append("\\\\"); break;
                case '\n': result.Append("\\n"); break;
                case '\r': result.Append("\\r"); break;
                case '\t': result.Append("\\t"); break;
                case '\f': result.Append("\\f"); break;
                case < ' ' or > '~': throw new NotSupportedException("Typed package string literals require printable ASCII or tab, newline, carriage return and form feed. Vespa's GC configuration rewrites other characters into unsupported Unicode escapes.");
                default: result.Append(value); break;
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
