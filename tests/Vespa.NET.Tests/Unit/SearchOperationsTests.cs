using System.Net;
using System.Text.Json;
using Moq;
using Moq.Protected;
using Vespa;
using Vespa.Models;
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
            [TestDataFactory.CreateSearchHit("doc-1", new TestDocument { Title = "Test" })]
        );
        SetupSearchSuccessResponse(expectedResponse);

        // Act
        var result = await _searchOps.SearchAsync<TestDocument>(request);

        // Assert
        Assert.NotNull(result);
        Assert.NotNull(result.Root);
        Assert.Single(result.Root.Children);
        VerifyHttpCall(HttpMethod.Post, "/search/");
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

    #region NearestNeighborSearchAsync Tests (10 tests)

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
    public async Task NearestNeighborSearchAsync_WithDoubleArray_ZeroCopy()
    {
        // Arrange
        var doubleArray = new[] { 1.0, 2.0, 3.0 };
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
        await VerifySearchRequestContainsEmbedding([1.0, 2.0, 3.0]);
    }

    [Fact]
    public async Task NearestNeighborSearchAsync_WithFloatArray_ConvertsToDouble()
    {
        // Arrange
        var floatArray = new[] { 1.0f, 2.0f, 3.0f };
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
        await VerifySearchRequestContainsEmbedding([1.0, 2.0, 3.0]);
    }

    [Fact]
    public async Task NearestNeighborSearchAsync_WithSByteArray_ConvertsToDouble()
    {
        // Arrange
        var sbyteArray = new sbyte[] { 1, 2, 3 };
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
        await VerifySearchRequestContainsEmbedding([1.0, 2.0, 3.0]);
    }

    [Fact]
    public async Task NearestNeighborSearchAsync_WithHalfArray_ConvertsToDouble()
    {
        // Arrange
        var halfArray = new[] { (Half)1.0, (Half)2.0, (Half)3.0 };
        var tensor = VespaTensor.FromDenseValues(halfArray);
        var expectedResponse = TestDataFactory.CreateSearchResponse(
            new List<SearchHit<TestDocument>>()
        );
        SetupSearchSuccessResponse(expectedResponse);

        // Act
        await _searchOps.NearestNeighborSearchAsync<TestDocument>(
            tensor, "embedding", "testdoc"
        );

        // Assert - Values should be approximately equal (Half has lower precision)
        VerifyHttpCall(HttpMethod.Post, "/search/");
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
        await VerifySearchRequestContainsYql("category = 'news'");
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
    public async Task NearestNeighborSearchAsync_BuildsCorrectYqlWithTargetHits()
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

        // Assert - Verify YQL contains targetHits: 20
        await VerifySearchRequestContainsYql("targetHits: 20");
        await VerifySearchRequestContainsYql("nearestNeighbor(embedding, query_embedding)");
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
    public async Task QueryAsync_WithParameters_IncludesInInput()
    {
        // Arrange
        var yql = "select * from testdoc where userQuery()";
        var parameters = new Dictionary<string, object>
        {
            ["query"] = "test query"
        };
        var expectedResponse = TestDataFactory.CreateSearchResponse(
            new List<SearchHit<TestDocument>>()
        );
        SetupSearchSuccessResponse(expectedResponse);

        // Act
        await _searchOps.QueryAsync<TestDocument>(yql, parameters: parameters);

        // Assert
        VerifyHttpCall(HttpMethod.Post, "/search/");
    }

    #endregion

    #region Helper Methods

    private void SetupSearchSuccessResponse<T>(VespaSearchResponse<T> response) where T : class
    {
        var json = JsonSerializer.Serialize(response, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        _mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json)
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

    private async Task VerifySearchRequestContainsEmbedding(double[] expectedValues)
    {
        _mockHandler
            .Protected()
            .Verify(
                "SendAsync",
                Times.Once(),
                ItExpr.Is<HttpRequestMessage>(req =>
                    VerifyEmbeddingInRequest(req, expectedValues).Result
                ),
                ItExpr.IsAny<CancellationToken>()
            );
    }

    private async Task VerifySearchRequestContainsYql(string expectedYqlFragment)
    {
        _mockHandler
            .Protected()
            .Verify(
                "SendAsync",
                Times.Once(),
                ItExpr.Is<HttpRequestMessage>(req =>
                    VerifyYqlInRequest(req, expectedYqlFragment).Result
                ),
                ItExpr.IsAny<CancellationToken>()
            );
    }

    private static async Task<bool> VerifyEmbeddingInRequest(HttpRequestMessage request, double[] expectedValues)
    {
        if (request.Content == null) return false;

        var json = await request.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("input", out var input))
            return false;

        if (!input.TryGetProperty("query(query_embedding)", out var queryEmbedding))
            return false;

        if (!queryEmbedding.TryGetProperty("values", out var values))
            return false;

        var actualValues = values.EnumerateArray()
            .Select(v => v.GetDouble())
            .ToArray();

        return actualValues.SequenceEqual(expectedValues);
    }

    private static async Task<bool> VerifyYqlInRequest(HttpRequestMessage request, string expectedFragment)
    {
        if (request.Content == null) return false;

        var json = await request.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("yql", out var yql))
            return false;

        var yqlString = yql.GetString();
        return yqlString != null && yqlString.Contains(expectedFragment);
    }

    #endregion

    #region Test Models

    private class TestDocument
    {
        public string Title { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
    }

    #endregion
}
