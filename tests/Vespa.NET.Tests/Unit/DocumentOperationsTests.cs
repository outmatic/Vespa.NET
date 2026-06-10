using System.Net;
using System.Text.Json;
using Moq;
using Moq.Protected;
using Vespa;
using Vespa.Documents;
using Vespa.Models;
using Vespa.Models.Attributes;
using Xunit;

namespace Vespa.Tests.Unit;

/// <summary>
/// Tests for DocumentOperations covering CRUD operations, URL escaping, and error handling
/// </summary>
public class DocumentOperationsTests : IDisposable
{
    private readonly Mock<HttpMessageHandler> _mockHandler;
    private readonly HttpClient _httpClient;
    private readonly VespaClientOptions _options;
    private readonly DocumentOperations _documentOps;

    public DocumentOperationsTests()
    {
        _mockHandler = new Mock<HttpMessageHandler>();
        _httpClient = new HttpClient(_mockHandler.Object)
        {
            BaseAddress = new Uri("http://localhost:8080")
        };
        _options = new VespaClientOptions
        {
            Endpoint = "http://localhost:8080",
            DefaultNamespace = "test"
        };
        _documentOps = new DocumentOperations(_httpClient, _options);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    #region PutAsync Tests (7 tests)

    [Fact]
    public async Task PutAsync_SuccessfulPut_ReturnsSuccessResponse()
    {
        // Arrange
        var doc = new { name = "Test", value = 123 };
        SetupSuccessResponse();

        // Act
        var result = await _documentOps.PutAsync("doc-1", doc, "testdoc");

        // Assert
        Assert.NotNull(result);
        Assert.True(result.IsSuccess);
        Assert.Equal(200, result.StatusCode);
        VerifyHttpCall(HttpMethod.Post, "/document/v1/test/testdoc/docid/doc-1");
    }

    [Theory]
    [InlineData("test-id", "test-id")]
    [InlineData("id/with/slashes", "id%2Fwith%2Fslashes")]
    [InlineData("id with spaces", "id%20with%20spaces")]
    [InlineData("id?with&params", "id%3Fwith%26params")]
    [InlineData("id@with#special", "id%40with%23special")]
    public async Task PutAsync_EscapesDocumentId_Correctly(string docId, string expectedEscaped)
    {
        // Arrange
        var doc = new { field = "value" };
        var expectedUrl = $"/document/v1/test/testdoc/docid/{expectedEscaped}";
        SetupSuccessResponse();

        // Act
        await _documentOps.PutAsync(docId, doc, "testdoc");

        // Assert
        VerifyHttpCall(HttpMethod.Post, expectedUrl);
    }

    [Fact]
    public async Task PutAsync_WithCustomNamespace_UsesCustomNamespace()
    {
        // Arrange
        var doc = new { field = "value" };
        SetupSuccessResponse();

        // Act
        await _documentOps.PutAsync("doc-1", doc, "testdoc", "custom-ns");

        // Assert
        VerifyHttpCall(HttpMethod.Post, "/document/v1/custom-ns/testdoc/docid/doc-1");
    }

    [Fact]
    public async Task PutAsync_WithDefaultNamespace_UsesOptionsDefaultNamespace()
    {
        // Arrange
        var doc = new { field = "value" };
        SetupSuccessResponse();

        // Act
        await _documentOps.PutAsync("doc-1", doc, "testdoc");

        // Assert
        VerifyHttpCall(HttpMethod.Post, "/document/v1/test/testdoc/docid/doc-1");
    }

    [Fact]
    public async Task PutAsync_WithNullDocumentId_ThrowsArgumentNullException()
    {
        // Arrange
        var doc = new { field = "value" };

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _documentOps.PutAsync(null!, doc, "testdoc"));
    }

    [Fact]
    public async Task PutAsync_WithNullDocument_ThrowsArgumentNullException()
    {
        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _documentOps.PutAsync("doc-1", (object)null!, "testdoc"));
    }

    [Fact]
    public async Task PutAsync_HttpError_ThrowsVespaException()
    {
        // Arrange
        var doc = new { field = "value" };
        SetupErrorResponse(HttpStatusCode.InternalServerError, "Server error");

        // Act & Assert
        var ex = await Assert.ThrowsAnyAsync<VespaException>(() =>
            _documentOps.PutAsync("doc-1", doc, "testdoc"));

        Assert.IsType<VespaServerException>(ex);
        Assert.Equal(500, ex.StatusCode);
        Assert.Contains("Server error", ex.Message);
    }

    #endregion

    #region GetAsync Tests (6 tests)

    [Fact]
    public async Task GetAsync_SuccessfulGet_ReturnsVespaDocumentWithShortenedId()
    {
        // Arrange
        var expectedDoc = new VespaDocument<TestDocument>
        {
            Id = "id:test:testdoc::doc-1",
            Fields = new TestDocument { Name = "Test", Value = 123 }
        };
        SetupGetSuccessResponse(expectedDoc);

        // Act
        var result = await _documentOps.GetAsync<TestDocument>("doc-1", "testdoc");

        // Assert
        Assert.NotNull(result);
        Assert.NotNull(result.Fields);
        Assert.Equal("doc-1", result.Id); // Verify it's shortened
        Assert.Equal(expectedDoc.Fields.Name, result.Fields.Name);
        Assert.Equal(expectedDoc.Fields.Value, result.Fields.Value);
        VerifyHttpCall(HttpMethod.Get, "/document/v1/test/testdoc/docid/doc-1");
    }

    [Fact]
    public async Task GetAsync_Returns_Null_On404()
    {
        // Arrange
        SetupNotFoundResponse();

        // Act
        var result = await _documentOps.GetAsync<TestDocument>("doc-1", "testdoc");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task GetAsync_ThrowsVespaException_OnOtherErrors()
    {
        // Arrange
        SetupErrorResponse(HttpStatusCode.InternalServerError, "Server error");

        // Act & Assert
        var ex = await Assert.ThrowsAnyAsync<VespaException>(() =>
            _documentOps.GetAsync<TestDocument>("doc-1", "testdoc"));

        Assert.IsType<VespaServerException>(ex);
        Assert.Equal(500, ex.StatusCode);
    }

    [Theory]
    [InlineData("id/with/slash", "id%2Fwith%2Fslash")]
    [InlineData("id with space", "id%20with%20space")]
    public async Task GetAsync_EscapesDocumentId_Correctly(string docId, string expectedEscaped)
    {
        // Arrange
        var expectedUrl = $"/document/v1/test/testdoc/docid/{expectedEscaped}";
        SetupGetSuccessResponse(new VespaDocument<TestDocument>
        {
            Id = "test",
            Fields = new TestDocument { Name = "Test" }
        });

        // Act
        await _documentOps.GetAsync<TestDocument>(docId, "testdoc");

        // Assert
        VerifyHttpCall(HttpMethod.Get, expectedUrl);
    }

    [Theory]
    // Full Vespa IDs are stripped to the user-specified part — which may contain "::"
    [InlineData("id:test:testdoc::album::dark-side", "album%3A%3Adark-side")]
    // g=/n= selectors occupy the key/value slot; the user part keeps its colons
    [InlineData("id:test:testdoc:g=group1:doc:1", "doc%3A1")]
    // Bare IDs that merely contain "::" are not full Vespa IDs and must not be truncated
    [InlineData("a::b", "a%3A%3Ab")]
    public async Task GetAsync_NormalizesId_WithoutCorruptingUserPart(string docId, string expectedEscaped)
    {
        var expectedUrl = $"/document/v1/test/testdoc/docid/{expectedEscaped}";
        SetupGetSuccessResponse(new VespaDocument<TestDocument>
        {
            Id = "test",
            Fields = new TestDocument { Name = "Test" }
        });

        await _documentOps.GetAsync<TestDocument>(docId, "testdoc");

        VerifyHttpCall(HttpMethod.Get, expectedUrl);
    }

    [Fact]
    public async Task GetAsync_WithCustomNamespace_UsesCustomNamespace()
    {
        // Arrange
        SetupGetSuccessResponse(new VespaDocument<TestDocument>
        {
            Id = "test",
            Fields = new TestDocument { Name = "Test" }
        });

        // Act
        await _documentOps.GetAsync<TestDocument>("doc-1", "testdoc", "custom");

        // Assert
        VerifyHttpCall(HttpMethod.Get, "/document/v1/custom/testdoc/docid/doc-1");
    }

    [Fact]
    public async Task GetAsync_WithNullDocumentId_ThrowsArgumentNullException()
    {
        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _documentOps.GetAsync<TestDocument>(null!, "testdoc"));
    }

    #endregion

    #region UpdateFieldsAsync Tests (6 tests)

    private static Dictionary<string, FieldOperation> SampleFieldOps() =>
        new() { ["name"] = FieldOp.Assign("Updated") };

    [Fact]
    public async Task UpdateFieldsAsync_SuccessfulUpdate_ReturnsSuccessResponse()
    {
        SetupSuccessResponse();

        var result = await _documentOps.UpdateFieldsAsync("doc-1", SampleFieldOps(), "testdoc");

        Assert.NotNull(result);
        Assert.True(result.IsSuccess);
        VerifyHttpCall(HttpMethod.Put, "/document/v1/test/testdoc/docid/doc-1");
    }

    [Fact]
    public async Task UpdateFieldsAsync_WithCreateIfMissingTrue_AddsQueryParameter()
    {
        SetupSuccessResponse();

        await _documentOps.UpdateFieldsAsync("doc-1", SampleFieldOps(), "testdoc", createIfMissing: true);

        VerifyHttpCall(HttpMethod.Put, "/document/v1/test/testdoc/docid/doc-1?create=true");
    }

    [Fact]
    public async Task UpdateFieldsAsync_WithCreateIfMissingFalse_NoQueryParameter()
    {
        SetupSuccessResponse();

        await _documentOps.UpdateFieldsAsync("doc-1", SampleFieldOps(), "testdoc", createIfMissing: false);

        VerifyHttpCall(HttpMethod.Put, "/document/v1/test/testdoc/docid/doc-1");
    }

    [Theory]
    [InlineData("id/slash", "id%2Fslash")]
    [InlineData("id space", "id%20space")]
    public async Task UpdateFieldsAsync_EscapesDocumentId_Correctly(string docId, string expectedEscaped)
    {
        var expectedUrl = $"/document/v1/test/testdoc/docid/{expectedEscaped}";
        SetupSuccessResponse();

        await _documentOps.UpdateFieldsAsync(docId, SampleFieldOps(), "testdoc");

        VerifyHttpCall(HttpMethod.Put, expectedUrl);
    }

    [Fact]
    public async Task UpdateFieldsAsync_WithNullFieldOperations_ThrowsArgumentNullException()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _documentOps.UpdateFieldsAsync("doc-1", (Dictionary<string, FieldOperation>)null!, "testdoc"));
    }

    [Fact]
    public async Task UpdateFieldsAsync_HttpError_ThrowsVespaException()
    {
        SetupErrorResponse(HttpStatusCode.BadRequest, "Invalid update");

        var ex = await Assert.ThrowsAsync<VespaException>(() =>
            _documentOps.UpdateFieldsAsync("doc-1", SampleFieldOps(), "testdoc"));

        Assert.Equal(400, ex.StatusCode);
    }

    #endregion

    #region DeleteAsync Tests (4 tests)

    [Fact]
    public async Task DeleteAsync_SuccessfulDelete_ReturnsSuccessResponse()
    {
        // Arrange
        SetupSuccessResponse();

        // Act
        var result = await _documentOps.DeleteAsync("doc-1", "testdoc");

        // Assert
        Assert.NotNull(result);
        Assert.True(result.IsSuccess);
        VerifyHttpCall(HttpMethod.Delete, "/document/v1/test/testdoc/docid/doc-1");
    }

    [Theory]
    [InlineData("id/slash", "id%2Fslash")]
    [InlineData("id#hash", "id%23hash")]
    public async Task DeleteAsync_EscapesDocumentId_Correctly(string docId, string expectedEscaped)
    {
        // Arrange
        var expectedUrl = $"/document/v1/test/testdoc/docid/{expectedEscaped}";
        SetupSuccessResponse();

        // Act
        await _documentOps.DeleteAsync(docId, "testdoc");

        // Assert
        VerifyHttpCall(HttpMethod.Delete, expectedUrl);
    }

    [Fact]
    public async Task DeleteAsync_WithNullDocumentId_ThrowsArgumentNullException()
    {
        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _documentOps.DeleteAsync(null!, "testdoc"));
    }

    [Fact]
    public async Task DeleteAsync_HttpError_ThrowsVespaException()
    {
        // Arrange
        SetupErrorResponse(HttpStatusCode.NotFound, "Document not found");

        // Act & Assert
        var ex = await Assert.ThrowsAnyAsync<VespaException>(() =>
            _documentOps.DeleteAsync("doc-1", "testdoc"));

        Assert.IsType<VespaNotFoundException>(ex);
        Assert.Equal(404, ex.StatusCode);
    }

    #endregion

    #region NormalizeId Tests (3 tests)

    [Theory]
    [InlineData("id:test:testdoc::doc-1", "/document/v1/test/testdoc/docid/doc-1")]
    [InlineData("id:myns:music::abc-123", "/document/v1/test/testdoc/docid/abc-123")]
    [InlineData("doc-1", "/document/v1/test/testdoc/docid/doc-1")]
    public async Task PutAsync_NormalizesFullVespaId(string inputId, string expectedPath)
    {
        var doc = new { field = "value" };
        SetupSuccessResponse();

        await _documentOps.PutAsync(inputId, doc, "testdoc");

        VerifyHttpCall(HttpMethod.Post, expectedPath);
    }

    [Theory]
    [InlineData("id:test:testdoc::doc-1", "/document/v1/test/testdoc/docid/doc-1")]
    [InlineData("doc-1", "/document/v1/test/testdoc/docid/doc-1")]
    public async Task GetAsync_NormalizesFullVespaId(string inputId, string expectedPath)
    {
        SetupGetSuccessResponse(new VespaDocument<TestDocument> { Id = "doc-1" });

        await _documentOps.GetAsync<TestDocument>(inputId, "testdoc");

        VerifyHttpCall(HttpMethod.Get, expectedPath);
    }

    [Theory]
    [InlineData("id:test:testdoc::doc-1", "/document/v1/test/testdoc/docid/doc-1")]
    [InlineData("doc-1", "/document/v1/test/testdoc/docid/doc-1")]
    public async Task DeleteAsync_NormalizesFullVespaId(string inputId, string expectedPath)
    {
        SetupSuccessResponse();

        await _documentOps.DeleteAsync(inputId, "testdoc");

        VerifyHttpCall(HttpMethod.Delete, expectedPath);
    }

    #endregion

    #region GetByGroupAsync / GetByNumberAsync Tests

    [Fact]
    public async Task GetByGroupAsync_BuildsCorrectUrl()
    {
        SetupGetSuccessResponse(new VespaDocument<TestDocument> { Id = "doc-1" });

        await _documentOps.GetByGroupAsync<TestDocument>("mygroup", "lid-1", "testdoc");

        VerifyHttpCall(HttpMethod.Get, "/document/v1/test/testdoc/group/mygroup/lid-1");
    }

    [Fact]
    public async Task GetByGroupAsync_EscapesGroupName()
    {
        SetupGetSuccessResponse(new VespaDocument<TestDocument> { Id = "doc-1" });

        await _documentOps.GetByGroupAsync<TestDocument>("my group", "lid-1", "testdoc");

        VerifyHttpCall(HttpMethod.Get, "/document/v1/test/testdoc/group/my%20group/lid-1");
    }

    [Fact]
    public async Task GetByGroupAsync_ReturnsNullOnNotFound()
    {
        SetupNotFoundResponse();

        var result = await _documentOps.GetByGroupAsync<TestDocument>("g", "lid-1", "testdoc");

        Assert.Null(result);
    }

    [Fact]
    public async Task GetByNumberAsync_BuildsCorrectUrl()
    {
        SetupGetSuccessResponse(new VespaDocument<TestDocument> { Id = "doc-1" });

        await _documentOps.GetByNumberAsync<TestDocument>(42L, "lid-1", "testdoc");

        VerifyHttpCall(HttpMethod.Get, "/document/v1/test/testdoc/number/42/lid-1");
    }

    #endregion

    #region Put/Update/Delete by Group/Number Tests

    [Fact]
    public async Task PutByGroupAsync_BuildsCorrectUrlAndMethod()
    {
        SetupSuccessResponse();

        await _documentOps.PutByGroupAsync("mygroup", "lid-1",
            new TestDocument { Name = "hello" }, "testdoc");

        VerifyHttpCall(HttpMethod.Post, "/document/v1/test/testdoc/group/mygroup/lid-1");
    }

    [Fact]
    public async Task PutByGroupAsync_EscapesGroupName()
    {
        SetupSuccessResponse();

        await _documentOps.PutByGroupAsync("my group", "lid-1",
            new TestDocument { Name = "hi" }, "testdoc");

        VerifyHttpCall(HttpMethod.Post, "/document/v1/test/testdoc/group/my%20group/lid-1");
    }

    [Fact]
    public async Task PutByNumberAsync_BuildsCorrectUrl()
    {
        SetupSuccessResponse();

        await _documentOps.PutByNumberAsync(99L, "lid-1",
            new TestDocument { Name = "hi" }, "testdoc");

        VerifyHttpCall(HttpMethod.Post, "/document/v1/test/testdoc/number/99/lid-1");
    }

    [Fact]
    public async Task UpdateFieldsByGroupAsync_WithCreateIfMissing_IncludesCreateParam()
    {
        SetupSuccessResponse();

        await _documentOps.UpdateFieldsByGroupAsync("g1", "lid-1",
            new Dictionary<string, FieldOperation> { ["quantity"] = FieldOp.Increment(1) },
            "testdoc", createIfMissing: true);

        _mockHandler.Protected().Verify(
            "SendAsync", Times.Once(),
            ItExpr.Is<HttpRequestMessage>(req =>
                req.Method == HttpMethod.Put &&
                req.RequestUri!.AbsolutePath == "/document/v1/test/testdoc/group/g1/lid-1" &&
                req.RequestUri!.Query.Contains("create=true")),
            ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task UpdateFieldsByGroupAsync_SendsFieldOps()
    {
        SetupSuccessResponse();

        await _documentOps.UpdateFieldsByGroupAsync("g1", "lid-1",
            new Dictionary<string, FieldOperation> { ["count"] = FieldOp.Increment(1) },
            "testdoc");

        VerifyHttpCall(HttpMethod.Put, "/document/v1/test/testdoc/group/g1/lid-1");
    }

    [Fact]
    public async Task UpdateFieldsByNumberAsync_SendsFieldOps()
    {
        SetupSuccessResponse();

        await _documentOps.UpdateFieldsByNumberAsync(7L, "lid-1",
            new Dictionary<string, FieldOperation> { ["count"] = FieldOp.Increment(1) },
            "testdoc");

        VerifyHttpCall(HttpMethod.Put, "/document/v1/test/testdoc/number/7/lid-1");
    }

    [Fact]
    public async Task DeleteByGroupAsync_UsesDelete_WithCondition()
    {
        SetupSuccessResponse();

        await _documentOps.DeleteByGroupAsync("g1", "lid-1", "testdoc", condition: "testdoc.deleted==false");

        _mockHandler.Protected().Verify(
            "SendAsync", Times.Once(),
            ItExpr.Is<HttpRequestMessage>(req =>
                req.Method == HttpMethod.Delete &&
                req.RequestUri!.AbsolutePath == "/document/v1/test/testdoc/group/g1/lid-1" &&
                req.RequestUri!.Query.Contains("condition=")),
            ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task DeleteByNumberAsync_UsesDelete()
    {
        SetupSuccessResponse();

        await _documentOps.DeleteByNumberAsync(3L, "lid-1", "testdoc");

        VerifyHttpCall(HttpMethod.Delete, "/document/v1/test/testdoc/number/3/lid-1");
    }

    [Fact]
    public async Task PutByGroupAsync_WithTimeoutOption_PropagatesTimeout()
    {
        string? capturedUrl = null;
        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => capturedUrl = req.RequestUri?.ToString())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });

        await _documentOps.PutByGroupAsync("g", "lid-1",
            new TestDocument { Name = "hi" }, "testdoc",
            requestOptions: new DocumentRequestOptions { Timeout = TimeSpan.FromSeconds(4) });

        Assert.NotNull(capturedUrl);
        Assert.Contains("timeout=4000ms", capturedUrl);
    }

    [Fact]
    public async Task GetByGroupAsync_WithRequestOptions_PropagatesFieldSet()
    {
        string? capturedUrl = null;
        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => capturedUrl = req.RequestUri?.ToString())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"id":"id:test:testdoc::lid-1","fields":{}}""")
            });

        await _documentOps.GetByGroupAsync<TestDocument>("g", "lid-1", "testdoc",
            fieldSet: "[document]",
            requestOptions: new DocumentRequestOptions { TraceLevel = 3 });

        Assert.NotNull(capturedUrl);
        Assert.Contains("fieldSet=", capturedUrl);
        Assert.Contains("tracelevel=3", capturedUrl);
    }

    #endregion

    #region GetManyAsync Tests

    [Fact]
    public async Task GetManyAsync_EmptyIds_ReturnsEmptyList()
    {
        var result = await _documentOps.GetManyAsync<TestDocument>([], "testdoc");

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetManyAsync_SingleId_ReturnsSingleDocument()
    {
        SetupGetSuccessResponse(new VespaDocument<TestDocument>
        {
            Id = "id:test:testdoc::doc-1",
            Fields = new TestDocument { Name = "Test", Value = 1 }
        });

        var result = await _documentOps.GetManyAsync<TestDocument>(["doc-1"], "testdoc");

        Assert.Single(result);
        Assert.Equal("Test", result[0].Fields!.Name);
    }

    [Fact]
    public async Task GetManyAsync_MultipleIds_ReturnsAllDocuments()
    {
        var callCount = 0;
        _mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(() =>
            {
                var idx = Interlocked.Increment(ref callCount);
                var doc = new VespaDocument<TestDocument>
                {
                    Id = $"id:test:testdoc::doc-{idx}",
                    Fields = new TestDocument { Name = $"Doc{idx}", Value = idx }
                };
                var json = JsonSerializer.Serialize(doc, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json)
                };
            });

        var result = await _documentOps.GetManyAsync<TestDocument>(
            ["doc-1", "doc-2", "doc-3"], "testdoc");

        Assert.Equal(3, result.Count);
    }

    [Fact]
    public async Task GetManyAsync_MixedResults_FiltersOutNulls()
    {
        var callCount = 0;
        _mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(() =>
            {
                var idx = Interlocked.Increment(ref callCount);
                if (idx == 2)
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                var doc = new VespaDocument<TestDocument>
                {
                    Id = $"id:test:testdoc::doc-{idx}",
                    Fields = new TestDocument { Name = $"Doc{idx}", Value = idx }
                };
                var json = JsonSerializer.Serialize(doc, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json)
                };
            });

        var result = await _documentOps.GetManyAsync<TestDocument>(
            ["doc-1", "doc-2", "doc-3"], "testdoc");

        Assert.Equal(2, result.Count);
    }

    #endregion

    #region Helper Methods

    private void SetupSuccessResponse()
    {
        _mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}")
            });
    }

    private void SetupGetSuccessResponse<T>(VespaDocument<T> document) where T : class
    {
        var json = JsonSerializer.Serialize(document, new JsonSerializerOptions
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

    private void SetupNotFoundResponse()
    {
        _mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private void SetupErrorResponse(HttpStatusCode statusCode, string message)
    {
        var error = new VespaError
        {
            Code = (int)statusCode,
            Message = message
        };

        var json = JsonSerializer.Serialize(error, new JsonSerializerOptions
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
            .ReturnsAsync(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(json)
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

    #endregion

    #region Test Models

    private class TestDocument
    {
        public string Name { get; set; } = string.Empty;
        public int Value { get; set; }
    }

    private class TestDocumentWithId
    {
        [VespaId]
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
    }

    #endregion

    #region VespaId Injection Tests

    [Fact]
    public async Task GetAsync_InjectsIdIntoVespaIdProperty()
    {
        // Arrange — simulate a real Vespa response: ID at wrapper level, not inside fields
        var json = """{"id":"id:test:testdoc::my-doc","fields":{"name":"Test"}}""";
        SetupRawJsonResponse(json);

        // Act
        var result = await _documentOps.GetAsync<TestDocumentWithId>("my-doc", "testdoc");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("my-doc", result.Id);
        Assert.Equal("my-doc", result.Fields!.Id);
        Assert.Equal("Test", result.Fields.Name);
    }

    [Fact]
    public async Task GetAsync_WithoutVespaIdProperty_WorksNormally()
    {
        // Arrange
        var json = """{"id":"id:test:testdoc::doc-1","fields":{"name":"Test","value":42}}""";
        SetupRawJsonResponse(json);

        // Act
        var result = await _documentOps.GetAsync<TestDocument>("doc-1", "testdoc");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("doc-1", result.Id);
        Assert.Equal("Test", result.Fields!.Name);
    }

    [Fact]
    public async Task VisitAsync_InjectsIdIntoVespaIdProperty()
    {
        // Arrange — simulate a visit response with two documents
        var json = JsonSerializer.Serialize(new
        {
            documents = new[]
            {
                new { id = "id:test:testdoc::doc-1", fields = new { name = "First" } },
                new { id = "id:test:testdoc::doc-2", fields = new { name = "Second" } }
            },
            documentCount = 2
        });
        SetupRawJsonResponse(json);

        // Act
        var results = new List<VespaDocument<TestDocumentWithId>>();
        await foreach (var doc in _documentOps.VisitAsync<TestDocumentWithId>("testdoc"))
            results.Add(doc);

        // Assert
        Assert.Equal(2, results.Count);
        Assert.Equal("doc-1", results[0].Fields!.Id);
        Assert.Equal("doc-2", results[1].Fields!.Id);
    }

    private void SetupRawJsonResponse(string json)
    {
        _mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            });
    }

    #endregion
}
