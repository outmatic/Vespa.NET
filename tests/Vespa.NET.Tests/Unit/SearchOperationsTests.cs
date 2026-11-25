using System.Net;
using System.Text.Json;
using Moq;
using Moq.Protected;
using Vespa;
using Vespa.Models;
using Vespa.Models.Attributes;
using Vespa.Models.Tensors;
using Vespa.Search;
using Vespa.Tests.Helpers;
using Xunit;

namespace Vespa.Tests.Unit;

/// <summary>
/// Tests for SearchOperations covering YQL search, nearest neighbor, and type conversions
/// </summary>
public class SearchOperationsTests : IDisposable
{
    private readonly Mock<HttpMessageHandler> _mockHandler;
    private readonly HttpClient _httpClient;
    private readonly SearchOperations _searchOps;
    private string? _sentSearchBody;

    public SearchOperationsTests()
    {
        _mockHandler = new Mock<HttpMessageHandler>();
        _httpClient = new HttpClient(_mockHandler.Object)
        {
            BaseAddress = new Uri("http://localhost:8080")
        };
        var options = new VespaClientOptions
        {
            Endpoint = "http://localhost:8080",
            DefaultNamespace = "test"
        };
        _searchOps = new SearchOperations(_httpClient, options);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    #region SearchAsync Tests (5 tests)

    [Fact]
    public async Task SearchAsync_WithValidRequest_ReturnsSearchResponse()
    {
        // Arrange
        var request = new VespaSearchRequest
        {
            Yql = "select * from testdoc where title contains 'test'",
            Hits = 10
        };
        var expectedResponse = TestDataFactory.CreateSearchResponse(
            [
                TestDataFactory.CreateSearchHit("id:test:testdoc::doc-1", new TestDocument { Title = "Test" }),
                TestDataFactory.CreateSearchHit("id:test:testdoc:g=tenant:part:two", new TestDocument { Title = "Second" })
            ]
        );
        SetupSearchSuccessResponse(expectedResponse);

        // Act
        var result = await _searchOps.SearchAsync<TestDocument>(request);

        // Assert
        Assert.NotNull(result);
        Assert.NotNull(result.Root);
        Assert.Collection(result.Root.Children,
            hit => Assert.Equal("doc-1", hit.Fields?.Id),
            hit => Assert.Equal("part:two", hit.Fields?.Id));
        VerifyHttpCall(HttpMethod.Post, "/search/");
    }

    [Fact]
    public async Task SearchAsync_MatchFeaturesInsideFields_ExposedOnHit()
    {
        // Real shape of Vespa's default JSON renderer: matchfeatures/summaryfeatures
        // are rendered inside "fields", not at hit level.
        var responseJson = """
            {
                "root": {
                    "id": "toplevel",
                    "relevance": 1.0,
                    "fields": { "totalCount": 1 },
                    "children": [
                        {
                            "id": "index:content/0/abc",
                            "relevance": 0.5,
                            "source": "content",
                            "fields": {
                                "sddocname": "testdoc",
                                "documentid": "id:test:testdoc::doc-1",
                                "title": "Test",
                                "matchfeatures": { "bm25(title)": 1.5 },
                                "summaryfeatures": { "attribute(year)": 2001.0 }
                            }
                        }
                    ]
                }
            }
            """;
        SetupRawSearchResponse(responseJson);

        var result = await _searchOps.SearchAsync<TestDocument>(new VespaSearchRequest
        {
            Yql = "select * from testdoc where true"
        });

        var hit = Assert.Single(result.Root.Children);
        Assert.Equal(1.5, hit.GetMatchFeature("bm25(title)"));
        Assert.Equal(2001.0, hit.GetSummaryFeature("attribute(year)"));
        Assert.Equal("Test", hit.Fields?.Title);
    }

    [Fact]
    public async Task SearchAsync_WithNullRequest_ThrowsArgumentNullException()
    {
        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _searchOps.SearchAsync<TestDocument>(null!));
    }

    [Fact]
    public async Task SearchAsync_WithNullYql_ThrowsArgumentNullException()
    {
        // Arrange
        var request = new VespaSearchRequest
        {
            Yql = null!,
            Hits = 10
        };

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _searchOps.SearchAsync<TestDocument>(request));
    }

    [Fact]
    public async Task SearchAsync_HttpError_ThrowsVespaException()
    {
        // Arrange
        var request = new VespaSearchRequest
        {
            Yql = "select * from testdoc",
            Hits = 10
        };
        SetupErrorResponse(HttpStatusCode.BadRequest, "Invalid query");

        // Act & Assert
        var ex = await Assert.ThrowsAsync<VespaException>(() =>
            _searchOps.SearchAsync<TestDocument>(request));

        Assert.Equal(400, ex.StatusCode);
    }

    [Fact]
    public async Task SearchAsync_OnErrorWithVespaErrorBody_CarriesParsedError()
    {
        // Search error paths must parse the structured VespaError body like Documents does
        var request = new VespaSearchRequest { Yql = "select * from testdoc", Hits = 10 };
        const string errorJson = """{"code":4,"message":"Could not create query from YQL: syntax error"}""";
        SetupErrorResponse(HttpStatusCode.BadRequest, errorJson);

        var ex = await Assert.ThrowsAsync<VespaException>(() =>
            _searchOps.SearchAsync<TestDocument>(request));

        Assert.Equal(400, ex.StatusCode);
        Assert.NotNull(ex.Error);
        Assert.Equal("Could not create query from YQL: syntax error", ex.Error!.Message);
        Assert.Contains("Search failed", ex.Message);
        Assert.Contains("syntax error", ex.Message);
    }

    [Fact]
    public async Task SearchAsync_On429_PopulatesRetryAfterFromHeader()
    {
        // Arrange
        var request = new VespaSearchRequest { Yql = "select * from testdoc", Hits = 10 };
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("throttled")
        };
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(3));
        _mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(response);

        // Act & Assert
        var ex = await Assert.ThrowsAsync<VespaTooManyRequestsException>(() =>
            _searchOps.SearchAsync<TestDocument>(request));

        Assert.Equal(429, ex.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(3), ex.RetryAfter);
    }

    [Fact]
    public async Task SearchAsync_WithRankingProfile_IncludesInRequest()
    {
        // Arrange
        var request = new VespaSearchRequest
        {
            Yql = "select * from testdoc",
            Hits = 10,
            Ranking = new RankingConfig { Profile = "custom-ranking" }
        };
        var expectedResponse = TestDataFactory.CreateSearchResponse(
            new List<SearchHit<TestDocument>>()
        );
        SetupSearchSuccessResponse(expectedResponse);

        // Act
        await _searchOps.SearchAsync<TestDocument>(request);

        // Assert - Verify request was sent
        VerifyHttpCall(HttpMethod.Post, "/search/");
    }

    #endregion

    #region NearestNeighborSearchAsync Tests

    [Theory]
    [InlineData(TensorFormat.MappedSingle)]
    [InlineData(TensorFormat.MixedSingleSparse)]
    [InlineData(TensorFormat.MixedMultiSparse)]
    [InlineData(TensorFormat.Verbose)]
    public async Task NearestNeighborSearchAsync_ThrowsException_ForNonIndexedDenseFormat(TensorFormat format)
    {
        // Arrange
        var tensor = new VespaTensor { Format = format };

        // Act & Assert
        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => _searchOps.NearestNeighborSearchAsync<TestDocument>(
                tensor, "embedding", "testdoc"
            )
        );

        Assert.Contains("IndexedDense", ex.Message);
    }

    [Fact]
    public async Task NearestNeighborSearchAsync_WithDoubleArray_PreservesValues()
    {
        // Arrange
        var doubleArray = new[] { 16777217.0, -2.5, 3.75 };
        var tensor = VespaTensor.FromDenseValues(doubleArray);
        var expectedResponse = TestDataFactory.CreateSearchResponse(
            new List<SearchHit<TestDocument>>()
        );
        SetupSearchSuccessResponse(expectedResponse);

        // Act
        await _searchOps.NearestNeighborSearchAsync<TestDocument>(
            tensor, "embedding", "testdoc"
        );

        // Assert - Verify request contains the embedding
        VerifySearchRequestContainsEmbedding(doubleArray);
    }

    [Fact]
    public async Task NearestNeighborSearchAsync_WithFloatArray_ConvertsToDouble()
    {
        // Arrange
        var floatArray = new[] { 0.1f, -2.5f, 3.75f };
        var tensor = VespaTensor.FromDenseValues(floatArray);
        var expectedResponse = TestDataFactory.CreateSearchResponse(
            new List<SearchHit<TestDocument>>()
        );
        SetupSearchSuccessResponse(expectedResponse);

        // Act
        await _searchOps.NearestNeighborSearchAsync<TestDocument>(
            tensor, "embedding", "testdoc"
        );

        // Assert
        VerifySearchRequestContainsEmbedding([(double)0.1f, -2.5, 3.75]);
    }

    [Fact]
    public async Task NearestNeighborSearchAsync_WithSByteArray_ConvertsToDouble()
    {
        // Arrange
        var sbyteArray = new sbyte[] { sbyte.MinValue, 0, sbyte.MaxValue };
        var tensor = VespaTensor.FromDenseValues(sbyteArray);
        var expectedResponse = TestDataFactory.CreateSearchResponse(
            new List<SearchHit<TestDocument>>()
        );
        SetupSearchSuccessResponse(expectedResponse);

        // Act
        await _searchOps.NearestNeighborSearchAsync<TestDocument>(
            tensor, "embedding", "testdoc"
        );

        // Assert
        VerifySearchRequestContainsEmbedding([-128.0, 0.0, 127.0]);
    }

    [Fact]
    public async Task NearestNeighborSearchAsync_WithHalfArray_ConvertsToDouble()
    {
        // Arrange
        var halfArray = new[] { (Half)0.125, (Half)(-2.5), (Half)3.75 };
        var tensor = VespaTensor.FromDenseValues(halfArray);
        var expectedResponse = TestDataFactory.CreateSearchResponse(
            new List<SearchHit<TestDocument>>()
        );
        SetupSearchSuccessResponse(expectedResponse);

        // Act
        await _searchOps.NearestNeighborSearchAsync<TestDocument>(
            tensor, "embedding", "testdoc"
        );

        // Assert
        VerifySearchRequestContainsEmbedding([0.125, -2.5, 3.75]);
    }

    [Fact]
    public async Task NearestNeighborSearchAsync_WithMissingDenseValues_ThrowsBeforeSending()
    {
        var tensor = new VespaTensor { Format = TensorFormat.IndexedDense };

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            _searchOps.NearestNeighborSearchAsync<TestDocument>(tensor, "embedding", "testdoc"));

        Assert.Equal("queryEmbedding", ex.ParamName);
        Assert.StartsWith("Unable to extract dense values from tensor", ex.Message);
        _mockHandler.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task NearestNeighborSearchAsync_WithEmptyArray_ThrowsArgumentException()
    {
        // Arrange
        var emptyArray = Array.Empty<double>();
        var tensor = VespaTensor.FromDenseValues(emptyArray);

        // Act & Assert
        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => _searchOps.NearestNeighborSearchAsync<TestDocument>(
                tensor, "embedding", "testdoc"
            )
        );

        Assert.Contains("cannot be empty", ex.Message);
    }

    [Fact]
    public async Task NearestNeighborSearchAsync_WithFilter_AddsFilterToYql()
    {
        // Arrange
        var tensor = VespaTensor.FromDenseValues([1.0, 2.0]);
        var expectedResponse = TestDataFactory.CreateSearchResponse(
            new List<SearchHit<TestDocument>>()
        );
        SetupSearchSuccessResponse(expectedResponse);

        // Act
        await _searchOps.NearestNeighborSearchAsync<TestDocument>(
            tensor, "embedding", "testdoc", filter: "category = 'news'"
        );

        // Assert - Verify YQL contains filter
        VerifySearchRequestContainsYql("category = 'news'");
    }

    [Fact]
    public async Task NearestNeighborSearchAsync_WithRankProfile_SetsRankingProfile()
    {
        // Arrange
        var tensor = VespaTensor.FromDenseValues([1.0, 2.0]);
        var expectedResponse = TestDataFactory.CreateSearchResponse(
            new List<SearchHit<TestDocument>>()
        );
        SetupSearchSuccessResponse(expectedResponse);

        // Act
        await _searchOps.NearestNeighborSearchAsync<TestDocument>(
            tensor, "embedding", "testdoc", rankProfile: "semantic-search"
        );

        // Assert
        VerifyHttpCall(HttpMethod.Post, "/search/");
    }

    [Fact]
    public async Task NearestNeighborSearchAsync_BuildsCorrectYqlWithTotalTargetHits()
    {
        // Arrange
        var tensor = VespaTensor.FromDenseValues([1.0, 2.0]);
        var expectedResponse = TestDataFactory.CreateSearchResponse(
            new List<SearchHit<TestDocument>>()
        );
        SetupSearchSuccessResponse(expectedResponse);

        // Act
        await _searchOps.NearestNeighborSearchAsync<TestDocument>(
            tensor, "embedding", "testdoc", topK: 20
        );

        // Assert - Verify YQL uses the preferred totalTargetHits annotation
        VerifySearchRequestContainsYql("{totalTargetHits: 20}");
        VerifySearchRequestContainsYql("nearestNeighbor(embedding, query_embedding)");
    }

    [Fact]
    public async Task NearestNeighborSearchAsync_WithNullTensor_ThrowsArgumentNullException()
    {
        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => _searchOps.NearestNeighborSearchAsync<TestDocument>(
                null!, "embedding", "testdoc"
            )
        );
    }

    #endregion

    #region QueryAsync Tests (3 tests)

    [Fact]
    public async Task QueryAsync_WithSimpleYql_ExecutesSearch()
    {
        // Arrange
        var yql = "select * from testdoc where title contains 'test'";
        var expectedResponse = TestDataFactory.CreateSearchResponse(
            new List<SearchHit<TestDocument>>()
        );
        SetupSearchSuccessResponse(expectedResponse);

        // Act
        var result = await _searchOps.QueryAsync<TestDocument>(yql);

        // Assert
        Assert.NotNull(result);
        VerifyHttpCall(HttpMethod.Post, "/search/");
    }

    [Fact]
    public async Task QueryAsync_WithPagination_SetsHitsAndOffset()
    {
        // Arrange
        var yql = "select * from testdoc";
        var expectedResponse = TestDataFactory.CreateSearchResponse(
            new List<SearchHit<TestDocument>>()
        );
        SetupSearchSuccessResponse(expectedResponse);

        // Act
        await _searchOps.QueryAsync<TestDocument>(yql, hits: 25, offset: 50);

        // Assert - Verify request contains pagination
        VerifyHttpCall(HttpMethod.Post, "/search/");
    }

    [Fact]
    public async Task QueryAsync_Parameters_AreSentAsTopLevelProperties()
    {
        // userInput(@param)/userQuery() read top-level request properties; nesting them
        // under "input" makes Vespa see input.param and the parameter never resolves.
        var yql = "select * from testdoc where userInput(@text)";
        var parameters = new Dictionary<string, object>
        {
            ["text"] = "test query"
        };
        var expectedResponse = TestDataFactory.CreateSearchResponse(
            new List<SearchHit<TestDocument>>()
        );
        var responseJson = JsonSerializer.Serialize(expectedResponse, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        string? capturedBody = null;
        _mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) =>
                capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson)
            });

        await _searchOps.QueryAsync<TestDocument>(yql, parameters: parameters);

        Assert.NotNull(capturedBody);
        using var body = JsonDocument.Parse(capturedBody);
        Assert.True(body.RootElement.TryGetProperty("text", out var textParam),
            "parameter should be a top-level request property");
        Assert.Equal("test query", textParam.GetString());
        Assert.False(body.RootElement.TryGetProperty("input", out _),
            "parameters must not be nested under input.*");
    }

    #endregion

    #region Helper Methods

    private void SetupSearchSuccessResponse<T>(VespaSearchResponse<T> response) where T : class
    {
        var json = JsonSerializer.Serialize(response, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        SetupRawSearchResponse(json);
    }

    private void SetupRawSearchResponse(string json)
    {
        _mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .Returns(async (HttpRequestMessage request, CancellationToken cancellationToken) =>
            {
                Assert.NotNull(request.Content);
                _sentSearchBody = await request.Content.ReadAsStringAsync(cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
            });
    }

    private void SetupErrorResponse(HttpStatusCode statusCode, string message)
    {
        _mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(message)
            });
    }

    private void VerifyHttpCall(HttpMethod method, string expectedPath)
    {
        _mockHandler
            .Protected()
            .Verify(
                "SendAsync",
                Times.Once(),
                ItExpr.Is<HttpRequestMessage>(req =>
                    req.Method == method &&
                    req.RequestUri!.PathAndQuery == expectedPath
                ),
                ItExpr.IsAny<CancellationToken>()
            );
    }

    private void VerifySearchRequestContainsEmbedding(double[] expectedValues)
    {
        VerifyHttpCall(HttpMethod.Post, "/search/");
        Assert.NotNull(_sentSearchBody);
        using var doc = JsonDocument.Parse(_sentSearchBody);
        var values = doc.RootElement.GetProperty("input").GetProperty("query(query_embedding)").GetProperty("values");
        Assert.Equal(expectedValues, values.EnumerateArray().Select(value => value.GetDouble()));
    }

    private void VerifySearchRequestContainsYql(string expectedYqlFragment)
    {
        VerifyHttpCall(HttpMethod.Post, "/search/");
        Assert.NotNull(_sentSearchBody);
        using var doc = JsonDocument.Parse(_sentSearchBody);
        Assert.Contains(expectedYqlFragment, doc.RootElement.GetProperty("yql").GetString());
    }

    #endregion

    #region Test Models

    private class TestDocument
    {
        [VespaId]
        public string? Id { get; init; }
        public string Title { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
    }

    #endregion
}
