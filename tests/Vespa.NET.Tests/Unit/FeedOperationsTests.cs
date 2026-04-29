using System.Collections.Concurrent;
using Moq;
using Vespa;
using Vespa.Documents;
using Vespa.Feed;
using Vespa.Models;
using Vespa.Tests.Helpers;
using Xunit;

namespace Vespa.Tests.Unit;

/// <summary>
/// Tests for FeedOperations covering bulk operations, concurrency control, and thread-safety
/// </summary>
public class FeedOperationsTests
{
    private readonly Mock<IDocumentOperations> _mockDocOps;
    private readonly VespaClientOptions _options;
    private readonly FeedOperations _feedOps;

    public FeedOperationsTests()
    {
        _mockDocOps = new Mock<IDocumentOperations>();
        _options = new VespaClientOptions
        {
            Endpoint = "http://localhost:8080",
            DefaultNamespace = "test"
        };
        _feedOps = new FeedOperations(_mockDocOps.Object, _options);
    }

    #region BulkPutAsync Basic Tests (7 tests)

    [Fact]
    public async Task BulkPutAsync_WithValidDocuments_ReturnsSuccessResult()
    {
        // Arrange
        var documents = new[]
        {
            TestDataFactory.CreateFeedDocument("doc-1", new { name = "Test 1" }),
            TestDataFactory.CreateFeedDocument("doc-2", new { name = "Test 2" }),
            TestDataFactory.CreateFeedDocument("doc-3", new { name = "Test 3" })
        };

        _mockDocOps
            .Setup(d => d.PutAsync(It.IsAny<string>(), It.IsAny<object>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<DocumentRequestOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VespaResponse { IsSuccess = true, StatusCode = 200 });

        // Act
        var result = await _feedOps.BulkPutAsync(documents, "testdoc");

        // Assert
        Assert.Equal(3, result.TotalDocuments);
        Assert.Equal(3, result.SuccessCount);
        Assert.Equal(0, result.FailureCount);
        Assert.True(result.IsSuccess);
        Assert.Equal(1.0, result.SuccessRate);
        Assert.Empty(result.Errors);
        Assert.True(result.Duration > TimeSpan.Zero);
    }

    [Fact]
    public async Task BulkPutAsync_WithEmptyList_ReturnsEmptyResult()
    {
        // Arrange
        var documents = Array.Empty<FeedDocument<object>>();

        // Act
        var result = await _feedOps.BulkPutAsync(documents, "testdoc");

        // Assert
        Assert.Equal(0, result.TotalDocuments);
        Assert.Equal(0, result.SuccessCount);
        Assert.Equal(0, result.FailureCount);
        Assert.True(result.IsSuccess);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task BulkPutAsync_MixedSuccessFailure_CountsCorrectly()
    {
        // Arrange
        var documents = new[]
        {
            TestDataFactory.CreateFeedDocument("doc-1", new { name = "Test 1" }),
            TestDataFactory.CreateFeedDocument("doc-2", new { name = "Test 2" }),
            TestDataFactory.CreateFeedDocument("doc-3", new { name = "Test 3" })
        };

        // Setup: doc-2 fails, others succeed
        _mockDocOps
            .Setup(d => d.PutAsync(It.Is<string>(id => id == "doc-2"), It.IsAny<object>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<DocumentRequestOptions?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new VespaException("Test error", 500));

        _mockDocOps
            .Setup(d => d.PutAsync(It.Is<string>(id => id != "doc-2"), It.IsAny<object>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<DocumentRequestOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VespaResponse { IsSuccess = true });

        // Act
        var result = await _feedOps.BulkPutAsync(documents, "testdoc");

        // Assert
        Assert.Equal(3, result.TotalDocuments);
        Assert.Equal(2, result.SuccessCount);
        Assert.Equal(1, result.FailureCount);
        Assert.False(result.IsSuccess);
        Assert.Equal(2.0 / 3.0, result.SuccessRate);
        Assert.Single(result.Errors);
        Assert.Equal("doc-2", result.Errors.First().DocumentId);
        Assert.Equal(500, result.Errors.First().StatusCode);
    }

    [Fact]
    public async Task BulkPutAsync_AllFailures_ReturnsFailureResult()
    {
        // Arrange
        var documents = new[]
        {
            TestDataFactory.CreateFeedDocument("doc-1", new { name = "Test 1" }),
            TestDataFactory.CreateFeedDocument("doc-2", new { name = "Test 2" })
        };

        _mockDocOps
            .Setup(d => d.PutAsync(It.IsAny<string>(), It.IsAny<object>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<DocumentRequestOptions?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new VespaException("Test error", 500));

        // Act
        var result = await _feedOps.BulkPutAsync(documents, "testdoc");

        // Assert
        Assert.Equal(2, result.TotalDocuments);
        Assert.Equal(0, result.SuccessCount);
        Assert.Equal(2, result.FailureCount);
        Assert.False(result.IsSuccess);
        Assert.Equal(0.0, result.SuccessRate);
        Assert.Equal(2, result.Errors.Count);
    }

    [Fact]
    public async Task BulkPutAsync_WithNullDocuments_ThrowsArgumentNullException()
    {
        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _feedOps.BulkPutAsync<object>(null!, "testdoc"));
    }

    [Fact]
    public async Task BulkPutAsync_WithNullDocumentType_ThrowsArgumentException()
    {
        // Arrange
        var documents = new[] { TestDataFactory.CreateFeedDocument("doc-1", new { }) };

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _feedOps.BulkPutAsync(documents, null!));
    }

    [Fact]
    public async Task BulkPutAsync_WithZeroMaxConcurrency_ThrowsArgumentOutOfRangeException()
    {
        // Arrange
        var documents = new[] { TestDataFactory.CreateFeedDocument("doc-1", new { }) };

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            _feedOps.BulkPutAsync(documents, "testdoc", maxConcurrency: 0));
    }

    #endregion

    #region Concurrency Control Tests (6 tests)

    [Fact]
    public async Task BulkPutAsync_RespectsMaxConcurrency()
    {
        // Arrange
        var documents = Enumerable.Range(0, 100)
            .Select(i => TestDataFactory.CreateFeedDocument($"doc-{i}", new { value = i }))
            .ToList();

        var concurrentCount = 0;
        var maxObservedConcurrency = 0;
        var lockObj = new object();

        _mockDocOps
            .Setup(d => d.PutAsync(It.IsAny<string>(), It.IsAny<object>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<DocumentRequestOptions?>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                lock (lockObj)
                {
                    concurrentCount++;
                    maxObservedConcurrency = Math.Max(maxObservedConcurrency, concurrentCount);
                }

                await Task.Delay(10); // Simulate work

                lock (lockObj)
                {
                    concurrentCount--;
                }

                return new VespaResponse { IsSuccess = true, StatusCode = 200 };
            });

        // Act
        var result = await _feedOps.BulkPutAsync(documents, "testdoc", maxConcurrency: 5);

        // Assert
        Assert.True(maxObservedConcurrency <= 5,
            $"Observed {maxObservedConcurrency} concurrent operations, expected max 5");
        Assert.Equal(100, result.SuccessCount);
    }

    [Fact]
    public async Task BulkPutAsync_SuccessCounter_IsThreadSafe()
    {
        // Arrange
        var documents = Enumerable.Range(0, 100)
            .Select(i => TestDataFactory.CreateFeedDocument($"doc-{i}", new { value = i }))
            .ToList();

        _mockDocOps
            .Setup(d => d.PutAsync(It.IsAny<string>(), It.IsAny<object>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<DocumentRequestOptions?>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await Task.Delay(1); // Small delay to increase chance of race conditions
                return new VespaResponse { IsSuccess = true };
            });

        // Act
        var result = await _feedOps.BulkPutAsync(documents, "testdoc", maxConcurrency: 20);

        // Assert - If counter wasn't thread-safe, we might get wrong count
        Assert.Equal(100, result.SuccessCount);
        Assert.Equal(0, result.FailureCount);
    }

    [Fact]
    public async Task BulkPutAsync_FailureCounter_IsThreadSafe()
    {
        // Arrange
        var documents = Enumerable.Range(0, 100)
            .Select(i => TestDataFactory.CreateFeedDocument($"doc-{i}", new { value = i }))
            .ToList();

        _mockDocOps
            .Setup(d => d.PutAsync(It.IsAny<string>(), It.IsAny<object>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<DocumentRequestOptions?>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await Task.Delay(1);
                throw new Exception("Test error");
            });

        // Act
        var result = await _feedOps.BulkPutAsync(documents, "testdoc", maxConcurrency: 20);

        // Assert
        Assert.Equal(0, result.SuccessCount);
        Assert.Equal(100, result.FailureCount);
    }

    [Fact]
    public async Task BulkPutAsync_ErrorList_IsThreadSafe()
    {
        // Arrange
        var documents = Enumerable.Range(0, 50)
            .Select(i => TestDataFactory.CreateFeedDocument($"doc-{i}", new { value = i }))
            .ToList();

        _mockDocOps
            .Setup(d => d.PutAsync(It.IsAny<string>(), It.IsAny<object>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<DocumentRequestOptions?>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await Task.Delay(1);
                throw new VespaException("Test error", 500);
            });

        // Act
        var result = await _feedOps.BulkPutAsync(documents, "testdoc", maxConcurrency: 10);

        // Assert - All errors should be captured without list corruption
        Assert.Equal(50, result.Errors.Count);
        Assert.All(result.Errors, error =>
        {
            Assert.NotNull(error.DocumentId);
            Assert.StartsWith("doc-", error.DocumentId);
            Assert.Equal(500, error.StatusCode);
        });
    }

    [Fact]
    public async Task BulkPutAsync_SemaphoreReleasedOnException()
    {
        // Arrange
        var documents = new[]
        {
            TestDataFactory.CreateFeedDocument("doc-1", new { }),
            TestDataFactory.CreateFeedDocument("doc-2", new { })
        };

        var firstCall = true;
        _mockDocOps
            .Setup(d => d.PutAsync(It.IsAny<string>(), It.IsAny<object>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<DocumentRequestOptions?>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                if (firstCall)
                {
                    firstCall = false;
                    throw new Exception("First document fails");
                }
                return Task.FromResult(new VespaResponse { IsSuccess = true });
            });

        // Act
        var result = await _feedOps.BulkPutAsync(documents, "testdoc", maxConcurrency: 1);

        // Assert - If semaphore wasn't released, second document wouldn't process
        Assert.Equal(1, result.SuccessCount);
        Assert.Equal(1, result.FailureCount);
    }

    [Fact]
    public async Task BulkPutAsync_WithCustomNamespace_PassesToDocumentOperations()
    {
        // Arrange
        var documents = new[] { TestDataFactory.CreateFeedDocument("doc-1", new { }) };

        _mockDocOps
            .Setup(d => d.PutAsync(It.IsAny<string>(), It.IsAny<object>(),
                "testdoc", "custom-ns", It.IsAny<string?>(), It.IsAny<DocumentRequestOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VespaResponse { IsSuccess = true });

        // Act
        await _feedOps.BulkPutAsync(documents, "testdoc", @namespace: "custom-ns");

        // Assert
        _mockDocOps.Verify(d => d.PutAsync(
            It.IsAny<string>(),
            It.IsAny<object>(),
            "testdoc",
            "custom-ns",
            It.IsAny<string?>(), It.IsAny<DocumentRequestOptions?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion

    #region BulkDeleteAsync Tests (5 tests)

    [Fact]
    public async Task BulkDeleteAsync_WithValidIds_ReturnsSuccessResult()
    {
        // Arrange
        var documentIds = new[] { "doc-1", "doc-2", "doc-3" };

        _mockDocOps
            .Setup(d => d.DeleteAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<DocumentRequestOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VespaResponse { IsSuccess = true, StatusCode = 200 });

        // Act
        var result = await _feedOps.BulkDeleteAsync(documentIds, "testdoc");

        // Assert
        Assert.Equal(3, result.TotalDocuments);
        Assert.Equal(3, result.SuccessCount);
        Assert.Equal(0, result.FailureCount);
        Assert.True(result.IsSuccess);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task BulkDeleteAsync_WithEmptyList_ReturnsEmptyResult()
    {
        // Arrange
        var documentIds = Array.Empty<string>();

        // Act
        var result = await _feedOps.BulkDeleteAsync(documentIds, "testdoc");

        // Assert
        Assert.Equal(0, result.TotalDocuments);
        Assert.Equal(0, result.SuccessCount);
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task BulkDeleteAsync_MixedSuccessFailure_CountsCorrectly()
    {
        // Arrange
        var documentIds = new[] { "doc-1", "doc-2", "doc-3" };

        _mockDocOps
            .Setup(d => d.DeleteAsync(It.Is<string>(id => id == "doc-2"), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<DocumentRequestOptions?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new VespaException("Test error", 404));

        _mockDocOps
            .Setup(d => d.DeleteAsync(It.Is<string>(id => id != "doc-2"), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<DocumentRequestOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VespaResponse { IsSuccess = true });

        // Act
        var result = await _feedOps.BulkDeleteAsync(documentIds, "testdoc");

        // Assert
        Assert.Equal(3, result.TotalDocuments);
        Assert.Equal(2, result.SuccessCount);
        Assert.Equal(1, result.FailureCount);
        Assert.Single(result.Errors);
        Assert.Equal("doc-2", result.Errors.First().DocumentId);
    }

    [Fact]
    public async Task BulkDeleteAsync_RespectsMaxConcurrency()
    {
        // Arrange
        var documentIds = Enumerable.Range(0, 50).Select(i => $"doc-{i}").ToList();

        var concurrentCount = 0;
        var maxObservedConcurrency = 0;
        var lockObj = new object();

        _mockDocOps
            .Setup(d => d.DeleteAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<DocumentRequestOptions?>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                lock (lockObj)
                {
                    concurrentCount++;
                    maxObservedConcurrency = Math.Max(maxObservedConcurrency, concurrentCount);
                }

                await Task.Delay(10);

                lock (lockObj)
                {
                    concurrentCount--;
                }

                return new VespaResponse { IsSuccess = true };
            });

        // Act
        await _feedOps.BulkDeleteAsync(documentIds, "testdoc", maxConcurrency: 5);

        // Assert
        Assert.True(maxObservedConcurrency <= 5,
            $"Observed {maxObservedConcurrency} concurrent operations, expected max 5");
    }

    [Fact]
    public async Task BulkDeleteAsync_WithNullDocumentIds_ThrowsArgumentNullException()
    {
        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _feedOps.BulkDeleteAsync(null!, "testdoc"));
    }

    #endregion

    #region FeedResult Tests (2 tests)

    [Fact]
    public void FeedResult_SuccessRate_CalculatedCorrectly()
    {
        // Arrange
        var result = new FeedResult { TotalDocuments = 10 };

        // Simulate 7 successes, 3 failures
        for (var i = 0; i < 7; i++)
            result.IncrementSuccess();
        for (var i = 0; i < 3; i++)
            result.IncrementFailure();

        // Act
        var successRate = result.SuccessRate;

        // Assert
        Assert.Equal(0.7, successRate);
        Assert.Equal(7, result.SuccessCount);
        Assert.Equal(3, result.FailureCount);
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void FeedResult_WithZeroDocuments_SuccessRateIsZero()
    {
        // Arrange
        var result = new FeedResult { TotalDocuments = 0 };

        // Act
        var successRate = result.SuccessRate;

        // Assert
        Assert.Equal(0.0, successRate);
        Assert.True(result.IsSuccess); // No failures means success
    }

    #endregion

    #region Per-document condition tests

    [Fact]
    public async Task BulkPutAsync_DocumentWithCondition_PassesConditionToPutAsync()
    {
        var doc = new FeedDocument<object>
        {
            Id = "doc-1",
            Fields = new { name = "Test" },
            Condition = "music.year > 2000"
        };

        _mockDocOps
            .Setup(d => d.PutAsync(It.IsAny<string>(), It.IsAny<object>(),
                It.IsAny<string>(), It.IsAny<string>(), "music.year > 2000", It.IsAny<DocumentRequestOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VespaResponse { IsSuccess = true, StatusCode = 200 });

        var result = await _feedOps.BulkPutAsync([doc], "music");

        Assert.Equal(1, result.SuccessCount);
        _mockDocOps.Verify(d => d.PutAsync(
            "doc-1", It.IsAny<object>(), "music",
            It.IsAny<string>(), "music.year > 2000", It.IsAny<DocumentRequestOptions?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task BulkPutAsync_DocumentWithoutCondition_PassesNullCondition()
    {
        var doc = new FeedDocument<object>
        {
            Id = "doc-1",
            Fields = new { name = "Test" }
        };

        _mockDocOps
            .Setup(d => d.PutAsync(It.IsAny<string>(), It.IsAny<object>(),
                It.IsAny<string>(), It.IsAny<string>(), null, It.IsAny<DocumentRequestOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VespaResponse { IsSuccess = true, StatusCode = 200 });

        var result = await _feedOps.BulkPutAsync([doc], "music");

        Assert.Equal(1, result.SuccessCount);
        _mockDocOps.Verify(d => d.PutAsync(
            "doc-1", It.IsAny<object>(), "music",
            It.IsAny<string>(), null, It.IsAny<DocumentRequestOptions?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task BulkPutAsync_MixedConditions_EachDocumentUsesItsOwnCondition()
    {
        var documents = new[]
        {
            new FeedDocument<object> { Id = "doc-1", Fields = new { }, Condition = "music.year > 2000" },
            new FeedDocument<object> { Id = "doc-2", Fields = new { } }
        };

        _mockDocOps
            .Setup(d => d.PutAsync(It.IsAny<string>(), It.IsAny<object>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<DocumentRequestOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VespaResponse { IsSuccess = true, StatusCode = 200 });

        var result = await _feedOps.BulkPutAsync(documents, "music");

        Assert.Equal(2, result.SuccessCount);
        _mockDocOps.Verify(d => d.PutAsync(
            "doc-1", It.IsAny<object>(), "music",
            It.IsAny<string>(), "music.year > 2000", It.IsAny<DocumentRequestOptions?>(), It.IsAny<CancellationToken>()),
            Times.Once);
        _mockDocOps.Verify(d => d.PutAsync(
            "doc-2", It.IsAny<object>(), "music",
            It.IsAny<string>(), null, It.IsAny<DocumentRequestOptions?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    #endregion

    #region FeedAsync Pipeline Tests (7 tests)

    [Fact]
    public async Task FeedAsync_WithAsyncEnumerable_ReturnsSuccessResult()
    {
        _mockDocOps
            .Setup(d => d.PutAsync(It.IsAny<string>(), It.IsAny<object>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<DocumentRequestOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VespaResponse { IsSuccess = true, StatusCode = 200 });

        var result = await _feedOps.FeedAsync(GenerateDocuments(5), "testdoc", maxConcurrency: 2);

        Assert.Equal(5, result.TotalDocuments);
        Assert.Equal(5, result.SuccessCount);
        Assert.Equal(0, result.FailureCount);
        Assert.True(result.IsSuccess);
        Assert.True(result.Duration > TimeSpan.Zero);
    }

    [Fact]
    public async Task FeedAsync_WithEmptyStream_ReturnsZeroResult()
    {
        var result = await _feedOps.FeedAsync(GenerateDocuments(0), "testdoc");

        Assert.Equal(0, result.TotalDocuments);
        Assert.Equal(0, result.SuccessCount);
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task FeedAsync_MixedSuccessFailure_TracksErrors()
    {
        _mockDocOps
            .Setup(d => d.PutAsync(It.Is<string>(id => id == "doc-2"), It.IsAny<object>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<DocumentRequestOptions?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new VespaException("Test error", 500));

        _mockDocOps
            .Setup(d => d.PutAsync(It.Is<string>(id => id != "doc-2"), It.IsAny<object>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<DocumentRequestOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VespaResponse { IsSuccess = true });

        var result = await _feedOps.FeedAsync(GenerateDocuments(5), "testdoc", maxConcurrency: 2);

        Assert.Equal(5, result.TotalDocuments);
        Assert.Equal(4, result.SuccessCount);
        Assert.Equal(1, result.FailureCount);
        Assert.Single(result.Errors);
        Assert.Equal("doc-2", result.Errors.First().DocumentId);
        Assert.Equal(500, result.Errors.First().StatusCode);
    }

    [Fact]
    public async Task FeedAsync_InvokesProgressCallback()
    {
        _mockDocOps
            .Setup(d => d.PutAsync(It.IsAny<string>(), It.IsAny<object>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<DocumentRequestOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VespaResponse { IsSuccess = true });

        var progressCalls = new ConcurrentQueue<FeedProgress>();

        var result = await _feedOps.FeedAsync(
            GenerateDocuments(3), "testdoc",
            maxConcurrency: 1,
            onProgress: p => progressCalls.Enqueue(p));

        Assert.Equal(3, result.SuccessCount);
        Assert.Equal(3, progressCalls.Count);
        // Last progress should have all 3 successes
        Assert.Equal(3, progressCalls.Last().SuccessCount);
    }

    [Fact]
    public async Task FeedAsync_RespectsBackpressure()
    {
        var concurrentCount = 0;
        var maxObservedConcurrency = 0;
        var lockObj = new object();

        _mockDocOps
            .Setup(d => d.PutAsync(It.IsAny<string>(), It.IsAny<object>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<DocumentRequestOptions?>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                lock (lockObj)
                {
                    concurrentCount++;
                    maxObservedConcurrency = Math.Max(maxObservedConcurrency, concurrentCount);
                }
                await Task.Delay(10);
                lock (lockObj) concurrentCount--;
                return new VespaResponse { IsSuccess = true };
            });

        var result = await _feedOps.FeedAsync(
            GenerateDocuments(50), "testdoc",
            maxConcurrency: 4, boundedCapacity: 8);

        Assert.Equal(50, result.SuccessCount);
        Assert.True(maxObservedConcurrency <= 4,
            $"Observed {maxObservedConcurrency} concurrent ops, expected max 4");
    }

    [Fact]
    public async Task FeedAsync_WithNullDocuments_ThrowsArgumentNullException()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _feedOps.FeedAsync<object>(null!, "testdoc"));
    }

    [Fact]
    public async Task FeedAsync_WithZeroConcurrency_ThrowsArgumentOutOfRangeException()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            _feedOps.FeedAsync(GenerateDocuments(1), "testdoc", maxConcurrency: 0));
    }

    [Fact]
    public async Task FeedAsync_OnCancellation_ThrowsOperationCanceledException()
    {
        using var cts = new CancellationTokenSource();

        _mockDocOps
            .Setup(d => d.PutAsync(It.IsAny<string>(), It.IsAny<object>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<DocumentRequestOptions?>(), It.IsAny<CancellationToken>()))
            .Returns<string, object, string, string?, string?, DocumentRequestOptions?, CancellationToken>((_, _, _, _, _, _, ct) =>
            {
                cts.Cancel();
                return Task.FromCanceled<VespaResponse>(ct);
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _feedOps.FeedAsync(GenerateDocuments(3), "testdoc", maxConcurrency: 1, cancellationToken: cts.Token));
    }

    private static async IAsyncEnumerable<FeedDocument<object>> GenerateDocuments(int count)
    {
        for (var i = 0; i < count; i++)
        {
            await Task.Yield();
            yield return new FeedDocument<object> { Id = $"doc-{i}", Fields = new { value = i } };
        }
    }

    #endregion

    #region BulkUpdateAsync Tests (7 tests)

    private static BulkFieldUpdate MakeUpdate(string id, string? condition = null) => new()
    {
        Id = id,
        FieldOperations = new() { ["name"] = FieldOp.Assign($"Updated {id}") },
        Condition = condition
    };

    [Fact]
    public async Task BulkUpdateAsync_WithValidUpdates_ReturnsSuccessResult()
    {
        var updates = new[] { MakeUpdate("doc-1"), MakeUpdate("doc-2") };

        _mockDocOps
            .Setup(d => d.UpdateFieldsAsync(It.IsAny<string>(),
                It.IsAny<Dictionary<string, FieldOperation>>(),
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>(),
                It.IsAny<string?>(), It.IsAny<DocumentRequestOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VespaResponse { IsSuccess = true, StatusCode = 200 });

        var result = await _feedOps.BulkUpdateAsync(updates, "testdoc");

        Assert.Equal(2, result.TotalDocuments);
        Assert.Equal(2, result.SuccessCount);
        Assert.Equal(0, result.FailureCount);
        Assert.True(result.IsSuccess);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task BulkUpdateAsync_WithEmptyList_ReturnsZeroResult()
    {
        var result = await _feedOps.BulkUpdateAsync(
            Array.Empty<BulkFieldUpdate>(), "testdoc");

        Assert.Equal(0, result.TotalDocuments);
        Assert.Equal(0, result.SuccessCount);
        Assert.True(result.IsSuccess);
        _mockDocOps.Verify(d => d.UpdateFieldsAsync(
            It.IsAny<string>(), It.IsAny<Dictionary<string, FieldOperation>>(),
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<string?>(),
            It.IsAny<DocumentRequestOptions?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task BulkUpdateAsync_WithCreateIfMissing_PassesFlagToUpdateFieldsAsync()
    {
        var updates = new[] { MakeUpdate("doc-1") };

        _mockDocOps
            .Setup(d => d.UpdateFieldsAsync(It.IsAny<string>(),
                It.IsAny<Dictionary<string, FieldOperation>>(),
                It.IsAny<string>(), It.IsAny<string?>(), true,
                It.IsAny<string?>(), It.IsAny<DocumentRequestOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VespaResponse { IsSuccess = true, StatusCode = 200 });

        var result = await _feedOps.BulkUpdateAsync(updates, "music", createIfMissing: true);

        Assert.Equal(1, result.SuccessCount);
        _mockDocOps.Verify(d => d.UpdateFieldsAsync(
            "doc-1", It.IsAny<Dictionary<string, FieldOperation>>(), "music",
            It.IsAny<string?>(), true, It.IsAny<string?>(),
            It.IsAny<DocumentRequestOptions?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task BulkUpdateAsync_PartialFailure_TracksErrorsCorrectly()
    {
        var updates = new[] { MakeUpdate("doc-1"), MakeUpdate("doc-2") };

        _mockDocOps
            .Setup(d => d.UpdateFieldsAsync("doc-1", It.IsAny<Dictionary<string, FieldOperation>>(),
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>(),
                It.IsAny<string?>(), It.IsAny<DocumentRequestOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VespaResponse { IsSuccess = true, StatusCode = 200 });

        _mockDocOps
            .Setup(d => d.UpdateFieldsAsync("doc-2", It.IsAny<Dictionary<string, FieldOperation>>(),
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>(),
                It.IsAny<string?>(), It.IsAny<DocumentRequestOptions?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new VespaException("Not found", 404));

        var result = await _feedOps.BulkUpdateAsync(updates, "music");

        Assert.Equal(2, result.TotalDocuments);
        Assert.Equal(1, result.SuccessCount);
        Assert.Equal(1, result.FailureCount);
        Assert.False(result.IsSuccess);
        Assert.Single(result.Errors);
        Assert.Equal("doc-2", result.Errors.First().DocumentId);
        Assert.Equal(404, result.Errors.First().StatusCode);
    }

    [Fact]
    public async Task BulkUpdateAsync_WithPerDocumentCondition_PassesConditionThrough()
    {
        var update = MakeUpdate("doc-1", condition: "music.year > 2000");

        _mockDocOps
            .Setup(d => d.UpdateFieldsAsync(It.IsAny<string>(),
                It.IsAny<Dictionary<string, FieldOperation>>(),
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>(),
                "music.year > 2000", It.IsAny<DocumentRequestOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VespaResponse { IsSuccess = true, StatusCode = 200 });

        var result = await _feedOps.BulkUpdateAsync([update], "music");

        Assert.Equal(1, result.SuccessCount);
        _mockDocOps.Verify(d => d.UpdateFieldsAsync(
            "doc-1", It.IsAny<Dictionary<string, FieldOperation>>(), "music",
            It.IsAny<string?>(), It.IsAny<bool>(), "music.year > 2000",
            It.IsAny<DocumentRequestOptions?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task BulkUpdateAsync_WithNullCondition_PassesNullThrough()
    {
        var update = MakeUpdate("doc-1");

        _mockDocOps
            .Setup(d => d.UpdateFieldsAsync(It.IsAny<string>(),
                It.IsAny<Dictionary<string, FieldOperation>>(),
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>(),
                null, It.IsAny<DocumentRequestOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VespaResponse { IsSuccess = true, StatusCode = 200 });

        var result = await _feedOps.BulkUpdateAsync([update], "music");

        Assert.Equal(1, result.SuccessCount);
        _mockDocOps.Verify(d => d.UpdateFieldsAsync(
            "doc-1", It.IsAny<Dictionary<string, FieldOperation>>(), "music",
            It.IsAny<string?>(), false, null,
            It.IsAny<DocumentRequestOptions?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task BulkUpdateAsync_WithInvalidConcurrency_ThrowsArgumentOutOfRangeException()
    {
        var updates = new[] { MakeUpdate("doc-1") };

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            _feedOps.BulkUpdateAsync(updates, "music", maxConcurrency: 0));
    }

    #endregion
}
