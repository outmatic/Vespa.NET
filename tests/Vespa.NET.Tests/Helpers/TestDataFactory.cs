using System.Net;
using System.Text;
using System.Text.Json;
using Vespa.Feed;
using Vespa.Models;
using Vespa.Models.Tensors;

namespace Vespa.Tests.Helpers;

/// <summary>
/// Factory for creating test data objects
/// </summary>
public static class TestDataFactory
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    #region Tensor Factory Methods

    /// <summary>
    /// Create a dense tensor with float values
    /// </summary>
    public static VespaTensor CreateFloatTensor(int size, string? tensorType = null)
    {
        var values = Enumerable.Range(0, size).Select(i => (float)i).ToArray();
        return VespaTensor.FromDenseValues(values, tensorType);
    }

    /// <summary>
    /// Create a dense tensor with double values
    /// </summary>
    public static VespaTensor CreateDoubleTensor(int size, string? tensorType = null)
    {
        var values = Enumerable.Range(0, size).Select(i => (double)i).ToArray();
        return VespaTensor.FromDenseValues(values, tensorType);
    }

    /// <summary>
    /// Create a dense tensor with sbyte values
    /// </summary>
    public static VespaTensor CreateInt8Tensor(int size, string? tensorType = null)
    {
        var values = Enumerable.Range(0, size).Select(i => (sbyte)(i % 128)).ToArray();
        return VespaTensor.FromDenseValues(values, tensorType);
    }

    /// <summary>
    /// Create a dense tensor with Half (bfloat16) values
    /// </summary>
    public static VespaTensor CreateHalfTensor(int size, string? tensorType = null)
    {
        var values = Enumerable.Range(0, size).Select(i => (Half)i).ToArray();
        return VespaTensor.FromDenseValues(values, tensorType);
    }

    /// <summary>
    /// Create a mapped tensor (single dimension sparse)
    /// </summary>
    public static VespaTensor CreateMappedTensor(Dictionary<string, double> values, string? tensorType = null)
    {
        return VespaTensor.FromMappedValues(values, tensorType);
    }

    /// <summary>
    /// Create a mixed sparse tensor
    /// </summary>
    public static VespaTensor CreateMixedSparseTensor(
        Dictionary<string, double[]> values,
        string? tensorType = null)
    {
        return VespaTensor.FromMixedSingleSparse(values, tensorType);
    }

    #endregion

    #region Feed Document Factory Methods

    /// <summary>
    /// Create a feed document with the specified ID and fields
    /// </summary>
    public static FeedDocument<T> CreateFeedDocument<T>(string id, T fields) where T : class
    {
        return new FeedDocument<T>
        {
            Id = id,
            Fields = fields
        };
    }

    /// <summary>
    /// Create multiple feed documents
    /// </summary>
    public static List<FeedDocument<T>> CreateFeedDocuments<T>(int count, Func<int, T> fieldsFactory) where T : class
    {
        return Enumerable.Range(0, count)
            .Select(i => CreateFeedDocument($"doc-{i}", fieldsFactory(i)))
            .ToList();
    }

    #endregion

    #region HTTP Response Factory Methods

    /// <summary>
    /// Create a successful HTTP response
    /// </summary>
    public static HttpResponseMessage CreateSuccessResponse(string json = "{}")
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    /// <summary>
    /// Create a successful HTTP response with a serialized object
    /// </summary>
    public static HttpResponseMessage CreateSuccessResponse<T>(T data)
    {
        var json = JsonSerializer.Serialize(data, JsonOptions);
        return CreateSuccessResponse(json);
    }

    /// <summary>
    /// Create an error HTTP response
    /// </summary>
    public static HttpResponseMessage CreateErrorResponse(
        HttpStatusCode statusCode,
        string message = "Test error",
        string? summary = null)
    {
        var error = new VespaError
        {
            Message = message,
            Code = (int)statusCode,
            Summary = summary
        };

        var json = JsonSerializer.Serialize(error, JsonOptions);

        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    /// <summary>
    /// Create a 404 Not Found response
    /// </summary>
    public static HttpResponseMessage CreateNotFoundResponse(string? summary = null)
    {
        return CreateErrorResponse(HttpStatusCode.NotFound, "Document not found", summary);
    }

    /// <summary>
    /// Create a 500 Internal Server Error response
    /// </summary>
    public static HttpResponseMessage CreateServerErrorResponse(string message = "Internal server error")
    {
        return CreateErrorResponse(HttpStatusCode.InternalServerError, message);
    }

    /// <summary>
    /// Create an empty response with just a status code
    /// </summary>
    public static HttpResponseMessage CreateEmptyResponse(HttpStatusCode statusCode)
    {
        return new HttpResponseMessage(statusCode);
    }

    #endregion

    #region Vespa Response Factory Methods

    /// <summary>
    /// Create a VespaDocument for testing
    /// </summary>
    public static VespaDocument<T> CreateVespaDocument<T>(string id, T fields) where T : class
    {
        return new VespaDocument<T>
        {
            Id = id,
            Fields = fields
        };
    }

    /// <summary>
    /// Create a VespaResponse for testing
    /// </summary>
    public static VespaResponse CreateVespaResponse(bool isSuccess = true, int statusCode = 200)
    {
        return new VespaResponse
        {
            IsSuccess = isSuccess,
            StatusCode = statusCode
        };
    }

    #endregion

    #region Search Response Factory Methods

    /// <summary>
    /// Create a search response with hits
    /// </summary>
    public static VespaSearchResponse<T> CreateSearchResponse<T>(
        List<SearchHit<T>> hits,
        int totalCount = -1) where T : class
    {
        return new VespaSearchResponse<T>
        {
            Root = new SearchRoot<T>
            {
                Children = hits,
                Fields = new SearchFields
                {
                    TotalCount = totalCount >= 0 ? totalCount : hits.Count
                },
                Coverage = new Coverage
                {
                    CoveragePercentage = 100,
                    Documents = hits.Count,
                    Full = true
                }
            }
        };
    }

    /// <summary>
    /// Create a search hit
    /// </summary>
    public static SearchHit<T> CreateSearchHit<T>(string id, T fields, double relevance = 1.0) where T : class
    {
        return new SearchHit<T>
        {
            Id = id,
            Relevance = relevance,
            Fields = fields
        };
    }

    #endregion
}
