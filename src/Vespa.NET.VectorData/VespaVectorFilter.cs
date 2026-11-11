using System.Collections;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;

namespace Vespa.VectorData;

internal sealed class VespaVectorFilter<TRecord>(VespaVectorRecordMapper<TRecord> mapper) where TRecord : class
{
    internal string Translate(Expression<Func<TRecord, bool>> filter) => Visit(filter.Body, filter.Parameters[0]);

    internal VespaCollectionModel.MappedProperty ResolvePropertySelector(LambdaExpression selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        if (selector.Parameters.Count != 1)
            throw new NotSupportedException("Select one mapped property directly.");
        var expression = selector.Body;
        while (expression is UnaryExpression { NodeType: ExpressionType.Convert, Method: null, Type: var type } convert && type == typeof(object))
            expression = convert.Operand;
        return TryPropertyName(Unwrap(expression, selector.Parameters[0]), selector.Parameters[0], out var name)
            ? mapper.Property(name) : throw new NotSupportedException("Select one mapped property directly.");
    }

    internal bool IsVectorSelector(LambdaExpression selector) => ResolvePropertySelector(selector).IsVector;

    private string Visit(Expression expression, ParameterExpression parameter)
    {
        expression = Unwrap(expression, parameter);
        if (expression is BinaryExpression { NodeType: ExpressionType.AndAlso or ExpressionType.OrElse } logic)
            return $"({Visit(logic.Left, parameter)} {(logic.NodeType == ExpressionType.AndAlso ? "and" : "or")} {Visit(logic.Right, parameter)})";
        if (expression is UnaryExpression { NodeType: ExpressionType.Not } not)
            return $"!({Visit(not.Operand, parameter)})";
        if (expression is ConstantExpression { Value: bool constant })
            return constant ? "true" : "false";
        if (expression is MethodCallExpression call)
            return Membership(call, parameter);
        if (TryProperty(expression, parameter, out var booleanProperty) && booleanProperty.Type == typeof(bool))
            return $"{booleanProperty.StorageName} = true";
        if (expression is not BinaryExpression comparison || comparison.NodeType is not
            (ExpressionType.Equal or ExpressionType.NotEqual or ExpressionType.GreaterThan or
             ExpressionType.GreaterThanOrEqual or ExpressionType.LessThan or ExpressionType.LessThanOrEqual))
            throw new NotSupportedException($"Filter expression '{expression.NodeType}' is not supported.");

        var nodeType = comparison.NodeType;
        Expression valueExpression;
        VespaCollectionModel.MappedProperty property;
        if (TryProperty(Unwrap(comparison.Left, parameter), parameter, out property))
            valueExpression = comparison.Right;
        else if (TryProperty(Unwrap(comparison.Right, parameter), parameter, out property))
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

        EnsureScalar(property);
        return Comparison(property, property.Type, Value(valueExpression), nodeType);
    }

    private static string Comparison(VespaCollectionModel.MappedProperty property, Type valueType, object? value, ExpressionType nodeType)
    {
        if (value is null)
            throw new NotSupportedException("Vespa YQL cannot express null/missing-field comparisons.");
        var type = Nullable.GetUnderlyingType(valueType) ?? valueType;
        if (type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(DateOnly))
            value = VespaCollectionModel.ToStorageValue(type, value);
        if (value is string text)
        {
            if (property.IsFullTextIndexed)
                throw new NotSupportedException("String equality requires a cased exact-match attribute, not a full-text indexed field.");
            if (text.Length == 0)
                throw new NotSupportedException("Vespa does not index empty string values; empty-string comparisons are unsupported.");
            if (nodeType is not (ExpressionType.Equal or ExpressionType.NotEqual))
                throw new NotSupportedException("Only equality and inequality are supported for strings.");
            var term = $"{property.StorageName} contains ({{stem:false,normalizeCase:false,accentDrop:false}}{JsonSerializer.Serialize(text)})";
            return nodeType == ExpressionType.NotEqual ? $"!({term})" : term;
        }
        var literal = Literal(value);
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

    private string Membership(MethodCallExpression call, ParameterExpression parameter)
    {
        if (ContainsArguments(call, out var source, out var value))
        {
            if (TryProperty(Unwrap(source, parameter), parameter, out var collection))
            {
                var elementType = VespaCollectionModel.CollectionElementType(collection.Type)
                    ?? throw new NotSupportedException("Contains on a record property requires a mapped array or list.");
                ValidateMembershipType(elementType);
                return Comparison(collection, elementType, Value(value), ExpressionType.Equal);
            }
            if (TryProperty(Unwrap(value, parameter), parameter, out var scalar))
            {
                EnsureScalar(scalar);
                return InSet(scalar, scalar.Type, source);
            }
        }
        // Restrict Any to one element membership test. Combining array predicates can
        // otherwise match different elements and silently change the LINQ semantics.
        if (call.Method.DeclaringType == typeof(Enumerable) && call.Method.Name == nameof(Enumerable.Any) && call.Arguments.Count == 2 &&
            TryProperty(Unwrap(call.Arguments[0], parameter), parameter, out var array))
        {
            var elementType = VespaCollectionModel.CollectionElementType(array.Type)
                ?? throw new NotSupportedException("Any requires a mapped array or list.");
            var predicate = call.Arguments[1] is UnaryExpression { NodeType: ExpressionType.Quote, Operand: LambdaExpression quoted }
                ? quoted : call.Arguments[1] as LambdaExpression;
            if (predicate is { Parameters.Count: 1, Body: MethodCallExpression contains } &&
                ContainsArguments(contains, out var set, out var element) &&
                Unwrap(element, predicate.Parameters[0]) == predicate.Parameters[0])
                return InSet(array, elementType, set);
        }
        throw new NotSupportedException($"Unsupported membership expression '{call}' ({call.Method.DeclaringType}). Use a constant array/list Contains(mappedScalar), mappedArray.Contains(value), or mappedArray.Any(value => allowed.Contains(value)).");
    }

    private static string InSet(VespaCollectionModel.MappedProperty property, Type elementType, Expression source)
    {
        ValidateMembershipType(elementType);
        var values = SetValues(source);
        // Validate all values before reducing an empty/duplicate set; null and empty
        // strings cannot be represented with the same membership semantics in Vespa.
        var terms = values.Select(value =>
        {
            if (value is null)
                throw new NotSupportedException("Membership sets cannot contain null values.");
            ValidateMembershipType(value.GetType());
            return Comparison(property, elementType, value, ExpressionType.Equal);
        }).Distinct(StringComparer.Ordinal).ToArray();
        if (terms.Length == 0)
            return "false";
        if (terms.Length == 1)
            return terms[0];
        if ((Nullable.GetUnderlyingType(elementType) ?? elementType) != typeof(string))
            return $"{property.StorageName} in ({string.Join(", ", values.Select(value => Literal(value!)).Distinct(StringComparer.Ordinal))})";
        // String IN performs linguistic case handling. Keep exact cased matching by
        // reusing the same annotated terms as string equality instead.
        return $"({string.Join(" or ", terms)})";
    }

    private static bool ContainsArguments(MethodCallExpression call, out Expression source, out Expression value)
    {
        // C# 14 binds array.Contains to MemoryExtensions via the built-in
        // array-to-span operator, including inside expression trees. Inspect that
        // exact shape; never invoke conversion operators or span-producing code.
        if (call.Method.DeclaringType == typeof(MemoryExtensions) && call.Method.Name == nameof(MemoryExtensions.Contains) &&
            call.Arguments.Count == 2 && call.Arguments[0] is MethodCallExpression
            {
                Method.Name: "op_Implicit", Arguments.Count: 1, Method.DeclaringType: { IsGenericType: true } spanType
            } span && (spanType.GetGenericTypeDefinition() == typeof(ReadOnlySpan<>) || spanType.GetGenericTypeDefinition() == typeof(Span<>)) &&
            span.Arguments[0].Type == spanType.GenericTypeArguments[0].MakeArrayType())
        {
            source = span.Arguments[0];
            value = call.Arguments[1];
            return true;
        }
        if (call.Method.DeclaringType == typeof(Enumerable) && call.Method.Name == nameof(Enumerable.Contains) && call.Arguments.Count == 2)
        {
            source = call.Arguments[0];
            value = call.Arguments[1];
            return true;
        }
        if (call.Method.Name == nameof(List<int>.Contains) && call.Method.DeclaringType is { IsGenericType: true } declaring &&
            declaring.GetGenericTypeDefinition() == typeof(List<>) && call.Object is not null && call.Arguments.Count == 1)
        {
            source = call.Object;
            value = call.Arguments[0];
            return true;
        }
        source = value = null!;
        return false;
    }

    private static object?[] SetValues(Expression source)
    {
        while (source is UnaryExpression { NodeType: ExpressionType.Convert, Method: null } convert && convert.Type == convert.Operand.Type)
            source = convert.Operand;
        if (source is NewArrayExpression { NodeType: ExpressionType.NewArrayInit } array)
            return array.Expressions.Select(Value).ToArray();
        var value = Value(source) ?? throw new NotSupportedException("Membership requires a non-null array or list.");
        var type = value.GetType();
        if (!(type.IsArray && type.GetArrayRank() == 1) &&
            !(type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>)))
            throw new NotSupportedException("Captured membership values must be a built-in array or List<T>; custom enumerables and comparers are unsupported.");
        return ((IEnumerable)value).Cast<object?>().ToArray();
    }

    private static void ValidateMembershipType(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type != typeof(string) && type != typeof(byte) && type != typeof(sbyte) && type != typeof(short) &&
            type != typeof(ushort) && type != typeof(int) && type != typeof(uint) && type != typeof(long))
            throw new NotSupportedException("Membership supports strings and signed-range integer values only.");
    }

    private static void EnsureScalar(VespaCollectionModel.MappedProperty property)
    {
        if (VespaCollectionModel.IsCollectionType(property.Type))
            throw new NotSupportedException("A scalar comparison cannot compare entire arrays or lists; use Contains or supported Any membership.");
    }

    private static string Literal(object value) => value switch
    {
        bool boolean => boolean ? "true" : "false",
        long number => number.ToString(CultureInfo.InvariantCulture) + "L",
        uint number when number > int.MaxValue => number.ToString(CultureInfo.InvariantCulture) + "L",
        byte or sbyte or short or ushort or int or uint => Convert.ToString(value, CultureInfo.InvariantCulture)!,
        float number when float.IsFinite(number) => number.ToString("R", CultureInfo.InvariantCulture),
        double number when double.IsFinite(number) => number.ToString("R", CultureInfo.InvariantCulture),
        _ => throw new NotSupportedException($"Filter values of type '{value.GetType()}' are unsupported.")
    };

    private bool TryProperty(Expression expression, ParameterExpression parameter,
        out VespaCollectionModel.MappedProperty property)
    {
        if (TryPropertyName(expression, parameter, out var name))
        {
            property = mapper.DataProperty(name);
            return true;
        }
        property = null!;
        return false;
    }

    private bool TryPropertyName(Expression expression, ParameterExpression parameter, out string name)
    {
        if (!mapper.IsDynamic && expression is MemberExpression member && member.Expression == parameter)
        {
            name = member.Member.Name;
            return true;
        }
        Expression? key = expression switch
        {
            MethodCallExpression { Object: var target, Method.Name: "get_Item", Arguments.Count: 1 } call when mapper.IsDynamic && target == parameter => call.Arguments[0],
            IndexExpression { Object: var target, Arguments.Count: 1 } index when mapper.IsDynamic && target == parameter => index.Arguments[0],
            _ => null
        };
        if (key is not null && Value(key) is string propertyName)
        {
            name = propertyName;
            return true;
        }
        name = "";
        return false;
    }

    private Expression Unwrap(Expression expression, ParameterExpression parameter)
    {
        while (expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert)
        {
            if (mapper.IsDynamic && convert.Method is null && convert.Operand.Type == typeof(object) &&
                TryPropertyName(convert.Operand, parameter, out var name))
            {
                if (mapper.Property(name).Type != convert.Type)
                    throw new NotSupportedException("A dictionary field cast must match its declared collection type.");
            }
            else
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
