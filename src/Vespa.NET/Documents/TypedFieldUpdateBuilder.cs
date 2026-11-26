using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using Vespa.Models;
using Vespa.Models.Attributes;

namespace Vespa.Documents;

/// <summary>
/// Fluent builder for typed field-level update operations.
/// Field names use <see cref="VespaFieldAttribute.Name"/> when supplied, otherwise the snake_case property name.
/// Each selected property must have its own document JSON binding with the same name.
/// </summary>
public sealed class TypedFieldUpdateBuilder<
    [DynamicallyAccessedMembers(DocumentFieldSelector.ModelMembers)] T> where T : class
{
    private readonly Dictionary<string, FieldOperation> _fields = [];

    /// <summary>
    /// Adds or replaces a field operation selected by a lambda expression.
    /// </summary>
    /// <param name="selector">Property selector, e.g. <c>m => m.PlayCount</c>.</param>
    /// <param name="operation">The operation to apply, e.g. <c>FieldOp.Increment()</c>.</param>
    public TypedFieldUpdateBuilder<T> Field(
        Expression<Func<T, object?>> selector,
        FieldOperation operation)
    {
        _fields[DocumentFieldSelector.FieldName(selector)] = operation;
        return this;
    }

    internal Dictionary<string, FieldOperation> Build() => _fields;
}
