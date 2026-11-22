using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json.Serialization;
using Vespa.Models;
using Vespa.Models.Attributes;

namespace Vespa.Documents;

/// <summary>Resolves a directly selected property that has its own document JSON binding.</summary>
internal static class DocumentFieldSelector
{
    internal static string FieldName<T>(Expression<Func<T, object?>> selector)
    {
        // Shared metadata validates shape and preserves the existing field-name policy.
        var name = VespaDocumentMeta.FieldName(selector);
        var member = selector.Body is UnaryExpression convert
            ? (MemberExpression)convert.Operand
            : (MemberExpression)selector.Body;
        var selected = (PropertyInfo)member.Member;
        var property = EffectiveProperty<T>(selected);

        if (property.IsDefined(typeof(VespaIdAttribute), inherit: false) ||
            property.IsDefined(typeof(VespaExtraFieldsAttribute), inherit: false) ||
            property.IsDefined(typeof(JsonExtensionDataAttribute), inherit: false) ||
            property.GetCustomAttribute<JsonIgnoreAttribute>(inherit: false)?.Condition == JsonIgnoreCondition.Always)
            throw new NotSupportedException(
                $"Property '{property.Name}' does not have an individual document JSON binding. Use an explicit native expression or field name for fields outside that binding.");

        // A base virtual getter dispatches to the override on the declared model.
        // Use that property's binding rather than the base member's attributes.
        if (property.DeclaringType != selected.DeclaringType)
        {
            var access = Expression.Property(selector.Parameters[0], property);
            var body = property.PropertyType.IsValueType ? Expression.Convert(access, typeof(object)) : (Expression)access;
            name = VespaDocumentMeta.FieldName(Expression.Lambda<Func<T, object?>>(body, selector.Parameters));
        }

        var jsonName = property.GetCustomAttribute<VespaFieldAttribute>(inherit: false)?.Name
            ?? property.GetCustomAttribute<JsonPropertyNameAttribute>(inherit: false)?.Name
            ?? VespaNaming.ToSnakeCase(property.Name);
        if (name != jsonName)
            throw new NotSupportedException(
                $"Property '{property.Name}' maps to '{name}' in Vespa metadata but '{jsonName}' in document JSON. Declare a matching VespaField name on the property or use an explicit native expression or field name.");

        return name;
    }

    internal static PropertyInfo EffectiveProperty<T>(PropertyInfo selected)
    {
        if (selected.DeclaringType == typeof(T))
            return selected;

        if (typeof(T).IsInterface)
        {
            foreach (var type in typeof(T).GetInterfaces().Prepend(typeof(T)))
                if (!type.IsAssignableFrom(selected.DeclaringType) && type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .Any(property => property.Name == selected.Name))
                    throw new NotSupportedException(
                        $"Property '{selected.Name}' is redeclared on '{typeof(T).Name}'. Select the property declared by the document model.");

            return selected;
        }

        PropertyInfo? effective;
        try
        {
            effective = typeof(T).GetProperty(selected.Name, BindingFlags.Public | BindingFlags.Instance);
        }
        catch (AmbiguousMatchException ex)
        {
            throw new NotSupportedException($"Property '{selected.Name}' has an ambiguous document binding on '{typeof(T).Name}'.", ex);
        }
        if (effective?.DeclaringType == selected.DeclaringType)
            return selected;
        if (selected.GetMethod!.IsVirtual && effective?.GetMethod is { IsPublic: true } getter &&
            getter.GetBaseDefinition() == selected.GetMethod.GetBaseDefinition())
            return effective;

        throw new NotSupportedException(
            $"Property '{selected.Name}' is hidden by another member on '{typeof(T).Name}'. Select the property declared by the document model.");
    }
}
