using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Vespa.Models;
using Vespa.Models.Tensors;
using Vespa.Tests.Helpers;
using Xunit;

namespace Vespa.Tests.Unit;

public sealed class GeneratedJsonOperationsTests
{
    [Fact]
    public async Task TensorRegistrationWithoutConverterFailsBeforeSendingAnyRequest()
    {
        using var handler = new MockHttpMessageHandler();
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            using var client = new VespaClient(http, new VespaClientOptions
            {
                Endpoint = "http://vespa.test",
                JsonModels = new VespaJsonModels().With(TestModelJson.Default.GeneratedTensorWithoutConverter)
            });
            await client.Documents.GetAsync<GeneratedTensorWithoutConverter>("one", "product");
        });

        Assert.Contains(nameof(GeneratedTensorWithoutConverter), error.Message);
        Assert.Contains("embedding", error.Message);
        Assert.Contains(nameof(VespaTensorConverter), error.Message);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RegisteredTensorConverterPreservesCellsOnDocumentReadsAndWrites(bool useGlobalConverter)
    {
        if (useGlobalConverter)
        {
            var options = new JsonSerializerOptions();
            options.Converters.Add(new VespaTensorConverter());
            var context = new TestModelJson(options);
            await CheckTensorRoundTrip(new VespaJsonModels().With(context.GeneratedTensorWithoutConverter),
                new GeneratedTensorWithoutConverter(DenseTensor()), model => model.Embedding);
        }
        else
        {
            await CheckTensorRoundTrip(new VespaJsonModels().With(TestModelJson.Default.GeneratedTensorWithConverter),
                new GeneratedTensorWithConverter(DenseTensor()), model => model.Embedding);
        }
    }

    [Fact]
    public async Task NestedTensorRegistrationWithoutConverterFailsBeforeSendingAnyRequest()
    {
        using var handler = new MockHttpMessageHandler();
        handler.QueueResponse(Json("{}"));
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            using var client = new VespaClient(http, new VespaClientOptions
            {
                Endpoint = "http://vespa.test",
                JsonModels = new VespaJsonModels().With(TestModelJson.Default.GeneratedNestedTensorWithoutConverter)
            });
            await client.Documents.PutAsync("one",
                new GeneratedNestedTensorWithoutConverter(new GeneratedTensorWithoutConverter(DenseTensor())), "product");
        });

        Assert.Contains(nameof(GeneratedNestedTensorWithoutConverter), error.Message);
        Assert.Contains("inner.embedding", error.Message);
        Assert.Contains(nameof(VespaTensorConverter), error.Message);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RegisteredNestedTensorConverterPreservesCellsOnDocumentReadsAndWrites(bool useGlobalConverter)
    {
        if (useGlobalConverter)
        {
            var options = new JsonSerializerOptions();
            options.Converters.Add(new VespaTensorConverter());
            var context = new TestModelJson(options);
            await CheckTensorRoundTrip(new VespaJsonModels().With(context.GeneratedNestedTensorWithoutConverter),
                new GeneratedNestedTensorWithoutConverter(new GeneratedTensorWithoutConverter(DenseTensor())),
                model => model.Inner.Embedding, nested: true);
        }
        else
        {
            await CheckTensorRoundTrip(new VespaJsonModels().With(TestModelJson.Default.GeneratedNestedTensorWithConverter),
                new GeneratedNestedTensorWithConverter(new GeneratedTensorWithConverter(DenseTensor())),
                model => model.Inner.Embedding, nested: true);
        }
    }

    [Fact]
    public void RegistrationWalksTensorCollectionsAndDictionaryValues()
    {
        var models = new VespaJsonModels();
        var collectionError = Assert.Throws<InvalidOperationException>(() =>
            models.With(TestModelJson.Default.GeneratedNestedTensorCollection));
        Assert.Contains("items[].embedding", collectionError.Message);

        var dictionaryError = Assert.Throws<InvalidOperationException>(() =>
            models.With(TestModelJson.Default.GeneratedTensorDictionary));
        Assert.Contains("tensors[]", dictionaryError.Message);
    }

    [Fact]
    public void SelfReferencingModelCanBeRegistered()
    {
        var metadata = TestModelJson.Default.GeneratedRecursiveNode;
        var models = new VespaJsonModels().With(metadata);

        Assert.Same(metadata, VespaJsonModels.Resolve<GeneratedRecursiveNode>(models, "document read")!.TypeInfo);
    }

    [Fact]
    public void RegistrationLeavesObjectAndJsonElementPayloadsUnchecked()
    {
        var metadata = TestModelJson.Default.GeneratedRuntimePayload;
        var models = new VespaJsonModels().With(metadata);

        Assert.Same(metadata, VespaJsonModels.Resolve<GeneratedRuntimePayload>(models, "document read")!.TypeInfo);
    }

    [Fact]
    public async Task UnregisteredTensorModelStillUsesTheDefaultConverter()
    {
        await CheckTensorRoundTrip(null, new GeneratedTensorWithoutConverter(DenseTensor()), model => model.Embedding);
    }

    private static VespaTensor DenseTensor() =>
        VespaTensor.FromDenseValues<float>([1.5f, 2.5f, 3.5f], "tensor<float>(x[3])");

    private static async Task CheckTensorRoundTrip<T>(
        VespaJsonModels? models, T data, Func<T, VespaTensor> embedding, bool nested = false) where T : class
    {
        using var handler = new MockHttpMessageHandler();
        handler.QueueResponses(
            Json("{}"),
            Json(TensorDocument("[1.5,2.5,3.5]")),
            Json(TensorDocument("""
                {"type":"tensor<float>(x[3])","cells":[
                    {"address":{"x":"0"},"value":1.5},
                    {"address":{"x":"1"},"value":2.5},
                    {"address":{"x":"2"},"value":3.5}
                ]}
                """)));
        using var http = new HttpClient(handler);
        using var client = new VespaClient(http, new VespaClientOptions { Endpoint = "http://vespa.test", JsonModels = models });
        var expected = DenseTensor().GetDenseValues<float>();

        await client.Documents.PutAsync("one", data, "product");
        using var body = JsonDocument.Parse(await Assert.Single(handler.Requests).Content!.ReadAsStringAsync());
        var fields = body.RootElement.GetProperty("fields");
        if (nested)
            fields = fields.GetProperty("inner");
        Assert.Equal(expected, fields.GetProperty("embedding").EnumerateArray().Select(cell => cell.GetSingle()));
        var dense = await client.Documents.GetAsync<T>("one", "product");
        Assert.Equal(expected, embedding(dense!.Fields!).GetDenseValues<float>());
        var verbose = await client.Documents.GetAsync<T>("one", "product");
        Assert.Equal(expected, embedding(verbose!.Fields!).Cells!.Select(cell => (float)cell.Value));

        string TensorDocument(string tensor)
        {
            var documentFields = "{\"embedding\":" + tensor + "}";
            if (nested)
                documentFields = "{\"inner\":" + documentFields + "}";
            return "{\"fields\":" + documentFields + "}";
        }
    }

    [Fact]
    public async Task DocumentReadsAndWritesUseRegisteredMetadata()
    {
        using var handler = new MockHttpMessageHandler();
        handler.QueueResponses(
            Json("""{"id":"id:default:product::one:two","pathId":"/document/v1/default/product/docid/one:two","fields":{"product_name":"Laptop"}}"""),
            Json("{}"),
            Json("{}", HttpStatusCode.NotFound),
            Json("""{"code":412,"message":"stale document"}""", HttpStatusCode.PreconditionFailed));
        using var http = new HttpClient(handler);
        using var client = CreateClient(http);

        var document = await client.Documents.GetAsync<GeneratedJsonProduct>("one:two", "product");
        Assert.NotNull(document);
        Assert.Equal("id:default:product::one:two", document.FullId);
        Assert.Equal("/document/v1/default/product/docid/one:two", document.PathId);
        Assert.Equal(new GeneratedJsonProduct("Laptop").WithId("one:two"), document.Fields);

        await client.Documents.PutAsync("one:two", document.Fields!, "product", condition: "product.price > 0");
        using var json = JsonDocument.Parse(await handler.Requests[1].Content!.ReadAsStringAsync());
        var fields = json.RootElement.GetProperty("fields");
        Assert.Equal("Laptop", fields.GetProperty("product_name").GetString());
        Assert.False(fields.TryGetProperty("id", out _));
        Assert.Null(await client.Documents.GetAsync<GeneratedJsonProduct>("missing", "product"));
        var error = await Assert.ThrowsAsync<VespaConditionNotMetException>(() =>
            client.Documents.PutAsync("one:two", document.Fields!, "product"));
        Assert.Equal("stale document", error.Error?.Message);
    }

    [Fact]
    public async Task SearchReadsGeneratedFieldsAndPreservesResponseMetadata()
    {
        using var handler = new MockHttpMessageHandler();
        handler.QueueResponse(Json("""
            {"root":{"fields":{"totalCount":42},"coverage":{"coverage":90},
            "children":[{"id":"id:default:product::one","relevance":"NaN","matchfeatures":{"score":1},
            "fields":{"product_name":"Laptop","matchfeatures":{"score":2}}},
            {"id":"index:2","fields":null}]},"timing":{"querytime":0.25}}
            """));
        using var http = new HttpClient(handler);
        using var client = CreateClient(http);
        using var parameter = JsonDocument.Parse("""{"limit":42}""");

        var request = new VespaSearchRequest { Yql = "select * from product where true" }
            .WithParameter("custom.payload", parameter.RootElement);
        var response = await client.Search.SearchAsync<GeneratedJsonProduct>(request);

        Assert.Equal(42, response.Root.Fields?.TotalCount);
        Assert.Equal(90, response.Root.Coverage?.CoveragePercentage);
        Assert.Equal(0.25, response.Timing?.QueryTime);
        Assert.Collection(response.Root.Children,
            hit =>
            {
                Assert.Equal(new GeneratedJsonProduct("Laptop").WithId("one"), hit.Fields);
                Assert.Equal(2, hit.GetMatchFeature("score"));
                Assert.True(double.IsNaN(hit.Relevance));
            },
            hit => Assert.Null(hit.Fields));
        using var json = JsonDocument.Parse(await Assert.Single(handler.Requests).Content!.ReadAsStringAsync());
        Assert.Equal(request.Yql, json.RootElement.GetProperty("yql").GetString());
        Assert.Equal(42, json.RootElement.GetProperty("custom.payload").GetProperty("limit").GetInt32());
    }

    [Fact]
    public async Task VisitsUseRegisteredMetadataForPagesAndJsonlLines()
    {
        using var handler = new MockHttpMessageHandler();
        handler.QueueResponses(
            Json("""
                {"documentCount":1,"continuation":"opaque:t","pathId":"/visit",
                "documents":[{"id":"id:default:product::one","fields":{"product_name":"Laptop"}}]}
                """),
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"put":"id:default:product::one","fields":{"product_name":"Laptop"}}
                    {"remove":"id:default:product::two"}
                    {"continuation":{"percentFinished":100}}
                    """, System.Text.Encoding.UTF8, "application/jsonl")
            });
        using var http = new HttpClient(handler);
        using var client = CreateClient(http);

        var page = await client.Documents.VisitPageAsync<GeneratedJsonProduct>("product");
        Assert.Equal(1, page.DocumentCount);
        Assert.Equal("opaque:t", page.Continuation);
        Assert.Equal("/visit", page.PathId);
        var product = new GeneratedJsonProduct("Laptop").WithId("one");
        Assert.Equal(product, Assert.Single(page.Documents).Fields);

        var streamed = new List<VespaDocument<GeneratedJsonProduct>>();
        await foreach (var entry in client.Documents.VisitJsonlAsync<GeneratedJsonProduct>("product"))
            streamed.Add(entry);

        Assert.Collection(streamed, entry => Assert.Equal(product, entry.Fields), entry =>
        {
            Assert.Equal("two", entry.Id);
            Assert.Null(entry.Fields);
        });
    }

    [Fact]
    public async Task GroupingReadsSummariesWithTheRegisteredMetadata()
    {
        using var handler = new MockHttpMessageHandler();
        handler.QueueResponse(Json("""
            {"root":{"children":[{"id":"group:root:0","continuation":{"this":"this-token"},
            "children":[{"id":"grouplist:product_name","continuation":{"next":"next-token"},
            "children":[{"id":"group:string:Laptop","fields":{"count()":9007199254740993},
            "children":[{"id":"hitlist:summary","children":[{"id":"id:default:product::one",
            "fields":{"product_name":"Laptop"}}]}]}]}]}]}}
            """));
        using var http = new HttpClient(handler);
        using var client = CreateClient(http);

        var grouping = await client.Search.GroupByAsync<GeneratedJsonProduct>(
            new VespaSearchRequest { Yql = "select * from product where true | all(group(product_name))" });
        var group = Assert.Single(Assert.Single(grouping.AsTyped().GroupingResults).Groups);
        Assert.Equal(new GeneratedJsonProduct("Laptop").WithId("one"), Assert.Single(group.Hits).Fields);
        Assert.Equal(9007199254740993L, group.GetAggregation<long>("count()", TestModelJson.Default.Options));
        Assert.Equal(new[] { "this-token", "next-token" }, grouping.ContinuationTokens);
    }

    [Fact]
    public async Task GeneratedUpdatePayloadIsSharedByAddressedAndSelectionUpdates()
    {
        using var handler = new MockHttpMessageHandler();
        handler.QueueResponses(Json("{}"), Json("""{"documentCount":1,"continuation":"update:t"}"""));
        using var http = new HttpClient(handler);
        using var client = CreateClient(http);
        using var additionalFields = JsonDocument.Parse("""{"limit":42}""");
        var fields = new Dictionary<string, FieldOperation>
        {
            ["tags"] = FieldOp.Match("hot", FieldOp.Increment(3)),
            ["settings"] = FieldOp.Assign(additionalFields.RootElement),
            ["scores"] = FieldOp.Modify(TensorModifyOperation.Replace, [new TensorCellUpdate(new() { ["x"] = "1" }, 7)])
        };

        await client.Documents.UpdateFieldsAsync("one", fields, "product");
        var page = await client.Documents.UpdateBySelectionPageAsync("product.price > 0", fields, "product");
        Assert.Equal(1, page.DocumentCount);
        Assert.Equal("update:t", page.Continuation);

        Assert.Equal(2, handler.Requests.Count);
        foreach (var request in handler.Requests)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var update = body.RootElement.GetProperty("fields");
            var match = update.GetProperty("tags").GetProperty("match");
            Assert.Equal("hot", match.GetProperty("element").GetString());
            Assert.Equal(3, match.GetProperty("increment").GetInt32());
            Assert.Equal(42, update.GetProperty("settings").GetProperty("assign").GetProperty("limit").GetInt32());
            var modify = update.GetProperty("scores").GetProperty("modify");
            Assert.Equal("replace", modify.GetProperty("operation").GetString());
            var cell = Assert.Single(modify.GetProperty("cells").EnumerateArray());
            Assert.Equal("1", cell.GetProperty("address").GetProperty("x").GetString());
            Assert.Equal(7, cell.GetProperty("value").GetInt32());
        }
    }

    [Fact]
    public async Task MissingMetadataIsReportedBeforeSendingTheRequest()
    {
        using var handler = new MockHttpMessageHandler();
        using var http = new HttpClient(handler);
        using var client = CreateClient(http);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.Documents.GetAsync<object>("one", "product"));
        Assert.Contains("System.Object", error.Message);
        var request = new VespaSearchRequest { Yql = "select * from product where true" }
            .WithParameter("custom.payload", new GeneratedJsonProduct("Laptop"));
        await Assert.ThrowsAsync<NotSupportedException>(() => client.Search.SearchAsync<GeneratedJsonProduct>(request));
        Assert.Empty(handler.Requests);
    }

    private static VespaClient CreateClient(HttpClient http) => new(http, new VespaClientOptions
    {
        Endpoint = "http://vespa.test",
        JsonModels = new VespaJsonModels().With(
            TestModelJson.Default.GeneratedJsonProduct, static (model, id) => model.WithId(id))
    });

    private static HttpResponseMessage Json(string body, HttpStatusCode statusCode = HttpStatusCode.OK) =>
        new(statusCode) { Content = new StringContent(body) };
}

internal sealed record GeneratedJsonProduct([property: JsonPropertyName("product_name")] string Name)
{
    [JsonIgnore]
    public string? Id { get; private init; }

    public GeneratedJsonProduct WithId(string id) => this with { Id = id };
}

internal sealed record GeneratedTensorWithoutConverter(
    [property: JsonPropertyName("embedding"), Vespa.Models.Attributes.VespaTensor("tensor<float>(x[3])")]
    VespaTensor Embedding);

internal sealed record GeneratedTensorWithConverter(
    [property: JsonPropertyName("embedding"), JsonConverter(typeof(VespaTensorConverter))]
    VespaTensor Embedding);

internal sealed record GeneratedNestedTensorWithoutConverter(
    [property: JsonPropertyName("inner")] GeneratedTensorWithoutConverter Inner);

internal sealed record GeneratedNestedTensorWithConverter(
    [property: JsonPropertyName("inner")] GeneratedTensorWithConverter Inner);

internal sealed record GeneratedNestedTensorCollection(
    [property: JsonPropertyName("items")] GeneratedTensorWithoutConverter[] Items);

internal sealed record GeneratedTensorDictionary(
    [property: JsonPropertyName("tensors")] Dictionary<string, VespaTensor> Tensors);

internal sealed class GeneratedRecursiveNode
{
    [JsonPropertyName("child")]
    public GeneratedRecursiveNode? Child { get; init; }
}

internal sealed record GeneratedRuntimePayload(
    [property: JsonPropertyName("payload")] object Payload,
    [property: JsonPropertyName("extra")] JsonElement Extra);

[JsonSerializable(typeof(GeneratedTensorWithoutConverter))]
[JsonSerializable(typeof(GeneratedTensorWithConverter))]
[JsonSerializable(typeof(GeneratedNestedTensorWithoutConverter))]
[JsonSerializable(typeof(GeneratedNestedTensorWithConverter))]
[JsonSerializable(typeof(GeneratedNestedTensorCollection))]
[JsonSerializable(typeof(GeneratedTensorDictionary))]
[JsonSerializable(typeof(GeneratedRecursiveNode))]
[JsonSerializable(typeof(GeneratedRuntimePayload))]
[JsonSerializable(typeof(GeneratedJsonProduct))]
[JsonSerializable(typeof(long))]
internal sealed partial class TestModelJson : JsonSerializerContext;
