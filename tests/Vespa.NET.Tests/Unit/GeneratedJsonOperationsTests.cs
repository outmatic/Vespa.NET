using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Vespa.Models;
using Vespa.Tests.Helpers;
using Xunit;

namespace Vespa.Tests.Unit;

public sealed class GeneratedJsonOperationsTests
{
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

[JsonSerializable(typeof(GeneratedJsonProduct))]
[JsonSerializable(typeof(long))]
internal sealed partial class TestModelJson : JsonSerializerContext;
