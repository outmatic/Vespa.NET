using Vespa.Documents;
using Vespa.IntegrationTests.Fixtures;
using Vespa.Models;
using Xunit;

namespace Vespa.IntegrationTests.E2E;

[Collection("Vespa")]
[Trait("Category", "E2E")]
public class CrudE2ETests(VespaFixture fixture)
{
    private static bool Enabled => VespaFixture.IntegrationEnabled;

    private static string NewId() => $"e2e-{Guid.NewGuid():N}";

    // ── Basic CRUD ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Put_Get_Delete_RoundTrip()
    {
        if (!Enabled) return;

        var id = NewId();
        var product = new TestProduct { Name = "E2E Laptop", Price = 999.99, Category = "electronics", InStock = true };

        var put = await fixture.Client.Documents.PutAsync(id, product);
        Assert.True(put.IsSuccess);

        await Task.Delay(500);

        var get = await fixture.Client.Documents.GetAsync<TestProduct>(id);
        Assert.NotNull(get);
        Assert.Equal("E2E Laptop", get.Fields?.Name);
        Assert.Equal(999.99, get.Fields?.Price);

        var del = await fixture.Client.Documents.DeleteAsync<TestProduct>(id);
        Assert.True(del.IsSuccess);

        await Task.Delay(300);

        var gone = await fixture.Client.Documents.GetAsync<TestProduct>(id);
        Assert.Null(gone);
    }

    [Fact]
    public async Task Get_NonExistentDocument_ReturnsNull()
    {
        if (!Enabled) return;

        var result = await fixture.Client.Documents.GetAsync<TestProduct>("nonexistent-id-xyz", "product", "test");
        Assert.Null(result);
    }

    // ── Partial Update ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Update_Partial_ChangesFieldsOnly()
    {
        if (!Enabled) return;

        var id = NewId();
        await fixture.Client.Documents.PutAsync(id, new TestProduct { Name = "Old Name", Price = 10.0, Category = "test" });
        await Task.Delay(500);

        await fixture.Client.Documents.UpdateFieldsAsync<TestProduct>(id,
            new Dictionary<string, FieldOperation>
            {
                ["price"] = FieldOp.Assign(25.0)
            });
        await Task.Delay(300);

        var updated = await fixture.Client.Documents.GetAsync<TestProduct>(id);
        Assert.Equal(25.0, updated?.Fields?.Price);
        Assert.Equal("Old Name", updated?.Fields?.Name);

        await fixture.Client.Documents.DeleteAsync<TestProduct>(id);
    }

    [Fact]
    public async Task Update_CreateIfMissing_CreatesNewDocument()
    {
        if (!Enabled) return;

        var id = NewId();
        // Document does not exist yet — createIfMissing should create it
        await fixture.Client.Documents.UpdateFieldsAsync(
            id,
            new Dictionary<string, FieldOperation>
            {
                ["product_name"] = FieldOp.Assign("Created via update"),
                ["price"] = FieldOp.Assign(42.0),
                ["category"] = FieldOp.Assign("upsert")
            },
            documentType: "product", @namespace: "test", createIfMissing: true);
        await Task.Delay(500);

        var doc = await fixture.Client.Documents.GetAsync<TestProduct>(id);
        Assert.NotNull(doc);
        Assert.Equal("Created via update", doc.Fields?.Name);
        Assert.Equal(42.0, doc.Fields?.Price);

        await fixture.Client.Documents.DeleteAsync<TestProduct>(id);
    }

    // ── Field Operations ────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateFields_Increment_WorksCorrectly()
    {
        if (!Enabled) return;

        var id = NewId();
        await fixture.Client.Documents.PutAsync(id, new TestProduct { Name = "Counter", Price = 1.0, Category = "test" });
        await Task.Delay(500);

        await fixture.Client.Documents.UpdateFieldsAsync<TestProduct>(id,
            new Dictionary<string, FieldOperation>
            {
                ["price"] = FieldOp.Increment(9.0)
            });
        await Task.Delay(300);

        var doc = await fixture.Client.Documents.GetAsync<TestProduct>(id);
        Assert.Equal(10.0, doc?.Fields?.Price);

        await fixture.Client.Documents.DeleteAsync<TestProduct>(id);
    }

    [Fact]
    public async Task UpdateFields_Assign_OverwritesValue()
    {
        if (!Enabled) return;

        var id = NewId();
        await fixture.Client.Documents.PutAsync(id, new TestProduct { Name = "Original", Price = 50.0, Category = "test" });
        await Task.Delay(500);

        await fixture.Client.Documents.UpdateFieldsAsync<TestProduct>(id,
            new Dictionary<string, FieldOperation>
            {
                ["product_name"] = FieldOp.Assign("Renamed"),
                ["price"] = FieldOp.Assign(99.0)
            });
        await Task.Delay(300);

        var doc = await fixture.Client.Documents.GetAsync<TestProduct>(id);
        Assert.Equal("Renamed", doc?.Fields?.Name);
        Assert.Equal(99.0, doc?.Fields?.Price);

        await fixture.Client.Documents.DeleteAsync<TestProduct>(id);
    }

    [Fact]
    public async Task UpdateFields_Multiply_WorksCorrectly()
    {
        if (!Enabled) return;

        var id = NewId();
        await fixture.Client.Documents.PutAsync(id, new TestProduct { Name = "Mul", Price = 10.0, Category = "test" });
        await Task.Delay(500);

        await fixture.Client.Documents.UpdateFieldsAsync<TestProduct>(id,
            new Dictionary<string, FieldOperation>
            {
                ["price"] = FieldOp.Multiply(3.0)
            });
        await Task.Delay(300);

        var doc = await fixture.Client.Documents.GetAsync<TestProduct>(id);
        Assert.Equal(30.0, doc?.Fields?.Price);

        await fixture.Client.Documents.DeleteAsync<TestProduct>(id);
    }

    [Fact]
    public async Task UpdateFields_Decrement_WorksCorrectly()
    {
        if (!Enabled) return;

        var id = NewId();
        await fixture.Client.Documents.PutAsync(id, new TestProduct { Name = "Dec", Price = 100.0, Category = "test" });
        await Task.Delay(500);

        await fixture.Client.Documents.UpdateFieldsAsync<TestProduct>(id,
            new Dictionary<string, FieldOperation>
            {
                ["price"] = FieldOp.Decrement(25.0)
            });
        await Task.Delay(300);

        var doc = await fixture.Client.Documents.GetAsync<TestProduct>(id);
        Assert.Equal(75.0, doc?.Fields?.Price);

        await fixture.Client.Documents.DeleteAsync<TestProduct>(id);
    }

    [Fact]
    public async Task UpdateFields_Divide_WorksCorrectly()
    {
        if (!Enabled) return;

        var id = NewId();
        await fixture.Client.Documents.PutAsync(id, new TestProduct { Name = "Div", Price = 90.0, Category = "test" });
        await Task.Delay(500);

        await fixture.Client.Documents.UpdateFieldsAsync<TestProduct>(id,
            new Dictionary<string, FieldOperation>
            {
                ["price"] = FieldOp.Divide(3.0)
            });
        await Task.Delay(300);

        var doc = await fixture.Client.Documents.GetAsync<TestProduct>(id);
        Assert.Equal(30.0, doc?.Fields?.Price);

        await fixture.Client.Documents.DeleteAsync<TestProduct>(id);
    }

    [Fact]
    public async Task UpdateFields_TypedBuilder_WorksCorrectly()
    {
        if (!Enabled) return;

        var id = NewId();
        await fixture.Client.Documents.PutAsync(id, new TestProduct { Name = "Typed", Price = 10.0, Category = "test" });
        await Task.Delay(500);

        await fixture.Client.Documents.UpdateFieldsAsync<TestProduct>(id, ops => ops
            .Field(p => p.Name, FieldOp.Assign("TypedUpdate"))
            .Field(p => p.Price, FieldOp.Increment(5.0)));
        await Task.Delay(300);

        var doc = await fixture.Client.Documents.GetAsync<TestProduct>(id);
        Assert.Equal("TypedUpdate", doc?.Fields?.Name);
        Assert.Equal(15.0, doc?.Fields?.Price);

        await fixture.Client.Documents.DeleteAsync<TestProduct>(id);
    }

    // ── Conditional Writes ──────────────────────────────────────────────────────

    [Fact]
    public async Task ConditionalWrite_FailsWhenConditionNotMet()
    {
        if (!Enabled) return;

        var id = NewId();
        await fixture.Client.Documents.PutAsync(id, new TestProduct { Name = "Item", Price = 5.0, Category = "test" });
        await Task.Delay(500);

        var ex = await Assert.ThrowsAnyAsync<VespaException>(async () =>
            await fixture.Client.Documents.UpdateFieldsAsync(id,
                new Dictionary<string, FieldOperation> { ["price"] = FieldOp.Assign(99.0) },
                documentType: "product", @namespace: "test",
                condition: "product.price > 100"));
        Assert.IsType<VespaConditionNotMetException>(ex);

        await fixture.Client.Documents.DeleteAsync<TestProduct>(id);
    }

    [Fact]
    public async Task ConditionalWrite_SucceedsWhenConditionMet()
    {
        if (!Enabled) return;

        var id = NewId();
        await fixture.Client.Documents.PutAsync(id, new TestProduct { Name = "Cond", Price = 200.0, Category = "test" });
        await Task.Delay(500);

        // Condition met: price > 100
        var result = await fixture.Client.Documents.UpdateFieldsAsync(id,
            new Dictionary<string, FieldOperation> { ["price"] = FieldOp.Assign(300.0) },
            documentType: "product", @namespace: "test",
            condition: "product.price > 100");
        Assert.True(result.IsSuccess);

        var doc = await fixture.Client.Documents.GetAsync<TestProduct>(id);
        Assert.Equal(300.0, doc?.Fields?.Price);

        await fixture.Client.Documents.DeleteAsync<TestProduct>(id);
    }

    [Fact]
    public async Task ConditionalDelete_FailsWhenConditionNotMet()
    {
        if (!Enabled) return;

        var id = NewId();
        await fixture.Client.Documents.PutAsync(id, new TestProduct { Name = "NoDel", Price = 5.0, Category = "test" });
        await Task.Delay(500);

        var ex = await Assert.ThrowsAnyAsync<VespaException>(async () =>
            await fixture.Client.Documents.DeleteAsync(id, "product", "test",
                condition: "product.price > 100"));
        Assert.IsType<VespaConditionNotMetException>(ex);

        // Document should still exist
        var doc = await fixture.Client.Documents.GetAsync<TestProduct>(id);
        Assert.NotNull(doc);

        await fixture.Client.Documents.DeleteAsync<TestProduct>(id);
    }

    // ── Put overwrites ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Put_Overwrite_ReplacesEntireDocument()
    {
        if (!Enabled) return;

        var id = NewId();
        await fixture.Client.Documents.PutAsync(id, new TestProduct { Name = "V1", Price = 10.0, Category = "old" });
        await Task.Delay(500);

        await fixture.Client.Documents.PutAsync(id, new TestProduct { Name = "V2", Price = 20.0, Category = "new" });
        await Task.Delay(500);

        var doc = await fixture.Client.Documents.GetAsync<TestProduct>(id);
        Assert.Equal("V2", doc?.Fields?.Name);
        Assert.Equal(20.0, doc?.Fields?.Price);
        Assert.Equal("new", doc?.Fields?.Category);

        await fixture.Client.Documents.DeleteAsync<TestProduct>(id);
    }
}
