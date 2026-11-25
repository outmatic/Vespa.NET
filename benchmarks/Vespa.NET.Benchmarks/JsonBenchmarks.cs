using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Order;
using Vespa.Models;
using Vespa.Models.Attributes;

namespace Vespa.Benchmarks;

// Measures search body encoding and decoding with in-memory streams.
[MemoryDiagnoser]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
public class JsonBenchmarks
{
    [VespaDocument("product", Namespace = "shop")]
    public sealed record Product
    {
        [VespaId] public string? Id { get; init; }
        [VespaField(Name = "product_name")] public string Name { get; init; } = "";
        [VespaField(Name = "price")] public double Price { get; init; }
        public string CategoryName { get; init; } = "";
    }

    private VespaSearchRequest _searchRequest = null!;
    private byte[] _searchResponseBody = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        _searchRequest = new VespaSearchRequest
        {
            Yql = "select * from product where price < 500;",
            Hits = 10,
            Ranking = new RankingConfig { Profile = "default" }
        };

        _searchResponseBody = Encoding.UTF8.GetBytes("""
            {
              "root": {
                "id": "toplevel",
                "relevance": 1.0,
                "fields": { "totalCount": 3 },
                "children": [
                  { "id": "id:shop:product::p-1", "relevance": 0.9,
                    "fields": { "product_name": "Laptop", "price": 499.0, "category_name": "electronics" } },
                  { "id": "id:shop:product::p-2", "relevance": 0.8,
                    "fields": { "product_name": "Mouse", "price": 29.0, "category_name": "accessories" } },
                  { "id": "id:shop:product::p-3", "relevance": 0.7,
                    "fields": { "product_name": "Desk", "price": 349.0, "category_name": "furniture" } }
                ]
              }
            }
            """);

        using var requestBody = new MemoryStream();
        await WriteSearchRequestAsync(requestBody);
        requestBody.Position = 0;
        using var requestJson = await JsonDocument.ParseAsync(requestBody);
        var request = requestJson.RootElement;
        if (request.GetProperty("yql").GetString() != _searchRequest.Yql ||
            request.GetProperty("hits").GetInt32() != 10 ||
            request.GetProperty("ranking").GetProperty("profile").GetString() != "default")
            throw new InvalidOperationException("The benchmark request did not encode the expected search parameters.");

        Product[] expected =
        [
            new() { Id = "p-1", Name = "Laptop", Price = 499, CategoryName = "electronics" },
            new() { Id = "p-2", Name = "Mouse", Price = 29, CategoryName = "accessories" },
            new() { Id = "p-3", Name = "Desk", Price = 349, CategoryName = "furniture" }
        ];
        var response = await Deserialize_SearchResponse();
        if (response is null || response.Root.Fields?.TotalCount != expected.Length ||
            !response.Root.Children.Select(hit => hit.Fields).SequenceEqual(expected))
            throw new InvalidOperationException("The benchmark response did not decode the expected products and document IDs.");
    }

    [Benchmark]
    public Task Serialize_SearchRequest() => WriteSearchRequestAsync(Stream.Null);

    [Benchmark]
    public async Task<VespaSearchResponse<Product>?> Deserialize_SearchResponse()
    {
        using var content = new ByteArrayContent(_searchResponseBody);
        return await VespaIdInjector.DeserializeSearchAndInjectAsync<Product>(content, CancellationToken.None);
    }

    private async Task WriteSearchRequestAsync(Stream destination)
    {
        using var content = JsonContent.Create(_searchRequest, options: VespaJsonOptions.Default);
        await content.CopyToAsync(destination);
    }
}
