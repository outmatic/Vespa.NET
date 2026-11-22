using System.Linq.Expressions;
using System.Net;
using System.Text.Json;
using Vespa.Documents;
using Vespa.Feed;
using Vespa.Models;
using Vespa.Models.Attributes;
using Vespa.Tests.Helpers;
using Xunit;

namespace Vespa.Tests.Unit;

public sealed class TypedBatchFeedTests : IDisposable
{
    private readonly BatchHandler _handler = new();
    private readonly HttpClient _http;
    private readonly FeedOperations _feed;

    public TypedBatchFeedTests()
    {
        _http = new(_handler) { BaseAddress = new Uri("http://localhost:8080") };
        var options = new VespaClientOptions
        {
            DefaultNamespace = "fallback",
            EnableRetry = true,
            MaxRetryAttempts = 1,
            FeedRetryBaseDelay = TimeSpan.FromMilliseconds(1)
        };
        _feed = new(new DocumentOperations(_http, options), options);
    }

    public void Dispose() => _http.Dispose();

    [Fact]
    public async Task FactoryInfersAnonymousPayloadForAnExplicitSchema()
    {
        var row = FeedDocument.Create("row", new { UnitPrice = 8 }, documentType: "legacy",
            condition: p => p.UnitPrice > 5);
        Queue(HttpStatusCode.OK);

        var result = await _feed.BulkPutAsync([row], "legacy");

        Assert.Equal(1, result.SuccessCount);
        var request = Assert.Single(_handler.Requests);
        Assert.Equal("/document/v1/fallback/legacy/docid/row", request.RequestUri!.AbsolutePath);
        Assert.Equal("(legacy.unit_price > 5)", Query(request)["condition"]);
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
        Assert.Equal(8, body.RootElement.GetProperty("fields").GetProperty("unit_price").GetInt32());
    }

    [Fact]
    public void TypedFactories_RenderAgainstTheInferredOrExplicitSchema()
    {
        var put = FeedDocument.Create<Inventory>("p", new Inventory(), condition: p => p.Price > 5);
        var mapped = BulkFieldUpdate.Create<Inventory>("u", CreateFields(), documentType: "alternate", condition: p => p.Price > 5);
        var poco = BulkFieldUpdate.Create<PlainModel>("b", b => b.Field(p => p.Price, FieldOp.Assign(12)),
            documentType: "legacy", condition: p => p.Price > 5);
        var explicitPut = FeedDocument.Create<PlainModel>("p", new(), "legacy", p => p.Price > 5);

        Assert.Equal("inventory", put.ExpectedDocumentType);
        Assert.Equal("(inventory.unit_price > 5)", put.Condition);
        Assert.Equal("alternate", mapped.ExpectedDocumentType);
        Assert.Equal("(alternate.unit_price > 5)", mapped.Condition);
        Assert.Equal("legacy", poco.ExpectedDocumentType);
        Assert.Equal("(legacy.unit_price > 5)", poco.Condition);
        Assert.Equal(poco.Condition, explicitPut.Condition);
        Assert.Equal("legacy", explicitPut.ExpectedDocumentType);
        Assert.Contains("unit_price", poco.FieldOperations);
        Assert.Throws<InvalidOperationException>(() => FeedDocument.Create<PlainModel>("p", new(), condition: p => true));
        Assert.Throws<InvalidOperationException>(() => BulkFieldUpdate.Create<PlainModel>("u", CreateFields(), condition: p => true));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" id.user == 42 ")]
    public void RawOrUnconditionalEntriesBindOnlyWhenTheCallerSpecifiesATarget(string? raw)
    {
        var put = FeedDocument.Create<PlainModel>("p", new(), rawCondition: raw);
        var update = BulkFieldUpdate.Create<PlainModel>("u", CreateFields(), rawCondition: raw);
        var boundPut = FeedDocument.Create<PlainModel>("p", new(), documentType: "archive", rawCondition: raw);
        var boundUpdate = BulkFieldUpdate.Create<PlainModel>("u", b => b.Field(p => p.Price, FieldOp.Assign(12)),
            documentType: "archive", rawCondition: raw);
        Assert.Null(put.ExpectedDocumentType);
        Assert.Null(update.ExpectedDocumentType);
        Assert.Equal("archive", boundPut.ExpectedDocumentType);
        Assert.Equal("archive", boundUpdate.ExpectedDocumentType);
        Assert.All(new[] { put.Condition, update.Condition, boundPut.Condition, boundUpdate.Condition }, value => Assert.Equal(raw, value));
    }

    [Theory]
    [InlineData("put")]
    [InlineData("dictionary")]
    [InlineData("builder")]
    public void InvalidConditionsFailBeforeFieldConfiguration(string factory)
    {
        var configured = false;
        Action<TypedFieldUpdateBuilder<Inventory>> configure = _ => configured = true;
        object Make(Expression<Func<Inventory, bool>> predicate, string? raw) => factory switch
        {
            "put" => FeedDocument.Create<Inventory>("row", new(), condition: predicate, rawCondition: raw),
            "dictionary" => BulkFieldUpdate.Create<Inventory>("row", CreateFields(), condition: predicate, rawCondition: raw),
            "builder" => BulkFieldUpdate.Create<Inventory>("row", configure, condition: predicate, rawCondition: raw),
            _ => throw new ArgumentOutOfRangeException(nameof(factory))
        };
        Assert.Throws<ArgumentException>(() => Make(p => true, ""));
        Assert.Throws<NotSupportedException>(() => Make(p => Math.Abs(p.Price) > 1, null));
        Assert.False(configured);
    }

    [Theory]
    [InlineData("put", null)]
    [InlineData("put", "replacement")]
    [InlineData("update", null)]
    [InlineData("update", "replacement")]
    [InlineData("stream", null)]
    [InlineData("stream", "replacement")]
    public async Task WrongTargetFailsOnlyThatEntryEvenWhenItsCloneReplacesTheCondition(string mode, string? replacement)
    {
        Queue(HttpStatusCode.OK, HttpStatusCode.OK);
        List<FeedProgress> progress = [];
        FeedResult result;
        if (mode == "update")
        {
            var good = BulkFieldUpdate.Create<Inventory>("before", CreateFields(), condition: p => true);
            var wrong = BulkFieldUpdate.Create<Inventory>("original", CreateFields(), "Inventory", p => true);
            result = await _feed.BulkUpdateAsync([good, wrong with { Id = "bad", Condition = replacement }, good with { Id = "after" }],
                "inventory", "other-tenant", maxConcurrency: 1);
        }
        else
        {
            var good = FeedDocument.Create<Inventory>("before", new(), condition: p => true);
            var wrong = FeedDocument.Create<Inventory>("original", new(), "Inventory", p => true);
            FeedDocument<Inventory>[] rows = [good, wrong with { Id = "bad", Condition = replacement }, good with { Id = "after" }];
            result = mode == "put"
                ? await _feed.BulkPutAsync(rows, "inventory", "other-tenant", maxConcurrency: 1)
                : await _feed.FeedAsync(Stream(rows), "inventory", "other-tenant", maxConcurrency: 1, boundedCapacity: 1, onProgress: progress.Add);
        }

        Assert.Equal(3, result.TotalDocuments);
        Assert.Equal(2, result.SuccessCount);
        Assert.Equal(1, result.FailureCount);
        var error = Assert.Single(result.Errors);
        Assert.Equal("bad", error.DocumentId);
        Assert.Null(error.StatusCode);
        Assert.Contains("inventory", error.Message, StringComparison.Ordinal);
        Assert.Contains("Inventory", error.Message, StringComparison.Ordinal);
        Assert.Equal(new[] { "before", "after" }.Select(id => "/document/v1/other-tenant/inventory/docid/" + id),
            _handler.Requests.Select(request => request.RequestUri!.AbsolutePath));
        if (mode == "stream")
        {
            Assert.Equal(new[] { "before", "bad", "after" }, progress.Select(item => item.LastDocumentId));
            Assert.Equal(3, progress[^1].ProcessedCount);
            Assert.Equal(2, progress[^1].SuccessCount);
            Assert.Equal(1, progress[^1].FailureCount);
        }
    }

    [Fact]
    public void RecordEqualityDistinguishesBindingsWithoutPrintingInternalMetadata()
    {
        var model = new PlainModel();
        var put = FeedDocument.Create<PlainModel>("row", model, "first");
        var otherPut = FeedDocument.Create<PlainModel>("row", model, "second");
        Assert.Equal(put, put with { Condition = null });
        Assert.NotEqual(put, otherPut);
        Assert.Equal(put.ToString(), otherPut.ToString());
        Assert.DoesNotContain(nameof(FeedDocument<PlainModel>.ExpectedDocumentType), put.ToString());

        var update = BulkFieldUpdate.Create<PlainModel>("row", CreateFields(), "first");
        var otherUpdate = BulkFieldUpdate.Create<PlainModel>("row", CreateFields(), "second") with { FieldOperations = update.FieldOperations };
        Assert.Equal(update, update with { Condition = null });
        Assert.NotEqual(update, otherUpdate);
        Assert.Equal(update.ToString(), otherUpdate.ToString());
        Assert.DoesNotContain(nameof(BulkFieldUpdate.ExpectedDocumentType), update.ToString());
    }

    [Fact]
    public async Task JsonPreservesLegacyShapeAndReplayUsesTheRawCondition()
    {
        var typed = FeedDocument.Create<Inventory>("row", new() { Price = 8 }, condition: p => p.Price > 5);
        var legacy = new FeedDocument<Inventory> { Id = typed.Id, Fields = typed.Fields, Condition = typed.Condition };
        var json = JsonSerializer.Serialize(typed, VespaJsonOptions.Default);
        Assert.Equal(JsonSerializer.Serialize(legacy, VespaJsonOptions.Default), json);
        using var body = JsonDocument.Parse(json);
        Assert.Equal(new[] { "condition", "fields", "id" }, body.RootElement.EnumerateObject().Select(p => p.Name).Order());
        var replay = JsonSerializer.Deserialize<FeedDocument<Inventory>>(json, VespaJsonOptions.Default)!;
        Assert.Null(replay.ExpectedDocumentType);
        Assert.Equal(8, replay.Fields.Price);
        Assert.Equal(typed.Condition, replay.Condition);

        var update = BulkFieldUpdate.Create<Inventory>("row", CreateFields(), condition: p => true);
        var legacyUpdate = new BulkFieldUpdate { Id = update.Id, FieldOperations = update.FieldOperations, Condition = "true" };
        Assert.Equal(JsonSerializer.Serialize(legacyUpdate, VespaJsonOptions.Default), JsonSerializer.Serialize(update, VespaJsonOptions.Default));
        // FieldOperation supports writing only. An empty dictionary permits a round trip of the enclosing DTO.
        var empty = update with { FieldOperations = [] };
        var restored = JsonSerializer.Deserialize<BulkFieldUpdate>(JsonSerializer.Serialize(empty, VespaJsonOptions.Default), VespaJsonOptions.Default)!;
        Assert.Equal("true", restored.Condition);
        Assert.Null(restored.ExpectedDocumentType);

        Queue(HttpStatusCode.OK);
        var result = await _feed.BulkPutAsync([replay], "archive");
        Assert.Equal(1, result.SuccessCount);
        var request = Assert.Single(_handler.Requests);
        Assert.Equal("/document/v1/fallback/archive/docid/row", request.RequestUri!.AbsolutePath);
        Assert.Equal(typed.Condition, Query(request)["condition"]);
    }

    [Fact]
    public async Task UpdateFactoriesSnapshotFieldsAndFreezeTypedConditionBeforeConfigureAndRetry()
    {
        var minimum = 10;
        var fields = CreateFields();
        var dictionaryRow = BulkFieldUpdate.Create<Inventory>("dictionary", fields, condition: p => p.Price > minimum);
        TypedFieldUpdateBuilder<Inventory>? captured = null;
        var callbacks = 0;
        var builderRow = BulkFieldUpdate.Create<Inventory>("builder", b =>
        {
            captured = b;
            minimum = 20;
            callbacks++;
            b.Field(p => p.Price, FieldOp.Assign(12));
        }, condition: p => p.Price > minimum);
        fields["unit_price"] = FieldOp.Assign(99);
        captured!.Field(p => p.Price, FieldOp.Assign(101));
        minimum = 30;
        _handler.AfterFirstAttempt = () => minimum = 40;
        Queue(HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK, HttpStatusCode.OK);

        var result = await _feed.BulkUpdateAsync([builderRow, dictionaryRow], "inventory", createIfMissing: true, maxConcurrency: 1);

        Assert.Equal(2, result.SuccessCount);
        Assert.Equal(1, callbacks);
        Assert.Equal(40, minimum);
        var requests = _handler.Requests;
        Assert.Equal(new[] { "builder", "builder", "dictionary" }, requests.Select(r => r.RequestUri!.Segments[^1]));
        foreach (var request in requests)
        {
            Assert.Equal("(inventory.unit_price > 10)", Query(request)["condition"]);
            Assert.Equal("true", Query(request)["create"]);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal(12, body.RootElement.GetProperty("fields").GetProperty("unit_price").GetProperty("assign").GetInt32());
        }
    }

    private static Dictionary<string, FieldOperation> CreateFields() => new() { ["unit_price"] = FieldOp.Assign(12) };

    private static async IAsyncEnumerable<FeedDocument<Inventory>> Stream(IEnumerable<FeedDocument<Inventory>> rows)
    {
        foreach (var row in rows)
        {
            await Task.Yield();
            yield return row;
        }
    }

    private void Queue(params HttpStatusCode[] statuses)
    {
        foreach (var status in statuses)
            _handler.QueueResponse(new(status) { Content = new StringContent("{}") });
    }

    private static Dictionary<string, string> Query(HttpRequestMessage request) =>
        request.RequestUri!.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .ToDictionary(pair => pair[0], pair => Uri.UnescapeDataString(pair[1]));

    private sealed class BatchHandler : MockHttpMessageHandler
    {
        internal Action? AfterFirstAttempt { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (Requests.Count == 1)
                AfterFirstAttempt?.Invoke();
            return response;
        }
    }

    private class PlainModel
    {
        [VespaField(Name = "unit_price")]
        public int Price { get; init; }
    }

    [VespaDocument("inventory", Namespace = "tenant")]
    private sealed class Inventory : PlainModel { }
}
