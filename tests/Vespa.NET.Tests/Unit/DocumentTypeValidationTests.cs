using Vespa.Documents;
using Vespa.Feed;
using Vespa.Models;
using Vespa.Tests.Helpers;
using Xunit;

namespace Vespa.Tests.Unit;

public sealed class DocumentTypeValidationTests : IDisposable
{
    private readonly MockHttpMessageHandler _handler = new();
    private readonly HttpClient _http;
    private readonly DocumentOperations _documents;

    public DocumentTypeValidationTests()
    {
        _http = new(_handler) { BaseAddress = new Uri("http://localhost:8080") };
        _documents = new(_http, new());
    }

    public void Dispose() => _http.Dispose();

    [Theory]
    [InlineData("product.price > 0")]
    [InlineData("inventory:unit_price")]
    [InlineData("type/../sneaky")]
    [InlineData("1product")]
    [InlineData("café")]
    [InlineData("true")]
    [InlineData("FALSE")]
    [InlineData("And")]
    [InlineData("or")]
    [InlineData("NOT")]
    [InlineData("null")]
    [InlineData("Id")]
    [InlineData("")]
    [InlineData(" ")]
    public async Task InvalidPutDocumentTypeIsRejectedWithoutHttp(string documentType)
    {
        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            _documents.PutAsync("row", new Product { Price = 2 }, documentType));
        Assert.Equal("documentType", error.ParamName);
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task InvalidDocumentTypeIsRejectedForReadsAndSelectionMutations()
    {
        const string documentType = "product.price > 0";
        await Assert.ThrowsAsync<ArgumentException>(() => _documents.GetAsync<Product>("row", documentType));
        await Assert.ThrowsAsync<ArgumentException>(() => _documents.GetByGroupAsync<Product>("team", "row", documentType));
        await Assert.ThrowsAsync<ArgumentException>(() => _documents.GetByNumberAsync<Product>(42, "row", documentType));
        await Assert.ThrowsAsync<ArgumentException>(() => _documents.DeleteBySelectionAsync("true", documentType));
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task BulkFeedRejectsAnInvalidTargetWithoutHttp()
    {
        var feed = new FeedOperations(_documents, new());
        var row = new FeedDocument<Product> { Id = "row", Fields = new() { Price = 2 } };
        var result = await feed.BulkPutAsync([row], "product.price > 0");
        Assert.Equal(1, result.FailureCount);
        Assert.Null(Assert.Single(result.Errors).StatusCode);
        Assert.Empty(_handler.Requests);
    }

    private sealed class Product
    {
        public int Price { get; init; }
    }
}
