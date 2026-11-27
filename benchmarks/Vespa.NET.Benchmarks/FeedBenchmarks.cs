using System.Linq.Expressions;
using System.Net.Http.Json;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Vespa.Documents;
using Vespa.Feed;
using Product = Vespa.Benchmarks.JsonBenchmarks.Product;

namespace Vespa.Benchmarks;

[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class FeedBenchmarks
{
    private static readonly Expression<Func<Product, bool>> Condition = product =>
        product.Price > 0 && product.CategoryName == "electronics";
    private static readonly double[] Minimums = [100.5, 200.25, 300.75, 400.125];

    private readonly Product _product = new() { Id = "p-1", Name = "Laptop", Price = 499, CategoryName = "electronics" };
    private string _renderedCondition = null!;
    private int _nextMinimum;

    [GlobalSetup]
    public async Task Setup()
    {
        _renderedCondition = DocumentCondition.For(Condition);
        if (PutWithTypedCondition().Condition != PutWithRenderedCondition().Condition)
            throw new InvalidOperationException("The feed operations did not use the same condition.");

        for (var index = 0; index < Minimums.Length; index++)
        {
            _nextMinimum = index;
            var typed = PutWithCapturedThreshold().Condition;
            _nextMinimum = index;
            var invariant = PutWithInvariantThreshold().Condition;
            _nextMinimum = index;
            if (typed != invariant || typed != PutWithLiteralThreshold().Condition)
                throw new InvalidOperationException("The benchmark thresholds did not produce the same condition.");
        }

        using var body = new MemoryStream();
        await WriteDocumentBodyAsync(body);
        body.Position = 0;
        using var json = await JsonDocument.ParseAsync(body);
        var fields = json.RootElement.GetProperty("fields");
        if (fields.GetProperty("product_name").GetString() != _product.Name ||
            fields.GetProperty("price").GetDouble() != _product.Price ||
            fields.GetProperty("category_name").GetString() != _product.CategoryName ||
            fields.TryGetProperty("id", out _))
            throw new InvalidOperationException("The benchmark feed body did not encode the expected document fields.");
    }

    [BenchmarkCategory("Condition"), Benchmark]
    public FeedOperation PutWithTypedCondition() => FeedOperation.Put(_product.Id!, _product, condition: Condition);

    [BenchmarkCategory("Condition"), Benchmark]
    public FeedOperation PutWithRenderedCondition() => FeedOperation.Put(_product.Id!, _product, rawCondition: _renderedCondition);

    [BenchmarkCategory("PerDocumentCondition"), Benchmark(Baseline = true)]
    public FeedOperation PutWithCapturedThreshold()
    {
        var minimum = NextMinimum();
        return FeedOperation.Put(_product.Id!, _product, condition: product => product.Price > minimum);
    }

    [BenchmarkCategory("PerDocumentCondition"), Benchmark]
    public FeedOperation PutWithInvariantThreshold() => FeedOperation.Put(_product.Id!, _product,
        rawCondition: FormattableString.Invariant($"(product.price > {NextMinimum()})"));

    [BenchmarkCategory("PerDocumentCondition"), Benchmark]
    public FeedOperation PutWithLiteralThreshold() => FeedOperation.Put(_product.Id!, _product,
        rawCondition: $"(product.price > {DocumentCondition.FormatLiteral(NextMinimum())})");

    [BenchmarkCategory("DocumentBody"), Benchmark]
    public Task WriteOneDocumentBody() => WriteDocumentBodyAsync(Stream.Null);

    [BenchmarkCategory("DocumentBody"), Benchmark]
    public async Task Write100DocumentBodies()
    {
        for (var index = 0; index < 100; index++)
            await WriteDocumentBodyAsync(Stream.Null);
    }

    private async Task WriteDocumentBodyAsync(Stream destination)
    {
        using var content = JsonContent.Create(new { fields = _product }, options: VespaJsonOptions.Default);
        await content.CopyToAsync(destination);
    }

    private double NextMinimum()
    {
        var minimum = Minimums[_nextMinimum];
        _nextMinimum = (_nextMinimum + 1) % Minimums.Length;
        return minimum;
    }
}
