using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;

namespace Vespa.VectorData;

internal sealed class VespaVectorFilter<TRecord>(VespaVectorRecordMapper<TRecord> mapper) where TRecord : class
{
    internal string Translate(Expression<Func<TRecord, bool>> filter) => Visit(filter.Body, filter.Parameters[0]);

    private string Visit(Expression expression, ParameterExpression parameter)
    {
        expression = Unwrap(expression);
        if (expression is BinaryExpression { NodeType: ExpressionType.AndAlso or ExpressionType.OrElse } logic)
            return $"({Visit(logic.Left, parameter)} {(logic.NodeType == ExpressionType.AndAlso ? "and" : "or")} {Visit(logic.Right, parameter)})";
        if (expression is UnaryExpression { NodeType: ExpressionType.Not } not)
            return $"!({Visit(not.Operand, parameter)})";
        if (expression is ConstantExpression { Value: bool constant })
            return constant ? "true" : "false";
        if (TryProperty(expression, parameter, out var booleanProperty) && booleanProperty.Property.PropertyType == typeof(bool))
            return $"{booleanProperty.StorageName} = true";
        if (expression is not BinaryExpression comparison || comparison.NodeType is not
            (ExpressionType.Equal or ExpressionType.NotEqual or ExpressionType.GreaterThan or
             ExpressionType.GreaterThanOrEqual or ExpressionType.LessThan or ExpressionType.LessThanOrEqual))
            throw new NotSupportedException($"Filter expression '{expression.NodeType}' is not supported.");

        var nodeType = comparison.NodeType;
        Expression valueExpression;
        VespaVectorRecordMapper<TRecord>.MappedProperty property;
        if (TryProperty(Unwrap(comparison.Left), parameter, out property))
            valueExpression = comparison.Right;
        else if (TryProperty(Unwrap(comparison.Right), parameter, out property))
        {
            valueExpression = comparison.Left;
            nodeType = nodeType switch
            {
                ExpressionType.GreaterThan => ExpressionType.LessThan,
                ExpressionType.GreaterThanOrEqual => ExpressionType.LessThanOrEqual,
                ExpressionType.LessThan => ExpressionType.GreaterThan,
                ExpressionType.LessThanOrEqual => ExpressionType.GreaterThanOrEqual,
                _ => nodeType
            };
        }
        else
            throw new NotSupportedException("A comparison must use one mapped data property and one constant or captured value.");

        var value = Value(valueExpression)
            ?? throw new NotSupportedException("Vespa YQL cannot express null/missing-field comparisons.");
        if (value is string text)
        {
            if (text.Length == 0)
                throw new NotSupportedException("Vespa does not index empty string values; empty-string comparisons are unsupported.");
            if (nodeType is not (ExpressionType.Equal or ExpressionType.NotEqual))
                throw new NotSupportedException("Only equality and inequality are supported for strings.");
            var term = $"{property.StorageName} contains ({{stem:false,normalizeCase:false,accentDrop:false}}{JsonSerializer.Serialize(text)})";
            return nodeType == ExpressionType.NotEqual ? $"!({term})" : term;
        }
        var literal = value switch
        {
            bool b => b ? "true" : "false",
            byte or sbyte or short or ushort or int or uint or long => Convert.ToString(value, CultureInfo.InvariantCulture)!,
            float f when float.IsFinite(f) => f.ToString("R", CultureInfo.InvariantCulture),
            double d when double.IsFinite(d) => d.ToString("R", CultureInfo.InvariantCulture),
            _ => throw new NotSupportedException($"Filter values of type '{value.GetType()}' are unsupported.")
        };
        var op = nodeType switch
        {
            ExpressionType.Equal => "=",
            ExpressionType.NotEqual => "=",
            ExpressionType.GreaterThan => ">",
            ExpressionType.GreaterThanOrEqual => ">=",
            ExpressionType.LessThan => "<",
            ExpressionType.LessThanOrEqual => "<=",
            _ => throw new NotSupportedException()
        };
        var comparisonTerm = $"{property.StorageName} {op} {literal}";
        return nodeType == ExpressionType.NotEqual ? $"!({comparisonTerm})" : comparisonTerm;
    }

    private bool TryProperty(Expression expression, ParameterExpression parameter,
        out VespaVectorRecordMapper<TRecord>.MappedProperty property)
    {
        if (expression is MemberExpression member && member.Expression == parameter)
        {
            property = mapper.DataProperty(member.Member.Name);
            return true;
        }
        property = null!;
        return false;
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
        MemberExpression { Member: FieldInfo field } member => field.GetValue(member.Expression is null ? null : Value(member.Expression)),
        UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert => ConvertValue(convert),
        _ => throw new NotSupportedException("Only constants and captured local fields are supported as filter values.")
    };

    private static object? ConvertValue(UnaryExpression convert)
    {
        ValidateConversion(convert);
        var value = Value(convert.Operand);
        return value is null ? null : Convert.ChangeType(value,
            Nullable.GetUnderlyingType(convert.Type) ?? convert.Type, CultureInfo.InvariantCulture);
    }

    // Only remove promotions that preserve every possible source value. Evaluate
    // constant-side promotions so float -> double retains the actual binary value.
    private static void ValidateConversion(UnaryExpression convert)
    {
        var source = Nullable.GetUnderlyingType(convert.Operand.Type) ?? convert.Operand.Type;
        var target = Nullable.GetUnderlyingType(convert.Type) ?? convert.Type;
        var removesNullability = Nullable.GetUnderlyingType(convert.Operand.Type) is not null &&
            Nullable.GetUnderlyingType(convert.Type) is null;
        var safe = source == target ||
            (source == typeof(int) && (target == typeof(long) || target == typeof(double))) ||
            (source == typeof(float) && target == typeof(double)) ||
            ((source == typeof(byte) || source == typeof(sbyte) || source == typeof(short) || source == typeof(ushort)) &&
             (target == typeof(int) || target == typeof(long) || target == typeof(float) || target == typeof(double))) ||
            (source == typeof(uint) && (target == typeof(long) || target == typeof(double)));
        if (convert.Method is not null || removesNullability || !safe)
            throw new NotSupportedException("This filter cast can change comparison semantics. Convert captured values before building the filter.");
    }
}
