using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Vespa.Models;
using Vespa.Models.Attributes;
using Vespa.Search;
using Vespa.Tests.Helpers;
using Xunit;

namespace Vespa.Tests.Unit;

public sealed class TypedGroupingTests
{
    [Theory]
    [InlineData(long.MinValue)]
    [InlineData(-9007199254740993L)]
    [InlineData(9007199254740993L)]
    [InlineData(long.MaxValue)]
    public void Aggregation_Int64_PreservesValuesBeyondDoublePrecision(long expected)
    {
        var group = WithAggregation(expected.ToString(CultureInfo.InvariantCulture));

        Assert.Equal(expected, group.GetAggregation<long>("value"));
        Assert.True(group.TryGetAggregation<long>("value", out var value));
        Assert.Equal(expected, value);
        Assert.Equal(expected, Typed(group).GetAggregation<long>("value"));
    }

    [Fact]
    public void Aggregation_ReadsDecimalsDoublesStringsAndStructuredOutputs()
    {
        var group = WithFields("""
            {"exact":12345678901234567890.123456789,"mean":1.25,"label":"tools",
             "quantiles":[{"quantile":0.5,"value":12.5},{"quantile":0.9,"value":20.0}]}
            """);

        Assert.Equal(12345678901234567890.123456789m, group.GetAggregation<decimal>("exact"));
        Assert.Equal(1.25, group.GetAggregation<double>("mean"));
        Assert.Equal("tools", group.GetAggregation<string>("label"));
        Assert.Equal([new Quantile(0.5, 12.5), new Quantile(0.9, 20)], group.GetAggregation<Quantile[]>("quantiles")!);
        Assert.Equal(JsonValueKind.Array, group.GetAggregation<JsonElement>("quantiles").ValueKind);
    }

    [Theory]
    [InlineData("1.5")]
    [InlineData("9223372036854775808")]
    [InlineData("-9223372036854775809")]
    [InlineData("\"42\"")]
    [InlineData("true")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[]")]
    public void Aggregation_IncompatibleInt64_ThrowsOrReturnsFalse(string json)
    {
        var group = WithAggregation(json);

        Assert.Throws<JsonException>(() => group.GetAggregation<long>("value"));
        Assert.False(group.TryGetAggregation<long>("value", out var value));
        Assert.Equal(0, value);
        Assert.False(Typed(group).TryGetAggregation<long>("value", out _));
    }

    [Fact]
    public void Aggregation_JsonNull_SucceedsForNullableAndReferenceTypes()
    {
        var group = WithAggregation("null");

        Assert.Null(group.GetAggregation<long?>("value"));
        Assert.Null(group.GetAggregation<string>("value"));
        Assert.True(group.TryGetAggregation<long?>("value", out var number));
        Assert.Null(number);
        Assert.True(group.TryGetAggregation<string>("value", out var text));
        Assert.Null(text);
    }

    [Fact]
    public void Aggregation_MissingValue_DoesNotInventZeroOrUseRoundedLegacyValues()
    {
        var group = new VespaGroup("bucket", new Dictionary<string, double> { ["count()"] = 9007199254740992 }, []);

        var error = Assert.Throws<KeyNotFoundException>(() => group.GetAggregation<long>("count()"));
        Assert.Contains("count()", error.Message);
        Assert.False(group.TryGetAggregation<long>("count()", out var count));
        Assert.Equal(0, count);
        Assert.False(group.TryGetAggregation<string>("absent", out var text));
        Assert.Null(text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Aggregation_InvalidName_ThrowsArgumentException(string? name)
    {
        var group = WithAggregation("1");

        Assert.ThrowsAny<ArgumentException>(() => group.GetAggregation<long>(name!));
        Assert.ThrowsAny<ArgumentException>(() => group.TryGetAggregation<long>(name!, out _));
    }

    [Fact]
    public void Aggregation_ExplicitSerializerOptions_AreUsedByBothAccessors()
    {
        var group = WithAggregation("\"9007199254740993\"");
        var options = new JsonSerializerOptions { NumberHandling = JsonNumberHandling.AllowReadingFromString };

        Assert.Equal(9007199254740993L, group.GetAggregation<long>("value", options));
        Assert.True(Typed(group).TryGetAggregation<long>("value", out var value, options));
        Assert.Equal(9007199254740993L, value);
    }

    [Fact]
    public void Aggregation_UnsupportedType_IsNotHiddenByTryAccessor()
    {
        var group = WithAggregation("\"System.String\"");

        Assert.Throws<NotSupportedException>(() => group.GetAggregation<Type>("value"));
        Assert.Throws<NotSupportedException>(() => group.TryGetAggregation<Type>("value", out _));
    }

    [Fact]
    public async Task AsTyped_PreservesNestedSummariesExactOutputsAndPartialResponseMetadata()
    {
        using var handler = new MockHttpMessageHandler();
        handler.QueueResponse(JsonResponse("""
            {"timing":{"querytime":0.01},"trace":{"children":[{"message":"trace"}]},
             "root":{"id":"toplevel","fields":{"totalCount":42},
              "coverage":{"coverage":50,"documents":42,"full":false,"degraded":{"timeout":true}},
              "errors":[{"code":12,"summary":"Timed out","message":"Partial grouping"}],
              "children":[
               {"id":"id:test:product::regular","relevance":3,"fields":{"product_name":"Regular"}},
               {"id":"group:root:0","fields":{"count()":9007199254740993},
                "continuation":{"this":"root-token"},"children":[
                 {"id":"hitlist:root","children":[
                  {"id":"id:test:product::root","relevance":2,"fields":{"product_name":"Root summary"}}]},
                 {"id":"grouplist:category","continuation":{"next":"next-token"},"children":[
                  {"id":"group:string:tools","value":"tools","fields":{"count()":2},"children":[
                   {"id":"hitlist:category","children":[
                    {"id":"id:test:product::tool","relevance":1,"fields":{"product_name":"Tool"}}]},
                   {"id":"grouplist:quantity","children":[
                    {"id":"group:long:12","value":12,"fields":{"max(price)":1.25},"children":[
                     {"id":"hitlist:quantity","children":[
                      {"id":"id:test:product::nested","relevance":1,"fields":{"product_name":"Nested"}}]}]}]}]},
                  {"id":"group:null","fields":{"count()":1}}]}]},
               {"id":"group:root:1","fields":{"count()":7}}
              ]}}
            """));
        using var http = new HttpClient(handler) { BaseAddress = new("http://localhost:8080") };
        var search = new SearchOperations(http, new() { Endpoint = "http://localhost:8080" });

        var source = await search.GroupByAsync<Product>(new() { Yql = "select * from product where true | all(group(category) each(output(count())))" });
        var result = source.AsTyped();

        Assert.Equal(42, result.TotalCount);
        Assert.False(result.Coverage!.Full);
        Assert.True(result.Coverage.Degraded!.Timeout);
        Assert.Equal("Partial grouping", Assert.Single(result.Errors!).Message);
        Assert.Equal(0.01, result.Timing!.QueryTime);
        Assert.NotEmpty(result.Trace!.Children!);
        Assert.Equal(["root-token", "next-token"], result.ContinuationTokens);
        Assert.Equal("Regular", Assert.Single(result.Hits).Fields!.Name);
        Assert.Equal(2, result.GroupingRoots.Count);
        Assert.Equal(9007199254740993L, result.GroupingRoots[0].GetAggregation<long>("count()"));
        Assert.Equal("Root summary", Assert.Single(result.GroupingRoots[0].Hits).Fields!.Name);
        Assert.Equal(7, result.GroupingRoots[1].GetAggregation<long>("count()"));

        var list = Assert.Single(result.GroupingResults);
        Assert.Equal("category", list.Label);
        Assert.Same(list, Assert.Single(result.GroupingRoots[0].SubGroups));
        var tools = Assert.Single(list.Groups, group => group.Value == "tools");
        Assert.Equal("group:string:tools", tools.Id);
        var toolHit = Assert.Single(tools.Hits);
        Assert.Equal("Tool", toolHit.Fields!.Name);
        Assert.Equal("tool", toolHit.Fields.Id);
        Assert.Equal("id:test:product::tool", toolHit.FullId);
        var nested = Assert.Single(Assert.Single(tools.SubGroups).Groups);
        Assert.Equal("12", nested.Value);
        Assert.Equal(1.25, nested.GetAggregation<double>("max(price)"));
        Assert.Equal("Nested", Assert.Single(nested.Hits).Fields!.Name);
        Assert.Equal(1, Assert.Single(list.Groups, group => group.IsRestBucket).GetAggregation<long>("count()"));

        // Existing consumers keep their original model, summaries and double view.
        Assert.Same(source.Hits[0], result.Hits[0]);
        Assert.Same(source.GroupingResults[0].Groups[0].GetHits<Product>()[0], toolHit);
        Assert.Equal(2, source.GroupingResults[0].Groups[0].Aggregations["count()"]);
        Assert.Equal(9007199254740993L, source.GroupingRoots[0].GetAggregation<long>("count()"));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public void AsTyped_EmptyResponseAndIndependentLists_AreSupported()
    {
        var empty = new GroupingSearchResponse<Product>([], [], 0).AsTyped();
        Assert.Empty(empty.GroupingResults);
        Assert.Empty(empty.GroupingRoots);
        Assert.Null(empty.Coverage);
        Assert.Null(empty.ContinuationTokens);

        var group = WithAggregation("42");
        var response = new GroupingSearchResponse<Product>([], [new("only-list", [group])], 1);
        var result = response.AsTyped();
        Assert.Equal("only-list", Assert.Single(result.GroupingResults).Label);
        Assert.Equal(42, Assert.Single(result.GroupingResults[0].Groups).GetAggregation<long>("value"));

        var modified = (response with { GroupingResults = [] }).AsTyped();
        Assert.Empty(modified.GroupingResults);
        Assert.Single(result.GroupingResults);
    }

    [Fact]
    public void AsTyped_ManuallyConstructedMismatchedSummaries_ThrowsInsteadOfDroppingHits()
    {
        var invalid = WithAggregation("1") with { Hits = [new SearchHit<object>()] };
        var response = new GroupingSearchResponse<Product>([], [new("category", [invalid])], 1);

        Assert.Throws<InvalidCastException>(() => response.AsTyped());
    }

    [Fact]
    public async Task AsTyped_EachStreamingPage_PreservesContinuationAndRequest()
    {
        using var handler = new MockHttpMessageHandler();
        handler.QueueResponse(JsonResponse("""
            {"root":{"id":"toplevel","children":[
             {"id":"group:root:0","continuation":{"this":"root-token"},"children":[
              {"id":"grouplist:category","continuation":{"next":"next-token"},"children":[
               {"id":"group:string:first","value":"first","fields":{"count()":9007199254740993}}]}]}]}}
            """));
        handler.QueueResponse(JsonResponse("""
            {"root":{"id":"toplevel","children":[
             {"id":"group:root:0","children":[{"id":"grouplist:category","children":[
              {"id":"group:string:second","value":"second","fields":{"count()":2}}]}]}]}}
            """));
        using var http = new HttpClient(handler) { BaseAddress = new("http://localhost:8080") };
        var search = new SearchOperations(http, new() { Endpoint = "http://localhost:8080" });
        const string yql = "select * from product where true | all(group(category) max(1) each(output(count())))";
        var request = new VespaSearchRequest { Yql = yql };
        var pages = new List<TypedGroupingSearchResponse<Product>>();

        await foreach (var page in search.GroupByStreamAsync<Product>(request))
            pages.Add(page.AsTyped());

        Assert.Equal(2, pages.Count);
        Assert.Equal(9007199254740993L, pages[0].GroupingResults[0].Groups[0].GetAggregation<long>("count()"));
        Assert.Equal(2, pages[1].GroupingResults[0].Groups[0].GetAggregation<long>("count()"));
        Assert.Equal(["root-token", "next-token"], pages[0].ContinuationTokens);
        Assert.Null(pages[1].ContinuationTokens);
        Assert.Equal(yql, request.Yql);
        using var body = JsonDocument.Parse(await handler.Requests[1].Content!.ReadAsStringAsync());
        var continuedYql = body.RootElement.GetProperty("yql").GetString()!;
        Assert.Contains("'continuations':['root-token', 'next-token']", continuedYql);
    }

    private static VespaGroup WithAggregation(string json) => WithFields($$"""{"value":{{json}}}""");

    private static VespaGroup WithFields(string json)
    {
        using var document = JsonDocument.Parse(json);
        return new("bucket", new Dictionary<string, double>(), [])
        {
            RawAggregations = document.RootElement.EnumerateObject()
                .ToDictionary(field => field.Name, field => field.Value.Clone())
        };
    }

    private static VespaGroup<Product> Typed(VespaGroup group) =>
        new GroupingSearchResponse<Product>([], [], 0) { GroupingRoots = [group] }.AsTyped().GroupingRoots[0];

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed record Quantile(
        [property: JsonPropertyName("quantile")] double Position,
        [property: JsonPropertyName("value")] double Value);

    private sealed record Product
    {
        [VespaId]
        public string? Id { get; init; }

        [VespaField(Name = "product_name")]
        public string Name { get; init; } = "";
    }
}
