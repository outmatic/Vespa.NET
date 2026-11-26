using System.Globalization;
using Vespa.Documents;
using Vespa.Feed;
using Vespa.IntegrationTests.Fixtures;
using Vespa.Models;
using Vespa.Models.Attributes;
using Xunit;

namespace Vespa.IntegrationTests.E2E;

[Collection("Vespa")]
[Trait("Category", "E2E")]
public sealed class TypedFeedConditionE2ETests(VespaFixture fixture)
{
    [Theory]
    [InlineData("typed", 412)]
    [InlineData("localized", 500)]
    [InlineData("invariant", 412)]
    [InlineData("literal", 412)]
    public async Task PerDocumentThresholds_CheckStoredPricesUnderItalianCulture(string format, int failureStatus)
    {
        if (!VespaFixture.IntegrationEnabled) return;
        var originalCulture = CultureInfo.CurrentCulture;
        var prefix = $"feed-threshold-{Guid.NewGuid():N}";
        string[] ids = [prefix + "-lower", prefix + "-higher"];
        var documents = fixture.Client.Documents;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("it-IT");
            var original = new TestProduct { Name = "Original", Price = 150 };
            var replacement = original with { Name = "Updated", Price = 250 };
            foreach (var id in ids)
                await documents.PutAsync(id, original);

            FeedOperation[] operations = [Create(ids[0], 100.5), Create(ids[1], 200.5)];
            var localized = format == "localized";
            Assert.Contains(localized ? "100,5" : "100.5", operations[0].Condition!);
            Assert.Contains(localized ? "200,5" : "200.5", operations[1].Condition!);

            var result = await fixture.Client.Feed.FeedAsync(operations, new() { MaxConcurrency = 2 });

            Assert.Equal(2, result.TotalOperations);
            Assert.Equal(localized ? 0 : 1, result.SuccessCount);
            Assert.Equal(localized ? 2 : 1, result.FailureCount);
            var errors = result.Errors.OrderBy(error => error.OperationIndex).ToArray();
            Assert.Equal(localized ? new int?[] { 0, 1 } : [1], errors.Select(error => error.OperationIndex));
            Assert.All(errors, error =>
            {
                Assert.Equal(failureStatus, error.StatusCode);
                Assert.Equal(ids[error.OperationIndex!.Value], error.DocumentId);
                if (localized)
                    Assert.Contains("Failed to parse test and set condition", error.Message, StringComparison.Ordinal);
            });

            var lower = await documents.GetAsync<TestProduct>(ids[0]);
            var higher = await documents.GetAsync<TestProduct>(ids[1]);
            Assert.Equal(localized ? "Original" : "Updated", lower!.Fields!.Name);
            Assert.Equal(localized ? 150 : 250, lower.Fields.Price);
            Assert.Equal("Original", higher!.Fields!.Name);
            Assert.Equal(150, higher.Fields.Price);

            FeedOperation Create(string id, double minimum) => format switch
            {
                "typed" => FeedOperation.Put(id, replacement, condition: product => product.Price > minimum),
                "localized" => FeedOperation.Put(id, replacement, rawCondition: $"product.price > {minimum}"),
                "invariant" => FeedOperation.Put(id, replacement,
                    rawCondition: FormattableString.Invariant($"product.price > {minimum}")),
                "literal" => FeedOperation.Put(id, replacement,
                    rawCondition: $"product.price > {DocumentCondition.FormatLiteral(minimum)}"),
                _ => throw new ArgumentOutOfRangeException(nameof(format))
            };
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            foreach (var id in ids)
                await documents.DeleteAsync<TestProduct>(id);
        }
    }

    [Theory]
    [InlineData(FeedOperationKind.Put)]
    [InlineData(FeedOperationKind.Update)]
    public async Task ConditionalCreate_ChecksExistingDocumentsAndReports412WithoutStoppingTheFeed(FeedOperationKind kind)
    {
        if (!VespaFixture.IntegrationEnabled) return;
        var id = $"typed-feed-create-{Guid.NewGuid():N}";
        var documents = fixture.Client.Documents;
        try
        {
            FeedOperation[] operations =
            [
                Create("Original", 10),
                Create("Ready", 100),
                FeedOperation.Update<TestProduct>(id,
                    new Dictionary<string, FieldOperation> { ["price"] = FieldOp.Increment(5.0) },
                    condition: p => p.Name == "Original"),
                FeedOperation.Delete<TestProduct>(id, condition: p => p.Price == 100)
            ];
            var result = await fixture.Client.Feed.FeedAsync(operations, new() { BoundedCapacity = 1 });

            Assert.Equal(4, result.TotalOperations);
            Assert.Equal(2, result.SuccessCount);
            Assert.Equal(2, result.FailureCount);
            Assert.Collection(result.Errors,
                error => AssertFailure(error, 1, kind),
                error => AssertFailure(error, 3, FeedOperationKind.Delete));
            var stored = await documents.GetAsync<TestProduct>(id);
            Assert.Equal("Original", stored!.Fields!.Name);
            Assert.Equal(15.0, stored.Fields.Price);
        }
        finally
        {
            await documents.DeleteAsync<TestProduct>(id);
        }

        FeedOperation Create(string name, double price) => kind == FeedOperationKind.Put
            ? FeedOperation.Put(id, new TestProduct { Name = name, Price = price }, condition: p => p.Name == "Ready", create: true)
            : FeedOperation.Update<TestProduct>(id, update => update
                .Field(p => p.Name, FieldOp.Assign(name))
                .Field(p => p.Price, FieldOp.Assign(price)), condition: p => p.Name == "Ready", createIfMissing: true);

        void AssertFailure(FeedError error, int index, FeedOperationKind expectedKind)
        {
            Assert.Equal(412, error.StatusCode);
            Assert.Equal(index, error.OperationIndex);
            Assert.Equal(expectedKind, error.OperationKind);
            Assert.Equal(id, error.DocumentId);
            Assert.Equal("product", error.DocumentType);
            Assert.Equal("test", error.Namespace);
        }
    }

    [Fact]
    public async Task ExplicitTargets_FreezeConditionsBeforeUpdateConfigurationAndLazyFeedEnumeration()
    {
        if (!VespaFixture.IntegrationEnabled) return;
        var id = $"typed-feed-target-{Guid.NewGuid():N}";
        var otherNamespace = $"other-{Guid.NewGuid():N}";
        var documents = fixture.Client.Documents;
        try
        {
            await documents.PutAsync(id, new TestProduct { Name = "Original", Price = 1 });
            await documents.PutAsync(id, new TestProduct { Name = "Other", Price = 2 }, documentType: "product", @namespace: otherNamespace);

            var expected = "Original";
            var replace = FeedOperation.Put(id, new ProductFields { Label = "Replacement", Price = 5 },
                documentType: "product", condition: p => p.Label == expected);
            expected = "Replacement";
            var updatePrice = FeedOperation.Update<RetargetedFields>(id, update =>
            {
                expected = "Changed during configuration";
                update.Field(p => p.Price, FieldOp.Assign(8.0));
            }, documentType: "product", condition: p => p.Label == expected);
            expected = "Other";
            var deleteOther = FeedOperation.Delete<ProductFields>(id, documentType: "product", @namespace: otherNamespace,
                condition: p => p.Label == expected);

            var result = await fixture.Client.Feed.FeedAsync(DeferredOperations(), new() { BoundedCapacity = 1 });

            Assert.Empty(result.Errors);
            Assert.Equal(3, result.SuccessCount);
            var stored = await documents.GetAsync<TestProduct>(id);
            Assert.Equal($"id:test:product::{id}", stored!.FullId);
            Assert.Equal("Replacement", stored.Fields!.Name);
            Assert.Equal(8.0, stored.Fields.Price);
            Assert.Null(await documents.GetAsync<TestProduct>(id, "product", @namespace: otherNamespace));

            IEnumerable<FeedOperation> DeferredOperations()
            {
                expected = "Changed during enumeration";
                yield return replace;
                yield return updatePrice;
                yield return deleteOther;
            }
        }
        finally
        {
            await documents.DeleteAsync<TestProduct>(id);
            await documents.DeleteAsync(id, "product", @namespace: otherNamespace);
        }
    }

    private class ProductFields
    {
        [VespaField(Name = "product_name")]
        public string Label { get; init; } = "";
        public double Price { get; init; }
    }

    [VespaDocument("event", Namespace = "ignored_namespace")]
    private sealed class RetargetedFields : ProductFields { }
}
