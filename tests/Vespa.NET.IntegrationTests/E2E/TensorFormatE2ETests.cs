using Vespa.Documents;
using Vespa.IntegrationTests.Fixtures;
using Vespa.Models;
using Vespa.Models.Tensors;
using Vespa.Query;
using Vespa.Search;
using Xunit;

namespace Vespa.IntegrationTests.E2E;

/// <summary>
/// Round-trip tests for the tensor rendering formats: documents are fed with known
/// tensor values, read back with <c>format.tensors</c> / <c>presentation.format.tensors</c>
/// overrides, and the parsed <see cref="VespaTensor"/> values are compared to what was fed.
/// </summary>
[Collection("Vespa")]
[Trait("Category", "E2E")]
public class TensorFormatE2ETests(VespaFixture fixture)
{
    private static bool Enabled => VespaFixture.IntegrationEnabled;

    private static string NewId() => $"tensor-fmt-{Guid.NewGuid():N}";

    private const float FloatTolerance = 1e-6f;

    private static void AssertClose(float[] expected, float[]? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.Length, actual.Length);
        for (var i = 0; i < expected.Length; i++)
            Assert.Equal(expected[i], actual[i], FloatTolerance);
    }

    // ── Document GET: short-value ────────────────────────────────────────────

    [Fact]
    public async Task DocumentGet_ShortValueFormat_DenseTensor_Parses()
    {
        if (!Enabled) return;

        var id = NewId();
        float[] values = [1.5f, -2.25f, 0.125f, 42f];
        try
        {
            await fixture.Client.Documents.PutAsync(id, new TestProduct
            {
                Name = "short-value dense",
                Category = "tensor-fmt",
                Embedding = VespaTensor.FromDenseValues(values)
            });

            // short-value renders dense tensors as bare (nested) arrays
            var doc = await fixture.Client.Documents.GetAsync<TestProduct>(
                id, "product", @namespace: "test",
                requestOptions: new DocumentRequestOptions { TensorFormat = "short-value" });

            AssertClose(values, doc?.Fields?.Embedding?.GetDenseValues<float>());
        }
        finally
        {
            await fixture.Client.Documents.DeleteAsync<TestProduct>(id);
        }
    }

    [Fact]
    public async Task DocumentGet_ShortValueFormat_MappedTensor_Parses()
    {
        if (!Enabled) return;

        var id = NewId();
        var mapped = new Dictionary<string, float> { ["a"] = 1.5f, ["b"] = -3.75f };
        try
        {
            await fixture.Client.Documents.PutAsync(id, new TestProduct
            {
                Name = "short-value mapped",
                Category = "tensor-fmt",
                Weights = VespaTensor.FromMappedValues(mapped)
            });

            var doc = await fixture.Client.Documents.GetAsync<TestProduct>(
                id, "product", @namespace: "test",
                requestOptions: new DocumentRequestOptions { TensorFormat = "short-value" });

            var parsed = doc?.Fields?.Weights?.GetMappedValues<float>();
            Assert.NotNull(parsed);
            Assert.Equal(2, parsed.Count);
            Assert.Equal(1.5f, parsed["a"], FloatTolerance);
            Assert.Equal(-3.75f, parsed["b"], FloatTolerance);
        }
        finally
        {
            await fixture.Client.Documents.DeleteAsync<TestProduct>(id);
        }
    }

    // ── Search: hex and hex-value ────────────────────────────────────────────

    [Fact]
    public async Task Search_HexTensorFormat_DenseFloat_RoundTrips()
    {
        if (!Enabled) return;

        var id = NewId();
        var category = $"hex-{Guid.NewGuid():N}";
        float[] values = [0.5f, -1.25f, 3.5f, -0.0625f];
        try
        {
            await fixture.Client.Documents.PutAsync(id, new TestProduct
            {
                Name = "hex dense",
                Category = category,
                Embedding = VespaTensor.FromDenseValues(values)
            });
            await Task.Delay(2000);

            // hex renders {"type":…,"cells":"<hex>"} — type-aware float decoding
            var request = new VespaSearchRequest
            {
                Yql = YqlBuilder<TestProduct>.Select()
                    .Where(w => w.Field(p => p.Category).Contains(category))
                    .Build(),
                PresentationFormatTensors = "hex"
            };

            var result = await fixture.Client.Search.SearchAsync<TestProduct>(request);
            var hit = Assert.Single(result.Root.Children);
            AssertClose(values, hit.Fields?.Embedding?.GetDenseValues<float>());
        }
        finally
        {
            await fixture.Client.Documents.DeleteAsync<TestProduct>(id);
        }
    }

    [Fact]
    public async Task Search_HexValueTensorFormat_DenseDouble_RoundTrips()
    {
        if (!Enabled) return;

        var id = NewId();
        var category = $"hexval-{Guid.NewGuid():N}";
        double[] values = [1.5, -2.25, 1234.0625];
        try
        {
            await fixture.Client.Documents.PutAsync(id, new TestProduct
            {
                Name = "hex-value dense",
                Category = category,
                // hex-value renders a bare hex string without a type — the converter
                // decodes 16 hex digits per cell, so use the double tensor field.
                DenseDouble = VespaTensor.FromDenseValues(values)
            });
            await Task.Delay(2000);

            var request = new VespaSearchRequest
            {
                Yql = YqlBuilder<TestProduct>.Select()
                    .Where(w => w.Field(p => p.Category).Contains(category))
                    .Build(),
                PresentationFormatTensors = "hex-value"
            };

            var result = await fixture.Client.Search.SearchAsync<TestProduct>(request);
            var hit = Assert.Single(result.Root.Children);
            var parsed = hit.Fields?.DenseDouble?.GetDenseValues<double>();
            Assert.NotNull(parsed);
            Assert.Equal(values, parsed);
        }
        finally
        {
            await fixture.Client.Documents.DeleteAsync<TestProduct>(id);
        }
    }

    // ── Mixed tensor (blocks) ────────────────────────────────────────────────

    [Fact]
    public async Task MixedTensor_BlocksForm_RoundTrips()
    {
        if (!Enabled) return;

        var id = NewId();
        var blocks = new List<TensorBlock>();
        var block1 = new TensorBlock { Address = new Dictionary<string, string> { ["a"] = "left", ["b"] = "up" } };
        block1.SetValues(new float[] { 1.5f, -2.5f });
        var block2 = new TensorBlock { Address = new Dictionary<string, string> { ["a"] = "right", ["b"] = "down" } };
        block2.SetValues(new float[] { 0.25f, 8f });
        blocks.Add(block1);
        blocks.Add(block2);

        try
        {
            var put = await fixture.Client.Documents.PutAsync(id, new TestProduct
            {
                Name = "mixed blocks",
                Category = "tensor-fmt",
                BlockWeights = VespaTensor.FromBlocks(blocks)
            });
            Assert.True(put.IsSuccess);

            var doc = await fixture.Client.Documents.GetAsync<TestProduct>(id);
            var parsed = doc?.Fields?.BlockWeights;
            Assert.NotNull(parsed);
            Assert.NotNull(parsed.Blocks);
            Assert.Equal(2, parsed.Blocks.Count);

            var left = parsed.Blocks.Single(b => b.Address["a"] == "left" && b.Address["b"] == "up");
            AssertClose([1.5f, -2.5f], left.GetValues<float>());

            var right = parsed.Blocks.Single(b => b.Address["a"] == "right" && b.Address["b"] == "down");
            AssertClose([0.25f, 8f], right.GetValues<float>());
        }
        finally
        {
            await fixture.Client.Documents.DeleteAsync<TestProduct>(id);
        }
    }

    // ── Search default (short) form for mapped tensors ───────────────────────

    [Fact]
    public async Task Search_DefaultFormat_MappedTensor_RoundTrips()
    {
        if (!Enabled) return;

        var id = NewId();
        var category = $"mapped-{Guid.NewGuid():N}";
        var mapped = new Dictionary<string, float> { ["x"] = 7.5f, ["y"] = -0.5f };
        try
        {
            await fixture.Client.Documents.PutAsync(id, new TestProduct
            {
                Name = "mapped default",
                Category = category,
                Weights = VespaTensor.FromMappedValues(mapped)
            });
            await Task.Delay(2000);

            var result = await fixture.Client.Search.QueryAsync<TestProduct>(
                YqlBuilder<TestProduct>.Select()
                    .Where(w => w.Field(p => p.Category).Contains(category))
                    .Build());

            var hit = Assert.Single(result.Root.Children);
            var parsed = hit.Fields?.Weights?.GetMappedValues<float>();
            Assert.NotNull(parsed);
            Assert.Equal(7.5f, parsed["x"], FloatTolerance);
            Assert.Equal(-0.5f, parsed["y"], FloatTolerance);
        }
        finally
        {
            await fixture.Client.Documents.DeleteAsync<TestProduct>(id);
        }
    }
}
