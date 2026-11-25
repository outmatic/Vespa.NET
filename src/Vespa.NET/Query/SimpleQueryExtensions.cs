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
        request.Recall = recall.BuildRecall();
        return request;
    }

    /// <summary>Sets a model filter combining required, excluded, and ranking terms with the model query.</summary>
    public static VespaSearchRequest WithModelFilter(this VespaSearchRequest request, SimpleQueryBuilder filter)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(filter);
        request.ModelFilter = filter.Build();
        return request;
    }

    /// <summary>Sets a composed model query with Vespa's <c>any</c> parser type to support signed terms.</summary>
    public static VespaSearchRequest WithSimpleQuery(this VespaSearchRequest request, SimpleQueryBuilder query)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(query);
        request.ModelQueryString = query.Build();
        request.ModelType = "any";
        return request;
    }

    /// <summary>Sets user text and selects Vespa's <c>tokenize</c> parser type to ignore simple-query syntax.</summary>
    public static VespaSearchRequest WithModelQueryText(this VespaSearchRequest request, string text)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        request.ModelQueryString = text;
        request.ModelType = "tokenize";
        return request;
    }
}
