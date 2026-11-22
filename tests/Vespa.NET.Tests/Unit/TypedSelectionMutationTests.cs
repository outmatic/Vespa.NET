using System.Linq.Expressions;
using System.Net;
using System.Text.Json;
using Vespa.Documents;
using Vespa.Models;
using Vespa.Models.Attributes;
using Vespa.Tests.Helpers;
using Xunit;

namespace Vespa.Tests.Unit;

public sealed class TypedSelectionMutationTests : IDisposable
{
    private const string SourceCluster = "source";
    private const string DestinationCluster = "archive";
    private const string ResumeToken = "token+/= &?";
    private const string NextToken = "next+/= &?";
    private readonly MockHttpMessageHandler _handler = new();
    private readonly HttpClient _http;
    private readonly DocumentOperations _concrete;

    public TypedSelectionMutationTests()
    {
        _http = new(_handler) { BaseAddress = new Uri("http://localhost:8080") };
        _concrete = new(_http, new() { DefaultNamespace = "fallback" });
    }

    public void Dispose() => _http.Dispose();

    public static TheoryData<string, bool> MutationForms => new()
    {
        { "dictionary", false }, { "dictionary", true },
        { "builder", false }, { "builder", true },
        { "delete", false }, { "delete", true },
        { "copy", false }, { "copy", true }
    };

    [Theory]
    [MemberData(nameof(MutationForms))]
    public async Task TypedSelection_PreservesScopeAndCapturedValueBeforeConfigure(string operation, bool page)
    {
        var minimumPrice = 10;
        var configureCount = 0;
        void Configure(TypedFieldUpdateBuilder<Inventory> builder)
        {
            minimumPrice = 99;
            configureCount++;
            builder.Field(p => p.Price, FieldOp.Assign(42));
        }

        await AssertMutation(operation, page, p => p.Price > minimumPrice, null,
            "(inventory and ((inventory.unit_price > 10)))", Configure);

        Assert.Equal(operation == "builder" ? 1 : 0, configureCount);
    }

    [Theory]
    [MemberData(nameof(MutationForms))]
    public async Task RawSelection_ForwardsNativeTextWithoutRewritingIt(string operation, bool page)
    {
        const string raw = " (id.user == 42 or inventory.unit_price > 0) ";
        await AssertMutation(operation, page, null, raw, raw);
    }

    [Theory]
    [MemberData(nameof(MutationForms))]
    public async Task InvalidSelection_FailsBeforeCallbackOrHttp(string operation, bool page)
    {
        var configureCalled = false;
        Action<TypedFieldUpdateBuilder<Inventory>> configure = _ => configureCalled = true;

        await AssertInvalid(null, null);
        await AssertInvalid(null, "");
        await AssertInvalid(null, " \t");
        await AssertInvalid(p => true, "true");
        await AssertInvalid(p => true, " ");
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            Invoke(operation, page, p => Math.Abs(p.Price) > 10, configure: configure));

        Assert.False(configureCalled);
        Assert.Empty(_handler.Requests);

        Task AssertInvalid(Expression<Func<Inventory, bool>>? selection, string? raw) =>
            Assert.ThrowsAnyAsync<ArgumentException>(() => Invoke(operation, page, selection, raw, configure: configure));
    }

    [Fact]
    public async Task OmittedAndDefaultSelectors_RequireExplicitSelectionEvenOnConcreteReceiver()
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(() => _concrete.DeleteBySelectionAsync<Inventory>());
        await Assert.ThrowsAnyAsync<ArgumentException>(() => _concrete.CopyBySelectionPageAsync<Inventory>(
            SourceCluster, DestinationCluster, selection: default, rawSelection: null));
        Assert.Empty(_handler.Requests);

        Queue(3);
        await _concrete.DeleteBySelectionAsync<Inventory>(selection: p => true);
        Queue(2);
        await _concrete.DeleteBySelectionPageAsync<Inventory>(rawSelection: "true");
        Assert.Collection(_handler.Requests,
            request => Assert.Equal("(inventory and (true))", Query(request)["selection"]),
            request => Assert.Equal("true", Query(request)["selection"]));
    }

    [Theory]
    [MemberData(nameof(MutationForms))]
    public async Task CallerCancellation_PropagatesThroughEveryForm(string operation, bool page)
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Invoke(operation, page, p => true, cancellationToken: source.Token));
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task ManualResume_ReusesTheSavedRawSelectionAndOpaqueToken()
    {
        var minimumPrice = 10;
        var saved = DocumentSelection.For<Inventory>(p => p.Price > minimumPrice);
        Queue(1, ResumeToken);
        var first = await _concrete.DeleteBySelectionPageAsync<Inventory>(rawSelection: saved, cluster: SourceCluster);
        minimumPrice = 99;
        Queue(3);
        var second = await _concrete.DeleteBySelectionPageAsync<Inventory>(
            rawSelection: saved, cluster: SourceCluster, continuation: first.Continuation);

        Assert.Equal(1, first.DocumentCount);
        Assert.Equal(ResumeToken, first.Continuation);
        Assert.True(second.IsComplete);
        Assert.Equal(3, second.DocumentCount);
        Assert.NotEqual(DocumentSelection.For<Inventory>(p => p.Price > minimumPrice), saved);
        Assert.Collection(_handler.Requests,
            request => Assert.DoesNotContain("continuation", Query(request)),
            request => Assert.Equal(ResumeToken, Query(request)["continuation"]));
        Assert.All(_handler.Requests, request => Assert.Equal(saved, Query(request)["selection"]));
    }

    private async Task AssertMutation(string operation, bool page, Expression<Func<Inventory, bool>>? selection,
        string? rawSelection, string expectedSelection, Action<TypedFieldUpdateBuilder<Inventory>>? configure = null)
    {
        Queue(2, NextToken);
        if (!page)
            Queue(3);
        var options = new SelectionRequestOptions { Timeout = TimeSpan.FromSeconds(2), BucketSpace = "global", TraceLevel = 4 };
        var result = await Invoke(operation, page, selection, rawSelection, page ? ResumeToken : null, options, configure);

        Assert.Equal(page ? 2L : 5L, result.Count);
        Assert.Equal(page ? NextToken : null, result.Continuation);
        var requests = _handler.Requests;
        Assert.Equal(page ? 1 : 2, requests.Count);
        var expectedMethod = operation switch { "delete" => HttpMethod.Delete, "copy" => HttpMethod.Post, _ => HttpMethod.Put };
        for (var index = 0; index < requests.Count; index++)
        {
            var request = requests[index];
            Assert.Equal(expectedMethod, request.Method);
            Assert.Equal("/document/v1/tenant/inventory/docid/", request.RequestUri!.AbsolutePath);
            var query = Query(request);
            Assert.Equal(expectedSelection, query["selection"]);
            Assert.Equal(SourceCluster, query["cluster"]);
            Assert.Equal("global", query["bucketSpace"]);
            Assert.Equal("2000ms", query["timeout"]);
            Assert.Equal("4", query["tracelevel"]);
            if (operation == "copy")
                Assert.Equal(DestinationCluster, query["destinationCluster"]);
            else
                Assert.DoesNotContain("destinationCluster", query);
            if (page || index > 0)
                Assert.Equal(page ? ResumeToken : NextToken, query["continuation"]);
            else
                Assert.DoesNotContain("continuation", query);

            if (expectedMethod == HttpMethod.Put)
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                Assert.Equal(42, body.RootElement.GetProperty("fields").GetProperty("unit_price").GetProperty("assign").GetInt32());
            }
            else
                Assert.Null(request.Content);
        }
    }

    private async Task<(long? Count, string? Continuation)> Invoke(string operation, bool page,
        Expression<Func<Inventory, bool>>? selection = null, string? rawSelection = null, string? continuation = null,
        SelectionRequestOptions? options = null, Action<TypedFieldUpdateBuilder<Inventory>>? configure = null,
        CancellationToken cancellationToken = default)
    {
        IDocumentOperations documents = _concrete;
        Dictionary<string, FieldOperation> fields = new() { ["unit_price"] = FieldOp.Assign(42) };
        configure ??= builder => builder.Field(p => p.Price, FieldOp.Assign(42));
        if (page)
        {
            var result = await (operation switch
            {
                "dictionary" => documents.UpdateBySelectionPageAsync<Inventory>(fields, selection, rawSelection, SourceCluster, continuation, options, cancellationToken),
                "builder" => documents.UpdateBySelectionPageAsync<Inventory>(configure, selection, rawSelection, SourceCluster, continuation, options, cancellationToken),
                "delete" => documents.DeleteBySelectionPageAsync<Inventory>(selection, rawSelection, SourceCluster, continuation, options, cancellationToken),
                "copy" => documents.CopyBySelectionPageAsync<Inventory>(SourceCluster, DestinationCluster, selection, rawSelection, continuation, options, cancellationToken),
                _ => throw new ArgumentOutOfRangeException(nameof(operation))
            });
            return (result.DocumentCount, result.Continuation);
        }

        var complete = await (operation switch
        {
            "dictionary" => documents.UpdateBySelectionAsync<Inventory>(fields, selection, rawSelection, SourceCluster, options, cancellationToken),
            "builder" => documents.UpdateBySelectionAsync<Inventory>(configure, selection, rawSelection, SourceCluster, options, cancellationToken),
            "delete" => documents.DeleteBySelectionAsync<Inventory>(selection, rawSelection, SourceCluster, options, cancellationToken),
            "copy" => documents.CopyBySelectionAsync<Inventory>(SourceCluster, DestinationCluster, selection, rawSelection, options, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        });
        return (complete.DocumentCount, null);
    }

    private void Queue(int count, string? continuation = null) =>
        _handler.QueueResponse(new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { documentCount = count, continuation }))
        });

    private static Dictionary<string, string> Query(HttpRequestMessage request) =>
        request.RequestUri!.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .ToDictionary(pair => pair[0], pair => Uri.UnescapeDataString(pair[1]));

    [VespaDocument("inventory", Namespace = "tenant")]
    private sealed class Inventory
    {
        [VespaField(Name = "unit_price")]
        public int Price { get; init; }
    }
}
