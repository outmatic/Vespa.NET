using System.Text.Json;
using System.Text.Json.Serialization;
using Vespa.Models;
using Xunit;

namespace Vespa.IntegrationTests.Serialization;

public sealed class SearchResponseSourceGenerationTests
{
    [Fact]
    public void GeneratedContext_ReadsAndWritesSearchHits()
    {
        const string json = """
            {
              "root": {
                "fields": { "totalCount": 1 },
                "children": [{
                  "id": "id:shop:product::p-1",
                  "relevance": 0.9,
                  "matchfeatures": { "score": 1 },
                  "fields": {
                    "product_name": "Laptop",
                    "matchfeatures": { "score": 2 },
                    "summaryfeatures": { "popularity": 3 }
                  }
                }]
              }
            }
            """;

        var typeInfo = SearchResponseContext.Default.Response;
        var response = Assert.IsType<VespaSearchResponse<SourceGeneratedProduct>>(JsonSerializer.Deserialize(json, typeInfo));
        var hit = Assert.Single(response.Root.Children);
        Assert.Equal("id:shop:product::p-1", hit.FullId);
        Assert.Equal("p-1", hit.Id);
        Assert.Equal("Laptop", hit.Fields?.Name);
        Assert.Equal(2, hit.GetMatchFeature("score"));
        Assert.Equal(3, hit.GetSummaryFeature("popularity"));

        var encoded = JsonSerializer.Serialize(response, typeInfo);
        var decoded = Assert.IsType<VespaSearchResponse<SourceGeneratedProduct>>(JsonSerializer.Deserialize(encoded, typeInfo));
        var roundTripped = Assert.Single(decoded.Root.Children);
        Assert.Equal(hit.Fields, roundTripped.Fields);
        Assert.Equal(hit.GetMatchFeature("score"), roundTripped.GetMatchFeature("score"));
        Assert.Equal(hit.GetSummaryFeature("popularity"), roundTripped.GetSummaryFeature("popularity"));
    }
}

internal sealed record SourceGeneratedProduct([property: JsonPropertyName("product_name")] string Name);

[JsonSerializable(typeof(VespaSearchResponse<SourceGeneratedProduct>), TypeInfoPropertyName = "Response")]
[JsonSerializable(typeof(SourceGeneratedProduct))]
internal sealed partial class SearchResponseContext : JsonSerializerContext;
