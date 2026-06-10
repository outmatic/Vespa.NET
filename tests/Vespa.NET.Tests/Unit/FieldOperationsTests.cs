using System.Net;
using System.Text.Json;
using Moq;
using Moq.Protected;
using Vespa;
using Vespa.Documents;
using Vespa.Models;
using Xunit;

namespace Vespa.Tests.Unit;

/// <summary>
/// Tests for field-level update operations and conditional write parameters
/// </summary>
public class FieldOperationsTests : IDisposable
{
    private readonly Mock<HttpMessageHandler> _mockHandler;
    private readonly HttpClient _httpClient;
    private readonly VespaClientOptions _options;
    private readonly DocumentOperations _documentOps;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public FieldOperationsTests()
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

    public void Dispose() => _httpClient.Dispose();

    // --- FieldOp factory tests ---

    [Fact]
    public void FieldOp_Assign_HasCorrectType()
    {
        var op = FieldOp.Assign("hello");
        Assert.Equal("assign", op.Type);
        Assert.Equal("hello", op.Value);
    }

    [Fact]
    public void FieldOp_Increment_DefaultDelta()
    {
        var op = FieldOp.Increment();
        Assert.Equal("increment", op.Type);
        Assert.Equal(1.0, op.Value);
    }

    [Fact]
    public void FieldOp_Increment_CustomDelta()
    {
        var op = FieldOp.Increment(5.5);
        Assert.Equal("increment", op.Type);
        Assert.Equal(5.5, op.Value);
    }

    [Fact]
    public void FieldOp_Decrement_DefaultDelta()
    {
        var op = FieldOp.Decrement();
        Assert.Equal("decrement", op.Type);
        Assert.Equal(1.0, op.Value);
    }

    [Fact]
    public void FieldOp_Multiply_HasCorrectType()
    {
        var op = FieldOp.Multiply(2.0);
        Assert.Equal("multiply", op.Type);
        Assert.Equal(2.0, op.Value);
    }

    [Fact]
    public void FieldOp_Divide_HasCorrectType()
    {
        var op = FieldOp.Divide(3.0);
        Assert.Equal("divide", op.Type);
        Assert.Equal(3.0, op.Value);
    }

    [Fact]
    public void FieldOp_Add_HasCorrectType()
    {
        var op = FieldOp.Add("rock");
        Assert.Equal("add", op.Type);
        Assert.Equal("rock", op.Value);
    }

    [Fact]
    public void FieldOp_Remove_HasCorrectType()
    {
        var op = FieldOp.Remove("rock");
        Assert.Equal("remove", op.Type);
        Assert.Equal("rock", op.Value);
    }

    [Fact]
    public void FieldOp_Match_HasCorrectType()
    {
        var inner = FieldOp.Increment();
        var op = FieldOp.Match("key1", inner);
        Assert.Equal("match", op.Type);
    }

    // --- FieldOperation JSON serialization ---

    [Fact]
    public void FieldOperation_Serializes_AsVespaFormat()
    {
        var op = FieldOp.Increment();
        var json = JsonSerializer.Serialize(op, JsonOpts);
        Assert.Equal("""{"increment":1}""", json);
    }

    [Fact]
    public void FieldOperation_Assign_SerializesString()
    {
        var op = FieldOp.Assign("hello");
        var json = JsonSerializer.Serialize(op, JsonOpts);
        Assert.Equal("""{"assign":"hello"}""", json);
    }

    [Fact]
    public void FieldOperation_Match_SerializesElementInsideMatch()
    {
        // Vespa document JSON format: {"match":{"element":"...","increment":1}}
        var op = FieldOp.Match("Lay Lady Lay", FieldOp.Increment(1));
        var json = JsonSerializer.Serialize(op, JsonOpts);
        Assert.Equal("""{"match":{"element":"Lay Lady Lay","increment":1}}""", json);
    }

    [Fact]
    public void FieldOperation_Match_WithIntegerElement_SerializesArrayIndex()
    {
        var op = FieldOp.Match(2, FieldOp.Assign("new value"));
        var json = JsonSerializer.Serialize(op, JsonOpts);
        Assert.Equal("""{"match":{"element":2,"assign":"new value"}}""", json);
    }

    // --- UpdateFieldsAsync HTTP tests ---

    [Fact]
    public async Task UpdateFieldsAsync_SendsPutRequest_ToCorrectUrl()
    {
        SetupSuccessResponse();
        var ops = new Dictionary<string, FieldOperation>
        {
            ["year"] = FieldOp.Increment()
        };

        await _documentOps.UpdateFieldsAsync("doc-1", ops, "music");

        VerifyHttpCall(HttpMethod.Put, "/document/v1/test/music/docid/doc-1");
    }

    [Fact]
    public async Task UpdateFieldsAsync_WithCreateIfMissing_AddsQueryParam()
    {
        SetupSuccessResponse();
        var ops = new Dictionary<string, FieldOperation> { ["x"] = FieldOp.Assign(1) };

        await _documentOps.UpdateFieldsAsync("doc-1", ops, "music", createIfMissing: true);

        VerifyHttpCall(HttpMethod.Put, "/document/v1/test/music/docid/doc-1?create=true");
    }

    [Fact]
    public async Task UpdateFieldsAsync_WithCondition_AddsQueryParam()
    {
        SetupSuccessResponse();
        var ops = new Dictionary<string, FieldOperation> { ["x"] = FieldOp.Assign(1) };

        await _documentOps.UpdateFieldsAsync("doc-1", ops, "music", condition: "music.year > 2000");

        VerifyHttpCall(HttpMethod.Put, "/document/v1/test/music/docid/doc-1?condition=music.year%20%3E%202000");
    }

    [Fact]
    public async Task UpdateFieldsAsync_WithBothCreateAndCondition_AddsAllQueryParams()
    {
        SetupSuccessResponse();
        var ops = new Dictionary<string, FieldOperation> { ["x"] = FieldOp.Assign(1) };

        await _documentOps.UpdateFieldsAsync("doc-1", ops, "music",
            createIfMissing: true, condition: "music.year > 2000");

        VerifyHttpCall(HttpMethod.Put, "/document/v1/test/music/docid/doc-1?create=true&condition=music.year%20%3E%202000");
    }

    [Fact]
    public async Task UpdateFieldsAsync_NullFieldOperations_ThrowsArgumentNullException()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _documentOps.UpdateFieldsAsync("doc-1", null!, "music"));
    }

    // --- Conditional writes on existing methods ---

    [Fact]
    public async Task PutAsync_WithCondition_AppendsQueryParam()
    {
        SetupSuccessResponse();
        var doc = new { name = "Test" };

        await _documentOps.PutAsync("doc-1", doc, "testdoc", condition: "testdoc.name == \"old\"");

        VerifyHttpCall(HttpMethod.Post, "/document/v1/test/testdoc/docid/doc-1?condition=testdoc.name%20%3D%3D%20%22old%22");
    }

    [Fact]
    public async Task PutAsync_WithoutCondition_NoQueryParams()
    {
        SetupSuccessResponse();
        var doc = new { name = "Test" };

        await _documentOps.PutAsync("doc-1", doc, "testdoc");

        VerifyHttpCall(HttpMethod.Post, "/document/v1/test/testdoc/docid/doc-1");
    }

    [Fact]
    public async Task DeleteAsync_WithCondition_AppendsQueryParam()
    {
        SetupSuccessResponse();

        await _documentOps.DeleteAsync("doc-1", "testdoc", condition: "testdoc.version == 1");

        VerifyHttpCall(HttpMethod.Delete, "/document/v1/test/testdoc/docid/doc-1?condition=testdoc.version%20%3D%3D%201");
    }

    [Fact]
    public async Task UpdateFieldsAsync_WithCondition_AppendsQueryParam()
    {
        SetupSuccessResponse();
        var ops = new Dictionary<string, FieldOperation> { ["name"] = FieldOp.Assign("Updated") };

        await _documentOps.UpdateFieldsAsync("doc-1", ops, "testdoc", condition: "testdoc.version == 1");

        VerifyHttpCall(HttpMethod.Put, "/document/v1/test/testdoc/docid/doc-1?condition=testdoc.version%20%3D%3D%201");
    }

    [Fact]
    public async Task UpdateFieldsAsync_WithCreateIfMissingAndCondition_AddsAllParams()
    {
        SetupSuccessResponse();
        var ops = new Dictionary<string, FieldOperation> { ["name"] = FieldOp.Assign("Updated") };

        await _documentOps.UpdateFieldsAsync("doc-1", ops, "testdoc",
            createIfMissing: true, condition: "testdoc.version == 1");

        VerifyHttpCall(HttpMethod.Put, "/document/v1/test/testdoc/docid/doc-1?create=true&condition=testdoc.version%20%3D%3D%201");
    }

    // --- HTTP 412 PreconditionFailed ---

    [Fact]
    public async Task PutAsync_Returns412_ThrowsVespaExceptionWithConditionMessage()
    {
        Setup412Response();

        var ex = await Assert.ThrowsAsync<VespaConditionNotMetException>(() =>
            _documentOps.PutAsync("doc-1", new { }, "testdoc", condition: "testdoc.x == 1"));

        Assert.Equal(412, ex.StatusCode);
        Assert.Contains("condition not met", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeleteAsync_Returns412_ThrowsVespaExceptionWithConditionMessage()
    {
        Setup412Response();

        var ex = await Assert.ThrowsAsync<VespaConditionNotMetException>(() =>
            _documentOps.DeleteAsync("doc-1", "testdoc", condition: "testdoc.x == 1"));

        Assert.Equal(412, ex.StatusCode);
        Assert.Contains("condition not met", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // --- ClearField ---

    [Fact]
    public void FieldOp_ClearField_HasAssignType()
    {
        var op = FieldOp.ClearField();
        Assert.Equal("assign", op.Type);
    }

    [Fact]
    public void FieldOp_ClearField_SerializesAsNull()
    {
        var op = FieldOp.ClearField();
        var json = JsonSerializer.Serialize(op, JsonOpts);
        Assert.Equal("""{"assign":null}""", json);
    }

    // --- Modify (tensor cell-level) ---

    [Fact]
    public void FieldOp_Modify_HasModifyType()
    {
        var cells = new List<TensorCellUpdate>
        {
            new(new() { ["x"] = "0" }, 5.0)
        };
        var op = FieldOp.Modify("replace", cells);
        Assert.Equal("modify", op.Type);
    }

    [Fact]
    public void FieldOp_Modify_Replace_SerializesCorrectly()
    {
        var cells = new List<TensorCellUpdate>
        {
            new(new() { ["x"] = "0", ["y"] = "1" }, 3.14)
        };
        var op = FieldOp.Modify("replace", cells);
        var json = JsonSerializer.Serialize(op, JsonOpts);
        var expected = """{"modify":{"operation":"replace","cells":[{"address":{"x":"0","y":"1"},"value":3.14}]}}""";
        Assert.Equal(expected, json);
    }

    [Fact]
    public void FieldOp_Modify_Add_MultipleCells_SerializesCorrectly()
    {
        var cells = new List<TensorCellUpdate>
        {
            new(new() { ["x"] = "a" }, 1.0),
            new(new() { ["x"] = "b" }, 2.0)
        };
        var op = FieldOp.Modify("add", cells);
        var json = JsonSerializer.Serialize(op, JsonOpts);
        var expected = """{"modify":{"operation":"add","cells":[{"address":{"x":"a"},"value":1},{"address":{"x":"b"},"value":2}]}}""";
        Assert.Equal(expected, json);
    }

    [Fact]
    public void FieldOp_Modify_Multiply_SerializesCorrectly()
    {
        var cells = new List<TensorCellUpdate>
        {
            new(new() { ["x"] = "0" }, 2.5)
        };
        var op = FieldOp.Modify("multiply", cells);
        var json = JsonSerializer.Serialize(op, JsonOpts);
        Assert.Contains("\"operation\":\"multiply\"", json);
        Assert.Contains("\"value\":2.5", json);
    }

    // --- ClearField in UpdateFieldsAsync ---

    [Fact]
    public async Task UpdateFieldsAsync_WithClearField_SendsAssignNull()
    {
        SetupSuccessResponse();
        var ops = new Dictionary<string, FieldOperation>
        {
            ["title"] = FieldOp.ClearField()
        };

        await _documentOps.UpdateFieldsAsync("doc-1", ops, "music");

        VerifyHttpCall(HttpMethod.Put, "/document/v1/test/music/docid/doc-1");
    }

    // --- Modify in UpdateFieldsAsync ---

    [Fact]
    public async Task UpdateFieldsAsync_WithModify_SendsPutRequest()
    {
        SetupSuccessResponse();
        var cells = new List<TensorCellUpdate>
        {
            new(new() { ["x"] = "0" }, 5.0)
        };
        var ops = new Dictionary<string, FieldOperation>
        {
            ["embedding"] = FieldOp.Modify("replace", cells)
        };

        await _documentOps.UpdateFieldsAsync("doc-1", ops, "music");

        VerifyHttpCall(HttpMethod.Put, "/document/v1/test/music/docid/doc-1");
    }

    // --- fieldSet on GetAsync ---

    [Fact]
    public async Task GetAsync_WithFieldSet_AppendsQueryParam()
    {
        SetupGetSuccessResponse();

        await _documentOps.GetAsync<TestDoc>("doc-1", "testdoc", fieldSet: "testdoc:[document]");

        VerifyHttpCall(HttpMethod.Get, "/document/v1/test/testdoc/docid/doc-1?fieldSet=testdoc%3A%5Bdocument%5D");
    }

    [Fact]
    public async Task GetAsync_WithoutFieldSet_NoQueryParams()
    {
        SetupGetSuccessResponse();

        await _documentOps.GetAsync<TestDoc>("doc-1", "testdoc");

        VerifyHttpCall(HttpMethod.Get, "/document/v1/test/testdoc/docid/doc-1");
    }

    // --- M14: Fieldpath syntax ---

    [Fact]
    public void FieldPath_Struct_ProducesCorrectPath()
        => Assert.Equal("address.city", FieldPath.Struct("address", "city"));

    [Fact]
    public void FieldPath_Map_ProducesCorrectPath()
        => Assert.Equal("tags{mykey}", FieldPath.Map("tags", "mykey"));

    [Fact]
    public void FieldPath_Array_ProducesCorrectPath()
        => Assert.Equal("items[0]", FieldPath.Array("items", 0));

    [Fact]
    public void FieldPath_Combine_ProducesCorrectPath()
        => Assert.Equal("address.lines.0", FieldPath.Combine("address", "lines", "0"));

    [Fact]
    public async Task UpdateFieldsAsync_WithFieldpathKey_SendsCorrectly()
    {
        SetupSuccessResponse();
        var ops = new Dictionary<string, FieldOperation>
        {
            [FieldPath.Struct("address", "city")] = FieldOp.Assign("NYC")
        };

        await _documentOps.UpdateFieldsAsync("doc-1", ops, "music");

        VerifyHttpCall(HttpMethod.Put, "/document/v1/test/music/docid/doc-1");
    }

    // --- M14: Update by selection ---

    [Fact]
    public async Task UpdateBySelectionAsync_SendsPutWithSelectionParam()
    {
        SetupSuccessResponse();
        var ops = new Dictionary<string, FieldOperation>
        {
            ["status"] = FieldOp.Assign("archived")
        };

        await _documentOps.UpdateBySelectionAsync("music.year < 2000", ops, "music");

        _mockHandler.Protected().Verify(
            "SendAsync", Times.Once(),
            ItExpr.Is<HttpRequestMessage>(req =>
                req.Method == HttpMethod.Put &&
                req.RequestUri!.PathAndQuery.Contains("selection=music.year")),
            ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task UpdateBySelectionAsync_WithCluster_IncludesClusterParam()
    {
        SetupSuccessResponse();
        var ops = new Dictionary<string, FieldOperation>
        {
            ["status"] = FieldOp.Assign("archived")
        };

        await _documentOps.UpdateBySelectionAsync("music.year < 2000", ops, "music", cluster: "content");

        _mockHandler.Protected().Verify(
            "SendAsync", Times.Once(),
            ItExpr.Is<HttpRequestMessage>(req =>
                req.Method == HttpMethod.Put &&
                req.RequestUri!.PathAndQuery.Contains("cluster=content")),
            ItExpr.IsAny<CancellationToken>());
    }

    // --- M14: Delete by selection ---

    [Fact]
    public async Task DeleteBySelectionAsync_SendsDeleteWithSelectionParam()
    {
        SetupSuccessResponse();

        await _documentOps.DeleteBySelectionAsync("music.year < 1980", "music");

        _mockHandler.Protected().Verify(
            "SendAsync", Times.Once(),
            ItExpr.Is<HttpRequestMessage>(req =>
                req.Method == HttpMethod.Delete &&
                req.RequestUri!.PathAndQuery.Contains("selection=music.year")),
            ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task DeleteBySelectionAsync_WithCluster_IncludesClusterParam()
    {
        SetupSuccessResponse();

        await _documentOps.DeleteBySelectionAsync("music.year < 1980", "music", cluster: "content");

        _mockHandler.Protected().Verify(
            "SendAsync", Times.Once(),
            ItExpr.Is<HttpRequestMessage>(req =>
                req.Method == HttpMethod.Delete &&
                req.RequestUri!.PathAndQuery.Contains("cluster=content")),
            ItExpr.IsAny<CancellationToken>());
    }

    // --- M14: Selection ops — continuation loop ---

    [Fact]
    public async Task UpdateBySelectionAsync_WhenContinuationReturned_LoopsUntilDone()
    {
        var responses = new Queue<HttpResponseMessage>(
        [
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"documentCount": 100, "continuation": "abc"}""")
            },
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"documentCount": 50, "continuation": "def"}""")
            },
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"documentCount": 25}""")
            }
        ]);

        _mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(responses.Dequeue);

        var result = await _documentOps.UpdateBySelectionAsync(
            "music.year < 2000",
            new Dictionary<string, FieldOperation> { ["status"] = FieldOp.Assign("archived") },
            "music", cluster: "content");

        Assert.True(result.IsSuccess);
        Assert.Equal(175, result.DocumentCount);
        _mockHandler.Protected().Verify(
            "SendAsync", Times.Exactly(3),
            ItExpr.IsAny<HttpRequestMessage>(),
            ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task UpdateBySelectionAsync_WithContinuation_SendsReadableBodyOnEveryChunk()
    {
        // A real HTTP handler reads the request body on every chunk — a content
        // instance reused across chunks is disposed with the first request.
        var handler = new BodyReadingHandler(new Queue<HttpResponseMessage>(
        [
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"documentCount": 100, "continuation": "abc"}""")
            },
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"documentCount": 50}""")
            }
        ]));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8080") };
        var documentOps = new DocumentOperations(httpClient, _options);

        var result = await documentOps.UpdateBySelectionAsync(
            "music.year < 2000",
            new Dictionary<string, FieldOperation> { ["status"] = FieldOp.Assign("archived") },
            "music", cluster: "content");

        Assert.True(result.IsSuccess);
        Assert.Equal(150, result.DocumentCount);
        Assert.Equal(2, handler.Bodies.Count);
        Assert.All(handler.Bodies, b => Assert.Contains(""""assign":"archived"""", b));
    }

    private sealed class BodyReadingHandler(Queue<HttpResponseMessage> responses) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return responses.Dequeue();
        }
    }

    [Fact]
    public async Task UpdateBySelectionAsync_PassesContinuationTokenOnSubsequentCalls()
    {
        var capturedUrls = new List<string>();
        var responses = new Queue<HttpResponseMessage>(
        [
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"documentCount": 10, "continuation": "token-xyz"}""")
            },
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"documentCount": 5}""")
            }
        ]);

        _mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => capturedUrls.Add(req.RequestUri!.PathAndQuery))
            .ReturnsAsync(responses.Dequeue);

        await _documentOps.DeleteBySelectionAsync("music.year < 1980", "music", cluster: "content");

        Assert.Equal(2, capturedUrls.Count);
        Assert.DoesNotContain("continuation=", capturedUrls[0]);
        Assert.Contains("continuation=token-xyz", capturedUrls[1]);
    }

    // --- CopyBySelectionAsync ---

    [Fact]
    public async Task CopyBySelectionAsync_SendsPostWithAllRequiredParams()
    {
        string? capturedUrl = null;
        _mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => capturedUrl = req.RequestUri!.PathAndQuery)
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"documentCount": 42}""")
            });

        var result = await _documentOps.CopyBySelectionAsync(
            selection: "music.year < 2000",
            documentType: "music",
            cluster: "source-cluster",
            destinationCluster: "archive-cluster");

        Assert.True(result.IsSuccess);
        Assert.Equal(42, result.DocumentCount);
        Assert.NotNull(capturedUrl);
        Assert.Contains("selection=music.year", capturedUrl);
        Assert.Contains("cluster=source-cluster", capturedUrl);
        Assert.Contains("destinationCluster=archive-cluster", capturedUrl);

        _mockHandler.Protected().Verify(
            "SendAsync", Times.Once(),
            ItExpr.Is<HttpRequestMessage>(req => req.Method == HttpMethod.Post),
            ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task CopyBySelectionAsync_LoopsOnContinuation()
    {
        var responses = new Queue<HttpResponseMessage>(
        [
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"documentCount": 1000, "continuation": "c1"}""")
            },
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"documentCount": 500}""")
            }
        ]);

        _mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(responses.Dequeue);

        var result = await _documentOps.CopyBySelectionAsync(
            "music.year < 2000", "music", cluster: "a", destinationCluster: "b");

        Assert.Equal(1500, result.DocumentCount);
    }

    [Fact]
    public async Task CopyBySelectionAsync_EmptyCluster_ThrowsArgumentException()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _documentOps.CopyBySelectionAsync("music.year < 2000", "music",
                cluster: "", destinationCluster: "b"));
    }

    [Fact]
    public async Task CopyBySelectionAsync_EmptyDestinationCluster_ThrowsArgumentException()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _documentOps.CopyBySelectionAsync("music.year < 2000", "music",
                cluster: "a", destinationCluster: ""));
    }

    // --- SelectionRequestOptions ---

    [Fact]
    public void SelectionRequestOptions_ToQueryParams_EmitsAllSetValues()
    {
        var opts = new SelectionRequestOptions
        {
            TimeChunk = TimeSpan.FromSeconds(30),
            BucketSpace = "global",
            Timeout = TimeSpan.FromSeconds(5),
            TraceLevel = 4
        };

        var ps = opts.ToQueryParams().ToList();

        Assert.Contains(("timeChunk", "30000ms"), ps);
        Assert.Contains(("bucketSpace", "global"), ps);
        Assert.Contains(("timeout", "5000ms"), ps);
        Assert.Contains(("tracelevel", "4"), ps);
    }

    [Fact]
    public void SelectionRequestOptions_Default_EmitsNothing()
    {
        Assert.Empty(new SelectionRequestOptions().ToQueryParams());
    }

    [Fact]
    public async Task UpdateBySelectionAsync_WithRequestOptions_PropagatesQueryParams()
    {
        string? capturedUrl = null;
        _mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => capturedUrl = req.RequestUri!.PathAndQuery)
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"documentCount": 0}""")
            });

        await _documentOps.UpdateBySelectionAsync(
            "music.year < 2000",
            new Dictionary<string, FieldOperation> { ["status"] = FieldOp.Assign("archived") },
            "music",
            cluster: "content",
            requestOptions: new SelectionRequestOptions
            {
                TimeChunk = TimeSpan.FromSeconds(10),
                BucketSpace = "default",
                TraceLevel = 2
            });

        Assert.NotNull(capturedUrl);
        Assert.Contains("timeChunk=10000ms", capturedUrl);
        Assert.Contains("bucketSpace=default", capturedUrl);
        Assert.Contains("tracelevel=2", capturedUrl);
    }

    [Fact]
    public async Task CopyBySelectionAsync_WithRequestOptions_PropagatesQueryParams()
    {
        string? capturedUrl = null;
        _mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => capturedUrl = req.RequestUri!.PathAndQuery)
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"documentCount": 0}""")
            });

        await _documentOps.CopyBySelectionAsync(
            "music.year < 2000", "music",
            cluster: "a", destinationCluster: "b",
            requestOptions: new SelectionRequestOptions { Timeout = TimeSpan.FromSeconds(15) });

        Assert.NotNull(capturedUrl);
        Assert.Contains("timeout=15000ms", capturedUrl);
        Assert.Contains("destinationCluster=b", capturedUrl);
    }

    // --- Selection ops — JSONL streaming (Stream=true) ---

    [Fact]
    public async Task UpdateBySelectionAsync_WithStream_RequestsJsonlAndSetsQueryParam()
    {
        HttpRequestMessage? captured = null;
        _mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => captured = req)
            .ReturnsAsync(() =>
            {
                var resp = new HttpResponseMessage(HttpStatusCode.OK);
                // Final continuation with no token → operation complete after one chunk.
                resp.Content = new StringContent(
                    """
                    {"continuation":{"percentFinished":100}}
                    {"sessionStats":{"documentCount":42}}
                    """);
                return resp;
            });

        var result = await _documentOps.UpdateBySelectionAsync(
            "music.year < 2000",
            new Dictionary<string, FieldOperation> { ["status"] = FieldOp.Assign("archived") },
            "music", cluster: "content",
            requestOptions: new SelectionRequestOptions { Stream = true });

        Assert.True(result.IsSuccess);
        Assert.Equal(42, result.DocumentCount);

        Assert.NotNull(captured);
        Assert.Contains("stream=true", captured.RequestUri!.Query);
        Assert.Equal("application/jsonl", captured.Headers.Accept.First().MediaType);
    }

    [Fact]
    public async Task DeleteBySelectionAsync_WithStream_LoopsWhenContinuationHasToken()
    {
        var responses = new Queue<HttpResponseMessage>(
        [
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {"continuation":{"token":"ABC","percentFinished":50}}
                    {"sessionStats":{"documentCount":70}}
                    """)
            },
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {"continuation":{"percentFinished":100}}
                    {"sessionStats":{"documentCount":30}}
                    """)
            }
        ]);

        _mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(responses.Dequeue);

        var result = await _documentOps.DeleteBySelectionAsync(
            "music.year < 1980", "music", cluster: "content",
            requestOptions: new SelectionRequestOptions { Stream = true });

        Assert.Equal(100, result.DocumentCount);
        _mockHandler.Protected().Verify(
            "SendAsync", Times.Exactly(2),
            ItExpr.IsAny<HttpRequestMessage>(),
            ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task CopyBySelectionAsync_WithStream_ThrowsArgumentException()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _documentOps.CopyBySelectionAsync(
                "music.year < 2000", "music",
                cluster: "a", destinationCluster: "b",
                requestOptions: new SelectionRequestOptions { Stream = true }));
    }

    [Fact]
    public async Task CopyBySelectionPageAsync_WithStream_ThrowsArgumentException()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _documentOps.CopyBySelectionPageAsync(
                "music.year < 2000", "music",
                cluster: "a", destinationCluster: "b",
                requestOptions: new SelectionRequestOptions { Stream = true }));
    }

    // --- Selection ops — manual pagination (PageAsync variants) ---

    [Fact]
    public async Task UpdateBySelectionPageAsync_ReturnsSingleChunk_DoesNotLoop()
    {
        _mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"documentCount": 100, "continuation": "next-token"}""")
            });

        var page = await _documentOps.UpdateBySelectionPageAsync(
            "music.year < 2000",
            new Dictionary<string, FieldOperation> { ["status"] = FieldOp.Assign("archived") },
            "music", cluster: "content");

        Assert.Equal(100, page.DocumentCount);
        Assert.Equal("next-token", page.Continuation);
        Assert.False(page.IsComplete);

        _mockHandler.Protected().Verify(
            "SendAsync", Times.Once(),
            ItExpr.IsAny<HttpRequestMessage>(),
            ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task DeleteBySelectionPageAsync_PassesContinuationToken()
    {
        string? capturedUrl = null;
        _mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => capturedUrl = req.RequestUri!.PathAndQuery)
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"documentCount": 50}""")
            });

        var page = await _documentOps.DeleteBySelectionPageAsync(
            "music.year < 1980", "music", cluster: "content",
            continuation: "abc123");

        Assert.Equal(50, page.DocumentCount);
        Assert.Null(page.Continuation);
        Assert.True(page.IsComplete);
        Assert.NotNull(capturedUrl);
        Assert.Contains("continuation=abc123", capturedUrl);
    }

    [Fact]
    public async Task CopyBySelectionPageAsync_ReturnsContinuationWhenIncomplete()
    {
        _mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"documentCount": 500, "continuation": "c-mid"}""")
            });

        var page = await _documentOps.CopyBySelectionPageAsync(
            "music.year < 2000", "music", cluster: "src", destinationCluster: "dst");

        Assert.Equal(500, page.DocumentCount);
        Assert.Equal("c-mid", page.Continuation);
        Assert.False(page.IsComplete);
    }

    [Fact]
    public async Task UpdateBySelectionPageAsync_ManualLoop_EquivalentToAutoLoop()
    {
        var responses = new Queue<HttpResponseMessage>(
        [
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"documentCount": 40, "continuation": "p1"}""")
            },
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"documentCount": 30, "continuation": "p2"}""")
            },
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"documentCount": 10}""")
            }
        ]);

        _mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(responses.Dequeue);

        long total = 0;
        string? continuation = null;
        do
        {
            var page = await _documentOps.UpdateBySelectionPageAsync(
                "music.year < 2000",
                new Dictionary<string, FieldOperation> { ["status"] = FieldOp.Assign("archived") },
                "music", cluster: "content",
                continuation: continuation);
            total += page.DocumentCount;
            continuation = page.Continuation;
        } while (continuation is not null);

        Assert.Equal(80, total);
    }

    // --- VisitJsonlAsync aligned parameters ---

    [Fact]
    public async Task VisitJsonlAsync_WithSlicesAndConcurrency_IncludesInUrl()
    {
        string? capturedUrl = null;
        _mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => capturedUrl = req.RequestUri!.PathAndQuery)
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("")
            });

        await foreach (var _ in _documentOps.VisitJsonlAsync<object>(
            "music",
            slices: 8,
            sliceId: 3,
            concurrency: 4,
            includeRemoves: true,
            bucketSpace: "global",
            timeout: TimeSpan.FromSeconds(20)))
        { }

        Assert.NotNull(capturedUrl);
        Assert.Contains("slices=8", capturedUrl);
        Assert.Contains("sliceId=3", capturedUrl);
        Assert.Contains("concurrency=4", capturedUrl);
        Assert.Contains("includeRemoves=true", capturedUrl);
        Assert.Contains("bucketSpace=global", capturedUrl);
        Assert.Contains("timeout=20000ms", capturedUrl);
        Assert.Contains("stream=true", capturedUrl);
    }

    // --- Helpers ---

    private void SetupSuccessResponse()
    {
        _mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
    }

    private void Setup412Response()
    {
        _mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.PreconditionFailed) { Content = new StringContent("{}") });
    }

    private void SetupGetSuccessResponse()
    {
        var doc = new VespaDocument<TestDoc> { Id = "id:test:testdoc::doc-1", Fields = new TestDoc { Name = "Test" } };
        var json = JsonSerializer.Serialize(doc, JsonOpts);
        _mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
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
                    req.RequestUri!.PathAndQuery == expectedPath),
                ItExpr.IsAny<CancellationToken>());
    }

    private class TestDoc
    {
        public string Name { get; set; } = string.Empty;
    }
}
