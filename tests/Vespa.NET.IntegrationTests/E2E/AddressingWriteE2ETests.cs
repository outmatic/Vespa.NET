using Vespa.Documents;
using Vespa.IntegrationTests.Fixtures;
using Vespa.Models;
using Xunit;

namespace Vespa.IntegrationTests.E2E;

/// <summary>
/// Round-trip tests for Put/Update/Delete via group and number addressing.
/// The test fixture runs in index mode, so these tests verify that Vespa
/// accepts the addressing and the client builds URLs correctly — the
/// streaming-mode routing semantics are not exercised here.
/// </summary>
[Collection("Vespa")]
[Trait("Category", "E2E")]
public class AddressingWriteE2ETests(VespaFixture fixture)
{
    private static bool Enabled => VespaFixture.IntegrationEnabled;
    private static string NewLid() => $"addr-{Guid.NewGuid():N}";

    // ── By Group ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task PutByGroup_ThenGetByGroup_RoundTrips()
    {
        if (!Enabled) return;

        var lid = NewLid();
        var group = $"g-{Guid.NewGuid():N}";

        var put = await fixture.Client.Documents.PutByGroupAsync(
            group, lid,
            new TestProduct { Name = "group-write-1", Price = 9.99, Category = "cat" },
            "product", @namespace: "test");
        Assert.True(put.IsSuccess);

        var fetched = await fixture.Client.Documents.GetByGroupAsync<TestProduct>(
            group, lid, "product", @namespace: "test");
        Assert.NotNull(fetched);
        Assert.Equal("group-write-1", fetched.Fields?.Name);
        Assert.Equal(9.99, fetched.Fields?.Price);
    }

    [Fact]
    public async Task UpdateFieldsByGroup_Increment_Works()
    {
        if (!Enabled) return;

        var lid = NewLid();
        var group = $"g-{Guid.NewGuid():N}";

        await fixture.Client.Documents.PutByGroupAsync(
            group, lid,
            new TestProduct { Name = "count", Price = 0, Category = "c", Quantity = 5 },
            "product", @namespace: "test");

        var upd = await fixture.Client.Documents.UpdateFieldsByGroupAsync(
            group, lid,
            new Dictionary<string, FieldOperation> { ["quantity"] = FieldOp.Increment(3) },
            "product", @namespace: "test");
        Assert.True(upd.IsSuccess);

        var fetched = await fixture.Client.Documents.GetByGroupAsync<TestProduct>(
            group, lid, "product", @namespace: "test");
        Assert.Equal(8, fetched?.Fields?.Quantity);
    }

    [Fact]
    public async Task DeleteByGroup_RemovesDocument()
    {
        if (!Enabled) return;

        var lid = NewLid();
        var group = $"g-{Guid.NewGuid():N}";

        await fixture.Client.Documents.PutByGroupAsync(
            group, lid,
            new TestProduct { Name = "to-delete", Price = 1, Category = "c" },
            "product", @namespace: "test");

        var del = await fixture.Client.Documents.DeleteByGroupAsync(
            group, lid, "product", @namespace: "test");
        Assert.True(del.IsSuccess);

        var fetched = await fixture.Client.Documents.GetByGroupAsync<TestProduct>(
            group, lid, "product", @namespace: "test");
        Assert.Null(fetched);
    }

    // ── By Number ────────────────────────────────────────────────────────────

    [Fact]
    public async Task PutByNumber_ThenGetByNumber_RoundTrips()
    {
        if (!Enabled) return;

        var lid = NewLid();
        // Use a random positive long so concurrent test runs don't alias buckets.
        var bucket = Random.Shared.NextInt64(1, long.MaxValue);

        var put = await fixture.Client.Documents.PutByNumberAsync(
            bucket, lid,
            new TestProduct { Name = "number-write-1", Price = 2.5, Category = "cat" },
            "product", @namespace: "test");
        Assert.True(put.IsSuccess);

        var fetched = await fixture.Client.Documents.GetByNumberAsync<TestProduct>(
            bucket, lid, "product", @namespace: "test");
        Assert.NotNull(fetched);
        Assert.Equal("number-write-1", fetched.Fields?.Name);
    }

    [Fact]
    public async Task UpdateFieldsByNumber_PartialUpdate_ChangesFieldsOnly()
    {
        if (!Enabled) return;

        var lid = NewLid();
        var bucket = Random.Shared.NextInt64(1, long.MaxValue);

        await fixture.Client.Documents.PutByNumberAsync(
            bucket, lid,
            new TestProduct { Name = "orig", Price = 10, Category = "c" },
            "product", @namespace: "test");

        var upd = await fixture.Client.Documents.UpdateFieldsByNumberAsync(
            bucket, lid,
            new Dictionary<string, FieldOperation> { ["price"] = FieldOp.Assign(99.9) },
            "product", @namespace: "test");
        Assert.True(upd.IsSuccess);

        var fetched = await fixture.Client.Documents.GetByNumberAsync<TestProduct>(
            bucket, lid, "product", @namespace: "test");
        Assert.Equal(99.9, fetched?.Fields?.Price);
        Assert.Equal("orig", fetched?.Fields?.Name);
    }

    [Fact]
    public async Task DeleteByNumber_RemovesDocument()
    {
        if (!Enabled) return;

        var lid = NewLid();
        var bucket = Random.Shared.NextInt64(1, long.MaxValue);

        await fixture.Client.Documents.PutByNumberAsync(
            bucket, lid,
            new TestProduct { Name = "doomed", Price = 1, Category = "c" },
            "product", @namespace: "test");

        var del = await fixture.Client.Documents.DeleteByNumberAsync(
            bucket, lid, "product", @namespace: "test");
        Assert.True(del.IsSuccess);

        var fetched = await fixture.Client.Documents.GetByNumberAsync<TestProduct>(
            bucket, lid, "product", @namespace: "test");
        Assert.Null(fetched);
    }
}
