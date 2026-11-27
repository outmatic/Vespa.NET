using Vespa.Models;

namespace Vespa.Query;

/// <summary>Sets Simple Query Language parameters on a search request.</summary>
public static class SimpleQueryExtensions
{
    /// <summary>Sets non-ranked recall constraints. Every term must be required or excluded.</summary>
    public static VespaSearchRequest WithRecall(this VespaSearchRequest request, SimpleQueryBuilder recall)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(recall);
        return request with { Recall = recall.BuildRecall() };
    }

    /// <summary>Sets a model filter combining required, excluded, and ranking terms with the model query.</summary>
    public static VespaSearchRequest WithModelFilter(this VespaSearchRequest request, SimpleQueryBuilder filter)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(filter);
        return request with { ModelFilter = filter.Build() };
    }

    /// <summary>Sets a composed model query with Vespa's <c>any</c> parser type to support signed terms.</summary>
    public static VespaSearchRequest WithSimpleQuery(this VespaSearchRequest request, SimpleQueryBuilder query)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(query);
        return request with { ModelQueryString = query.Build(), ModelType = "any" };
    }

    /// <summary>Sets user text and selects Vespa's <c>tokenize</c> parser type to ignore simple-query syntax.</summary>
    public static VespaSearchRequest WithModelQueryText(this VespaSearchRequest request, string text)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        return request with { ModelQueryString = text, ModelType = "tokenize" };
    }
}
