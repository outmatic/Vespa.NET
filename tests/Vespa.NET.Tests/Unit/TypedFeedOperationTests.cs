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

public sealed class TypedFeedOperationTests : IDisposable
{
    private readonly FeedHandler _handler = new();
    private readonly HttpClient _http;
    private readonly FeedOperations _feed;

    public TypedFeedOperationTests()
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

    public static TheoryData<string> Factories => new() { "put", "dictionary", "builder", "delete" };

    [Theory]
    [MemberData(nameof(Factories))]
    public void Factories_QualifyPredicatesWithTheResolvedTarget(string factory)
    {
        var inferred = Create(factory, new Inventory(), condition: p => p.Price > 5);
        AssertTarget(inferred, "inventory", "tenant");

        var namespaceOverride = Create(factory, new Inventory(), @namespace: "custom", condition: p => p.Price > 5);
        AssertTarget(namespaceOverride, "inventory", "custom");

        var typeOverride = Create(factory, new Inventory(), documentType: "alternate", condition: p => p.Price > 5);
        AssertTarget(typeOverride, "alternate", null);

        var undecorated = Create(factory, new PriceFields(), documentType: "legacy", condition: p => p.Price > 5);
        AssertTarget(undecorated, "legacy", null);

        static void AssertTarget(FeedOperation operation, string type, string? ns)
        {
            Assert.Equal(type, operation.DocumentType);
            Assert.Equal(ns, operation.Namespace);
            Assert.Equal($"({type}.unit_price > 5)", operation.Condition);
        }
    }

    [Theory]
    [MemberData(nameof(Factories))]
    public async Task ExplicitPocoTarget_ForwardsConditionsCreationOptionsAndBodies(string factory)
    {
        const string raw = " (legacy.unit_price > 5) ";
        var model = new PriceFields { Price = 17 };
        var options = new DocumentRequestOptions { Timeout = TimeSpan.FromSeconds(2), Route = "default/chain.indexing", DryRun = true };
        var typed = Create(factory, model, documentType: "legacy", condition: p => p.Price > 5, create: true, options: options);
        var native = Create(factory, model, documentType: "legacy", rawCondition: raw, create: true, options: options);
        Assert.Same(options, typed.RequestOptions);
        Queue(HttpStatusCode.OK, HttpStatusCode.OK);

        var result = await _feed.FeedAsync([typed, native]);

        Assert.Equal(2, result.SuccessCount);
        var requests = _handler.Requests;
        Assert.Equal(2, requests.Count);
        Assert.Equal("(legacy.unit_price > 5)", Query(requests[0])["condition"]);
        Assert.Equal(raw, Query(requests[1])["condition"]);
        foreach (var request in requests)
        {
            Assert.Equal("/document/v1/fallback/legacy/docid/item", request.RequestUri!.AbsolutePath);
            var query = Query(request);
            Assert.Equal("2000ms", query["timeout"]);
            Assert.Equal("default/chain.indexing", query["route"]);
            Assert.Equal("true", query["dryRun"]);
            if (factory == "delete")
            {
                Assert.Equal(HttpMethod.Delete, request.Method);
                Assert.DoesNotContain("create", query);
                Assert.Null(request.Content);
                continue;
            }

            Assert.Equal("true", query["create"]);
            Assert.Equal(factory == "put" ? HttpMethod.Post : HttpMethod.Put, request.Method);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var price = body.RootElement.GetProperty("fields").GetProperty("unit_price");
            Assert.Equal(17, factory == "put" ? price.GetInt32() : price.GetProperty("assign").GetInt32());
        }
    }

    [Theory]
    [MemberData(nameof(Factories))]
    public void InvalidConditions_FailAtConstructionBeforeConfigure(string factory)
    {
        var callbacks = 0;
        Action<TypedFieldUpdateBuilder<Inventory>> configure = _ => callbacks++;
        var model = new Inventory();
        Assert.Throws<ArgumentException>(() => Create(factory, model, condition: p => true, rawCondition: "true", configure: configure));
        Assert.Throws<ArgumentException>(() => Create(factory, model, condition: p => true, rawCondition: "", configure: configure));
        Assert.Throws<NotSupportedException>(() => Create(factory, model, condition: p => Math.Abs(p.Price) > 5, configure: configure));
        Assert.Equal(0, callbacks);
        Assert.Empty(_handler.Requests);
    }

    [Theory]
    [MemberData(nameof(Factories))]
    public async Task MissingConditionOmitsParameterAndEmptyRawConditionSurvives(string factory)
    {
        var model = new Inventory();
        var unconditional = Create(factory, model);
        var empty = Create(factory, model, rawCondition: "");
        Assert.Null(unconditional.Condition);
        Assert.Equal("", empty.Condition);
        Assert.Equal("true", Create(factory, model, condition: p => true).Condition);
        Queue(HttpStatusCode.OK, HttpStatusCode.OK);

        var result = await _feed.FeedAsync([unconditional, empty]);

        Assert.Equal(2, result.SuccessCount);
        Assert.Collection(_handler.Requests,
            request => Assert.DoesNotContain("condition", Query(request)),
            request => Assert.Equal("", Query(request)["condition"]));
    }

    [Fact]
    public void NullAndDefaultConditionArgumentsAreUnconditionalOnAllFactoryShapes()
    {
        FeedOperation[] operations =
        [
            FeedOperation.Put("item", new Inventory(), condition: default),
            FeedOperation.Update<Inventory>("item", new Dictionary<string, FieldOperation>(), condition: null, rawCondition: default),
            FeedOperation.Update<Inventory>("item", _ => { }, condition: default, rawCondition: null),
            FeedOperation.Delete<Inventory>("item", condition: null)
        ];
        Assert.All(operations, operation => Assert.Null(operation.Condition));
    }

    [Fact]
    public async Task Builder_FreezesConditionBeforeConfigureAndReusesItOnRetry()
    {
        var minimum = 10;
        var callbacks = 0;
        var operation = FeedOperation.Update<Inventory>("item", builder =>
        {
            callbacks++;
            minimum = 20;
            builder.Field(p => p.Price, FieldOp.Assign(17));
        }, condition: p => p.Price > minimum);
        Assert.Equal("(inventory.unit_price > 10)", operation.Condition);
        minimum = 30;
        _handler.AfterFirstAttempt = () => minimum = 40;
        Queue(HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK);

        var result = await _feed.FeedAsync([operation]);

        Assert.Equal(1, result.TotalOperations);
        Assert.Equal(1, result.SuccessCount);
        Assert.Equal(1, callbacks);
        Assert.Equal(40, minimum);
        var requests = _handler.Requests;
        Assert.Equal(2, requests.Count);
        Assert.All(requests, request => Assert.Equal("(inventory.unit_price > 10)", Query(request)["condition"]));
    }

    [Fact]
    public async Task NativeExplicitFactoriesKeepTheirStringSlotAndUpdatesCopyTheInputDictionary()
    {
        const string raw = "id.user == 42";
        Dictionary<string, FieldOperation> fields = new() { ["unit_price"] = FieldOp.Assign(17) };
        FeedOperation[] operations =
        [
            FeedOperation.Update<Inventory>("item", fields, documentType: "legacy", condition: p => p.Price > 5),
            FeedOperation.Update("item", fields, "legacy", null, true, raw),
            FeedOperation.Delete("item", "legacy", null, raw)
        ];
        fields["unit_price"] = FieldOp.Assign(99);
        Queue(HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK);

        var result = await _feed.FeedAsync(operations);

        Assert.Equal(3, result.SuccessCount);
        var requests = _handler.Requests;
        Assert.Equal(3, requests.Count);
        Assert.Equal("(legacy.unit_price > 5)", Query(requests[0])["condition"]);
        Assert.Equal(raw, Query(requests[1])["condition"]);
        Assert.Equal("true", Query(requests[1])["create"]);
        Assert.Equal(raw, Query(requests[2])["condition"]);
        Assert.Equal(HttpMethod.Delete, requests[2].Method);
        foreach (var request in requests.Take(2))
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal(17, body.RootElement.GetProperty("fields").GetProperty("unit_price").GetProperty("assign").GetInt32());
        }
    }

    private static FeedOperation Create<T>(string factory, T model, string? documentType = null, string? @namespace = null,
        Expression<Func<T, bool>>? condition = null, string? rawCondition = null, bool create = false,
        DocumentRequestOptions? options = null, Action<TypedFieldUpdateBuilder<T>>? configure = null) where T : PriceFields
    {
        return factory switch
        {
            "put" => FeedOperation.Put("item", model, documentType, @namespace, condition, rawCondition, create, options),
            "dictionary" => FeedOperation.Update<T>("item", new Dictionary<string, FieldOperation> { ["unit_price"] = FieldOp.Assign(model.Price) },
                documentType, @namespace, condition, rawCondition, create, options),
            "builder" => FeedOperation.Update<T>("item", configure ?? (builder => builder.Field(p => p.Price, FieldOp.Assign(model.Price))),
                documentType, @namespace, condition, rawCondition, create, options),
            "delete" => FeedOperation.Delete<T>("item", documentType, @namespace, condition, rawCondition, options),
            _ => throw new ArgumentOutOfRangeException(nameof(factory))
        };
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

    private sealed class FeedHandler : MockHttpMessageHandler
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

    private class PriceFields
    {
        [VespaField(Name = "unit_price")]
        public int Price { get; init; }
    }

    [VespaDocument("inventory", Namespace = "tenant")]
    private sealed class Inventory : PriceFields { }
}
