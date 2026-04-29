using System.Text.Json;
using System.Text.Json.Serialization;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Order;
using Vespa.Models;
using Vespa.Models.Attributes;

namespace Vespa.Benchmarks;

[MemoryDiagnoser]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
public class JsonBenchmarks
{
    // ── Models ────────────────────────────────────────────────────────────────

    [VespaDocument("product", Namespace = "shop")]
    public sealed record Product
    {
        [VespaField(Name = "product_name")] public string Name { get; init; } = "";
        [VespaField(Name = "price")] public double Price { get; init; }
        public string Category { get; init; } = "";
    }

    // ── State ─────────────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private VespaSearchRequest _searchRequest = null!;
    private string _searchRequestJson = null!;
    private string _searchResponseJson = null!;

    [GlobalSetup]
    public void Setup()
    {
        _searchRequest = new VespaSearchRequest
        {
            Yql = "select * from product where price < 500;",
            Hits = 10,
            Ranking = new RankingConfig { Profile = "default" }
        };

        _searchRequestJson = JsonSerializer.Serialize(_searchRequest, _jsonOptions);

        _searchResponseJson = """
            {
              "root": {
                "id": "toplevel",
                "relevance": 1.0,
                "fields": { "totalCount": 3 },
                "children": [
                  { "id": "id:shop:product::p-1", "relevance": 0.9,
                    "fields": { "product_name": "Laptop", "price": 499.0, "category": "electronics" } },
                  { "id": "id:shop:product::p-2", "relevance": 0.8,
                    "fields": { "product_name": "Mouse",  "price": 29.0,  "category": "accessories" } },
                  { "id": "id:shop:product::p-3", "relevance": 0.7,
                    "fields": { "product_name": "Desk",   "price": 349.0, "category": "furniture" } }
                ]
              }
            }
            """;
    }

    // ── Benchmarks ────────────────────────────────────────────────────────────

    [Benchmark(Baseline = true)]
    public string Serialize_SearchRequest()
        => JsonSerializer.Serialize(_searchRequest, _jsonOptions);

    [Benchmark]
    public VespaSearchRequest? Deserialize_SearchRequest()
        => JsonSerializer.Deserialize<VespaSearchRequest>(_searchRequestJson, _jsonOptions);

    [Benchmark]
    public VespaSearchResponse<Product>? Deserialize_SearchResponse()
        => JsonSerializer.Deserialize<VespaSearchResponse<Product>>(_searchResponseJson, _jsonOptions);
}
