using System.Linq.Expressions;
using Vespa.Models.Attributes;

namespace Vespa.Query;

/// <summary>
/// Builds an ordered list of attribute and relevance keys for <c>ranking.sorting</c>.
/// </summary>
public sealed class SortingBuilder
{
    private readonly List<string> _keys = [];

    /// <summary>
    /// Appends a sort key for a Vespa attribute. The first key is the primary sort.
    /// </summary>
    public SortingBuilder OrderBy(string attribute, SortDirection direction = SortDirection.Ascending)
    {
        if (!YqlIdentifier.IsValid(attribute) || attribute.Contains('{'))
            throw new ArgumentException("Expected a Vespa attribute name, such as 'price' or 'labels.key'.", nameof(attribute));

        return Append(attribute, direction);
    }

    /// <summary>
    /// Appends a sort key using the field name mapped from a model property.
    /// </summary>
    public SortingBuilder OrderBy<T>(Expression<Func<T, object?>> selector, SortDirection direction = SortDirection.Ascending)
        where T : class => OrderBy(VespaDocumentMeta.FieldName(selector), direction);

    /// <summary>
    /// Appends the relevance score as a sort key, highest score first by default.
    /// </summary>
    public SortingBuilder OrderByRelevance(SortDirection direction = SortDirection.Descending) =>
        Append("[relevance]", direction);

    /// <summary>
    /// Builds the space-separated Vespa sort specification.
    /// </summary>
    public string Build()
    {
        if (_keys.Count == 0)
            throw new InvalidOperationException("Add at least one sort key before building a sort specification.");

        return string.Join(' ', _keys);
    }

    private SortingBuilder Append(string key, SortDirection direction)
    {
        _keys.Add((direction.IsAscending() ? "+" : "-") + key);
        return this;
    }
}
