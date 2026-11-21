using System.Linq.Expressions;
using Vespa.Documents;
using Vespa.IntegrationTests.Fixtures;
using Vespa.Models;
using Vespa.Models.Attributes;
using Xunit;

namespace Vespa.IntegrationTests.E2E;

[Collection("Vespa")]
[Trait("Category", "E2E")]
public sealed class TypedDocumentConditionE2ETests(VespaFixture fixture)
{
    [Fact]
    public async Task TypedMutations_CheckStoredValuesAndPreserveFailedWrites()
    {
        if (!VespaFixture.IntegrationEnabled) return;
        var documents = fixture.Client.Documents;
        var id = $"typed-condition-{Guid.NewGuid():N}";
        try
        {
            await documents.PutAsync(id, new TestProduct { Name = "Original", Price = 10, InStock = true });
            // The replacement passes the predicate, but the stored document does not.
            await Assert.ThrowsAsync<VespaConditionNotMetException>(() => documents.PutAsync(id,
                new TestProduct { Name = "Rejected", Price = 100 }, condition: p => p.Price > 50));
            Assert.Equal("Original", (await documents.GetAsync<TestProduct>(id))?.Fields?.Name);

            await documents.PutAsync(id, new TestProduct { Name = "Replacement", Price = 20, InStock = true },
                condition: p => p.Name == "Original" && p.Price == 10 && p.InStock);
            await Assert.ThrowsAsync<VespaConditionNotMetException>(() => documents.UpdateFieldsAsync<TestProduct>(id,
                update => update.Field(p => p.Price, FieldOp.Assign(999)), condition: p => p.Price < 10));
            await documents.UpdateFieldsAsync<TestProduct>(id,
                update => update.Field(p => p.Price, FieldOp.Increment(5)),
                condition: p => (p.Price >= 20 && p.Price < 30) || !p.InStock);
            Assert.Equal(25, (await documents.GetAsync<TestProduct>(id))?.Fields?.Price);

            await Assert.ThrowsAsync<VespaConditionNotMetException>(() => documents.DeleteAsync<TestProduct>(id, condition: p => p.Price != 25));
            await documents.DeleteAsync<TestProduct>(id, condition: p => p.Price == 25);
            Assert.Null(await documents.GetAsync<TestProduct>(id));
        }
        finally
        {
            await documents.DeleteAsync<TestProduct>(id);
        }
    }

    [Fact]
    public async Task TypedStringEquality_RoundTripsEscapesUnicodeAndLiteralWildcards()
    {
        if (!VespaFixture.IntegrationEnabled) return;
        var documents = fixture.Client.Documents;
        var id = $"typed-string-{Guid.NewGuid():N}";
        var name = "café 日本 😀 \"quoted\" \\path\n\r\t*?";
        try
        {
            await documents.PutAsync(id, new TestProduct { Name = name, Price = 10 });
            await Assert.ThrowsAsync<VespaConditionNotMetException>(() => documents.DeleteAsync<TestProduct>(id, condition: p => p.Name == "*"));
            var result = await documents.DeleteAsync<TestProduct>(id, condition: p => p.Name == name);
            Assert.True(result.IsSuccess);
        }
        finally
        {
            await documents.DeleteAsync<TestProduct>(id);
        }
    }

    [Fact]
    public async Task TypedCreate_BypassesConditionOnlyWhenDocumentIsAbsent()
    {
        if (!VespaFixture.IntegrationEnabled) return;
        var documents = fixture.Client.Documents;
        var id = $"typed-create-{Guid.NewGuid():N}";
        try
        {
            var product = new TestProduct { Name = "Created", Price = 10 };
            await documents.PutAsync(id, product, condition: p => p.Price > 100, create: true);
            await Assert.ThrowsAsync<VespaConditionNotMetException>(() => documents.PutAsync(id, product with { Name = "Rejected" },
                condition: p => p.Price > 100, create: true));
            Assert.Equal("Created", (await documents.GetAsync<TestProduct>(id))?.Fields?.Name);
        }
        finally
        {
            await documents.DeleteAsync<TestProduct>(id);
        }
    }

    [Fact]
    public async Task TypedMissingFieldChecks_UseNativeNullSemantics()
    {
        if (!VespaFixture.IntegrationEnabled) return;
        var documents = fixture.Client.Documents;
        var id = $"typed-null-{Guid.NewGuid():N}";
        try
        {
            await documents.PutAsync(id, new TestProduct { Name = "No tensor", Price = 10 });
            await Assert.ThrowsAsync<VespaConditionNotMetException>(() => documents.DeleteAsync<TestProduct>(id,
                condition: p => p.Embedding != null));
            await documents.DeleteAsync<TestProduct>(id, condition: p => p.Embedding == null);
        }
        finally
        {
            await documents.DeleteAsync<TestProduct>(id);
        }
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(3, false)]
    [InlineData(7, true)]
    public async Task NegatedNullableOrdering_HandlesMissingAndPresentFields(int? quantity, bool matches)
    {
        if (!VespaFixture.IntegrationEnabled) return;
        var documents = fixture.Client.Documents;
        var id = $"typed-nullable-{Guid.NewGuid():N}";
        try
        {
            await documents.PutAsync(id, new NullableProduct { Quantity = quantity });
            if (matches)
                await documents.DeleteAsync<NullableProduct>(id, condition: p => !(p.Quantity < 5));
            else
                await Assert.ThrowsAsync<VespaConditionNotMetException>(() => documents.DeleteAsync<NullableProduct>(id,
                    condition: p => !(p.Quantity < 5)));
        }
        finally
        {
            await documents.DeleteAsync<TestProduct>(id);
        }
    }

    [VespaDocument("product", Namespace = "test")]
    private sealed class NullableProduct
    {
        public int? Quantity { get; init; }
    }

    [Fact]
    public async Task DecimalOutsideIntegerRange_IsAcceptedAsANumericCondition()
    {
        if (!VespaFixture.IntegrationEnabled) return;
        var documents = fixture.Client.Documents;
        var id = $"typed-decimal-{Guid.NewGuid():N}";
        try
        {
            await documents.PutAsync(id, new DecimalProduct { Price = decimal.MaxValue });
            await documents.DeleteAsync<DecimalProduct>(id, condition: p => p.Price == decimal.MaxValue);
        }
        finally
        {
            await documents.DeleteAsync<TestProduct>(id);
        }
    }

    [VespaDocument("product", Namespace = "test")]
    private sealed class DecimalProduct
    {
        public decimal Price { get; init; }
    }

    [Theory]
    [InlineData(1e-12)]
    [InlineData(1e100)]
    public async Task ScientificNotation_RoundTripsThroughNativeConditions(double price)
    {
        if (!VespaFixture.IntegrationEnabled) return;
        var documents = fixture.Client.Documents;
        var id = $"typed-exponent-{Guid.NewGuid():N}";
        try
        {
            await documents.PutAsync(id, new TestProduct { Price = price });
            await documents.DeleteAsync<TestProduct>(id, condition: p => p.Price == price);
        }
        finally
        {
            await documents.DeleteAsync<TestProduct>(id);
        }
    }

    [Fact]
    public async Task NativeFloatEquality_PreservesTheStoredBinaryValue()
    {
        if (!VespaFixture.IntegrationEnabled) return;
        var documents = fixture.Client.Documents;
        var id = $"typed-float-{Guid.NewGuid():N}";
        try
        {
            await documents.PutAsync(id, new TestProduct { SingleValue = 0.1f });
            await Assert.ThrowsAsync<VespaConditionNotMetException>(() => documents.DeleteAsync<TestProduct>(id,
                condition: p => !(p.SingleValue == 0.1f)));
            await documents.DeleteAsync<TestProduct>(id, condition: p => p.SingleValue == 0.1f);
        }
        finally
        {
            await documents.DeleteAsync<TestProduct>(id);
        }
    }

    [Fact]
    public async Task MinimumIntegerLiteral_KeepsTheComparisonExact()
    {
        if (!VespaFixture.IntegrationEnabled) return;
        var documents = fixture.Client.Documents;
        var id = $"typed-integer-{Guid.NewGuid():N}";
        try
        {
            await documents.PutAsync(id, new BaseItem { BaseScore = long.MinValue + 1 });
            await documents.DeleteAsync<BaseItem>(id, condition: p => p.BaseScore > long.MinValue);
        }
        finally
        {
            await documents.DeleteAsync<BaseItem>(id);
        }
    }

    [Fact]
    public async Task CapturedNullableLimit_CanChangeBetweenRequests()
    {
        if (!VespaFixture.IntegrationEnabled) return;
        var documents = fixture.Client.Documents;
        var id = $"typed-limit-{Guid.NewGuid():N}";
        int? limit = 5;
        Expression<Func<NullableProduct, bool>> condition = p => !(p.Quantity < limit);
        try
        {
            await documents.PutAsync(id, new NullableProduct { Quantity = 3 });
            await Assert.ThrowsAsync<VespaConditionNotMetException>(() => documents.DeleteAsync(id, condition: condition));
            limit = null;
            await documents.DeleteAsync(id, condition: condition);
        }
        finally
        {
            await documents.DeleteAsync<TestProduct>(id);
        }
    }

    [Fact]
    public async Task DocumentIdentity_IsMetadataAndNativeIdSelectionStillWorks()
    {
        if (!VespaFixture.IntegrationEnabled) return;
        var documents = fixture.Client.Documents;
        var id = $"typed-identity-{Guid.NewGuid():N}";
        try
        {
            await documents.PutAsync(id, new IdentityProduct { Id = id, Name = "Stored" });
            await Assert.ThrowsAsync<NotSupportedException>(() => documents.DeleteAsync<IdentityProduct>(id,
                condition: p => p.Id == id));
            Assert.Equal(id, (await documents.GetAsync<IdentityProduct>(id))?.Fields?.Id);
            await documents.DeleteAsync<IdentityProduct>(id, rawCondition: $"id.specific == \"{id}\"");
            Assert.Null(await documents.GetAsync<IdentityProduct>(id));
        }
        finally
        {
            await documents.DeleteAsync<TestProduct>(id);
        }
    }

    [VespaDocument("product", Namespace = "test")]
    private sealed class IdentityProduct
    {
        [VespaId]
        public string Id { get; init; } = "";
        [VespaField(Name = "product_name")]
        public string Name { get; init; } = "";
    }
}
