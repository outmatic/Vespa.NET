using System.Net;
using Vespa.Feed;
using Vespa.Models;
using Xunit;

namespace Vespa.Tests.Unit;

public sealed partial class MixedFeedTests
{
    [Theory]
    [InlineData("part:one", "id:tenant:inventory::part:one")]
    [InlineData("id:tenant:inventory:g=team:part:one", "id:tenant:inventory:g=team:part:one")]
    [InlineData("id:tenant:inventory:n=00042:part:one", "id:tenant:inventory:n=42:part:one")]
    public async Task MixedFeed_OrdersCanonicalDocumentIdentitiesWithoutBlockingOtherDocuments(string firstId, string nextId)
    {
        var firstStarted = Signal();
        var independentStarted = Signal();
        var releaseFirst = Signal();
        using var harness = new Harness(async (request, token) =>
        {
            if (request.Uri.AbsolutePath.EndsWith("/other", StringComparison.Ordinal))
                independentStarted.TrySetResult();
            else if (request.Method == "POST")
            {
                firstStarted.TrySetResult();
                await releaseFirst.Task.WaitAsync(token);
            }
            return Response();
        });
        FeedOperation[] operations =
        [
            FeedOperation.Put(firstId, new { unit_price = 10 }, documentType: "inventory"),
            FeedOperation.Update(nextId, new Dictionary<string, FieldOperation> { ["unit_price"] = FieldOp.Assign(20) }, "inventory", @namespace: "tenant"),
            FeedOperation.Delete(nextId, "inventory", @namespace: "tenant"),
            Put("other")
        ];
        var feed = harness.Feed.FeedAsync(operations, new() { MaxConcurrency = 2, BoundedCapacity = 4 });
        Request[] beforeRelease;
        try
        {
            await Task.WhenAll(firstStarted.Task, independentStarted.Task).WaitAsync(TimeSpan.FromSeconds(10));
            beforeRelease = harness.Requests.ToArray();
        }
        finally
        {
            releaseFirst.TrySetResult();
        }
        var result = await feed.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(4, result.SuccessCount);
        Assert.Empty(result.Errors);
        Assert.Single(beforeRelease, request => !request.Uri.AbsolutePath.EndsWith("/other", StringComparison.Ordinal));
        var sameDocument = harness.Requests.Where(request => !request.Uri.AbsolutePath.EndsWith("/other", StringComparison.Ordinal)).ToArray();
        Assert.Equal(["POST", "PUT", "DELETE"], sameDocument.Select(request => request.Method));
        Assert.Single(sameDocument.Select(request => request.Uri.AbsolutePath).Distinct());
    }

    [Fact]
    public async Task MixedFeed_DifferentSchemasNamespacesAndLocationsRunIndependently()
    {
        var allStarted = Signal();
        var release = Signal();
        var started = 0;
        using var harness = new Harness(async (_, token) =>
        {
            if (Interlocked.Increment(ref started) == 5)
                allStarted.TrySetResult();
            await release.Task.WaitAsync(token);
            return Response();
        });
        FeedOperation[] operations =
        [
            Put("same"),
            FeedOperation.Put("same", new { value = 1 }, "inventory", "other_namespace"),
            FeedOperation.Put("same", new { value = 1 }, "other_schema", "tenant"),
            FeedOperation.Put("id:tenant:inventory:g=team:same", new { value = 1 }, "inventory", "tenant"),
            FeedOperation.Put("id:tenant:inventory:n=42:same", new { value = 1 }, "inventory", "tenant")
        ];
        var feed = harness.Feed.FeedAsync(operations, new() { MaxConcurrency = 5, BoundedCapacity = 5 });
        try
        {
            await allStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            release.TrySetResult();
        }

        var result = await feed.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(5, result.SuccessCount);
        Assert.Empty(result.Errors);
    }

    [Theory]
    [InlineData(429)]
    [InlineData(503)]
    public async Task MixedFeed_RetryKeepsLaterMutationsBehindTheRetriedOperation(int status)
    {
        var retryStarted = Signal();
        var otherStarted = Signal();
        var releaseRetry = Signal();
        var attempts = 0;
        using var harness = new Harness(async (request, token) =>
        {
            if (request.Uri.AbsolutePath.EndsWith("/other", StringComparison.Ordinal))
                otherStarted.TrySetResult();
            else if (request.Method == "POST")
            {
                if (Interlocked.Increment(ref attempts) == 1)
                    return Response((HttpStatusCode)status);
                retryStarted.TrySetResult();
                await releaseRetry.Task.WaitAsync(token);
            }
            return Response();
        }, new() { DefaultNamespace = "tenant", EnableRetry = true, MaxRetryAttempts = 1, FeedRetryBaseDelay = TimeSpan.FromMilliseconds(1) });
        FeedOperation[] operations =
        [
            Put("item"),
            FeedOperation.Update<Item>("item", update => update.Field(item => item.Price, FieldOp.Assign(20))),
            FeedOperation.Delete<Item>("item"),
            Put("other")
        ];
        var feed = harness.Feed.FeedAsync(operations, new() { MaxConcurrency = 2, BoundedCapacity = 4 });
        Request[] beforeRelease;
        try
        {
            await Task.WhenAll(retryStarted.Task, otherStarted.Task).WaitAsync(TimeSpan.FromSeconds(10));
            beforeRelease = harness.Requests.Where(request => request.Uri.AbsolutePath.EndsWith("/item", StringComparison.Ordinal)).ToArray();
        }
        finally
        {
            releaseRetry.TrySetResult();
        }
        var result = await feed.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(["POST", "POST"], beforeRelease.Select(request => request.Method));
        Assert.Equal(4, result.SuccessCount);
        Assert.Empty(result.Errors);
        var requests = harness.Requests.Where(request => request.Uri.AbsolutePath.EndsWith("/item", StringComparison.Ordinal)).ToArray();
        Assert.Equal(["POST", "POST", "PUT", "DELETE"], requests.Select(request => request.Method));
        Assert.Equal(requests[0].Body, requests[1].Body);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MixedFeed_BackpressureBoundsQueuedOperationsAndConcurrency(bool sameDocument)
    {
        const int concurrency = 3;
        const int capacity = 2;
        var expectedConcurrency = sameDocument ? 1 : concurrency;
        var sourceLimit = expectedConcurrency + capacity + 1;
        var allStarted = Signal();
        var sourceBlocked = Signal();
        var release = Signal();
        var active = 0;
        var maximumActive = 0;
        var produced = 0;
        using var harness = new Harness(async (_, token) =>
        {
            var current = Interlocked.Increment(ref active);
            int observed;
            do
                observed = Volatile.Read(ref maximumActive);
            while (current > observed && Interlocked.CompareExchange(ref maximumActive, current, observed) != observed);
            if (current == expectedConcurrency)
                allStarted.TrySetResult();
            try
            {
                await release.Task.WaitAsync(token);
                return Response();
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        });
        IEnumerable<FeedOperation> Source()
        {
            for (var i = 0; i < 100; i++)
            {
                if (Interlocked.Increment(ref produced) == sourceLimit)
                    sourceBlocked.TrySetResult();
                yield return Put(sameDocument ? "same" : $"item-{i}");
            }
        }
        var feed = harness.Feed.FeedAsync(Source(), new() { MaxConcurrency = concurrency, BoundedCapacity = capacity });
        int producedBeforeRelease;
        try
        {
            await Task.WhenAll(allStarted.Task, sourceBlocked.Task).WaitAsync(TimeSpan.FromSeconds(10));
            producedBeforeRelease = Volatile.Read(ref produced);
        }
        finally
        {
            release.TrySetResult();
        }
        var result = await feed.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(sourceLimit, producedBeforeRelease);
        Assert.Equal(expectedConcurrency, maximumActive);
        Assert.Equal(100, result.SuccessCount);
        Assert.Equal(0, active);
    }

    [Fact]
    public async Task MixedFeed_ProducerFailureDrainsAcceptedOperationsBeforeReturning()
    {
        var firstStarted = Signal();
        var sourceFailed = Signal();
        var release = Signal();
        using var harness = new Harness(async (request, token) =>
        {
            if (request.Method == "POST" && request.Uri.AbsolutePath.EndsWith("/item", StringComparison.Ordinal))
            {
                firstStarted.TrySetResult();
                await release.Task.WaitAsync(token);
            }
            return Response();
        });
        async IAsyncEnumerable<FeedOperation> Source()
        {
            yield return Put("item");
            await firstStarted.Task;
            yield return FeedOperation.Update<Item>("item", update => update.Field(item => item.Price, FieldOp.Assign(20)));
            yield return FeedOperation.Delete<Item>("other");
            sourceFailed.TrySetResult();
            throw new InvalidDataException("source failed");
        }
        var feed = harness.Feed.FeedAsync(Source(), new() { MaxConcurrency = 2, BoundedCapacity = 3 });
        bool returnedBeforeDrain;
        try
        {
            await sourceFailed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            returnedBeforeDrain = feed.IsCompleted;
        }
        finally
        {
            release.TrySetResult();
        }
        var failure = await Assert.ThrowsAsync<VespaFeedException>(() => feed.WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.False(returnedBeforeDrain);
        Assert.IsType<InvalidDataException>(failure.InnerException);
        Assert.Equal(3, failure.PartialResult.TotalOperations);
        Assert.Equal(3, failure.PartialResult.SuccessCount);
        Assert.Equal(0, failure.PartialResult.FailureCount);
        Assert.Equal(3, harness.Requests.Count);
    }

    [Fact]
    public async Task MixedFeed_ThrowingProgressCallbackUnblocksProducer()
    {
        using var harness = new Harness();
        var operations = Enumerable.Range(0, 1000).Select(index => Put($"item-{index}"));

        var failure = await Assert.ThrowsAsync<VespaFeedException>(() => harness.Feed.FeedAsync(operations,
            new() { MaxConcurrency = 1, BoundedCapacity = 1, OnProgress = _ => throw new InvalidOperationException("observer failed") })
            .WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.IsType<InvalidOperationException>(failure.InnerException);
        Assert.Equal(1, failure.PartialResult.SuccessCount);
    }

    [Fact]
    public async Task MixedFeed_CancellationStopsBlockedProducerAndInflightOperation()
    {
        var started = Signal();
        var active = 0;
        using var source = new CancellationTokenSource();
        using var harness = new Harness(async (_, token) =>
        {
            Interlocked.Increment(ref active);
            started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, token);
                return Response();
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        });
        var operations = Enumerable.Range(0, 1000).Select(_ => Put("same"));
        var feed = harness.Feed.FeedAsync(operations, new() { MaxConcurrency = 4, BoundedCapacity = 2 }, source.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        source.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => feed.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, active);
        Assert.Single(harness.Requests);
    }

    [Fact]
    public async Task MixedFeed_NullEntryFaultsSourceAndPreservesCompletedWork()
    {
        using var harness = new Harness();
        FeedOperation[] operations = [Put("item"), null!];

        var failure = await Assert.ThrowsAsync<VespaFeedException>(() => harness.Feed.FeedAsync(operations));

        Assert.IsType<ArgumentNullException>(failure.InnerException);
        Assert.Equal(1, failure.PartialResult.TotalOperations);
        Assert.Equal(1, failure.PartialResult.SuccessCount);
    }
}
