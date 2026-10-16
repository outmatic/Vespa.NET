using System.Net;
using System.Text.Json;
using Vespa.Documents;
using Vespa.Models;
using Vespa.Models.Attributes;
using Vespa.Tests.Helpers;
using Xunit;

namespace Vespa.Tests.Unit;

public sealed class TypedDocumentOptionsTests
{
    [Theory]
    [InlineData("put", "POST", "docid/item")]
    [InlineData("get", "GET", "docid/item")]
    [InlineData("delete", "DELETE", "docid/item")]
    [InlineData("update", "PUT", "docid/item")]
    [InlineData("update-builder", "PUT", "docid/item")]
    [InlineData("group-get", "GET", "group/team/item")]
    [InlineData("number-get", "GET", "number/42/item")]
    [InlineData("group-update", "PUT", "group/team/item")]
    [InlineData("group-update-builder", "PUT", "group/team/item")]
    [InlineData("number-update", "PUT", "number/42/item")]
    [InlineData("number-update-builder", "PUT", "number/42/item")]
    [InlineData("get-many", "GET", "docid/item")]
    [InlineData("get-many-explicit", "GET", "docid/item")]
    [InlineData("group-put", "POST", "group/team/item")]
    [InlineData("number-put", "POST", "number/42/item")]
    [InlineData("group-delete", "DELETE", "group/team/item")]
    [InlineData("number-delete", "DELETE", "number/42/item")]
    public async Task TypedOperations_ForwardRequestOptionsAndModelMetadata(string operation, string method, string path)
    {
        var handler = new MockHttpMessageHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8080") };
        IDocumentOperations documents = new DocumentOperations(http, new() { DefaultNamespace = "fallback" });
        handler.QueueResponse(new(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"id":"id:tenant:inventory::item","fields":{"unit_price":12}}""")
        });

        var options = new DocumentRequestOptions
        {
            Timeout = TimeSpan.FromSeconds(2),
            TraceLevel = 3,
            Cluster = method == "GET" ? "storage" : null,
            TensorFormat = method == "GET" ? "long" : null,
            Route = method != "GET" ? "default/chain.indexing" : null,
            DryRun = method != "GET"
        };
        Dictionary<string, FieldOperation> fields = new() { ["unit_price"] = FieldOp.Assign(12) };
        Action<TypedFieldUpdateBuilder<Inventory>> configure = update => update.Field(x => x.Price, FieldOp.Assign(12));
        const string condition = "inventory.unit_price >= 0";

        switch (operation)
        {
            case "put":
                await documents.PutAsync(options, "item", new Inventory { Price = 12 }, condition: condition, create: true);
                break;
            case "get":
                await documents.GetAsync<Inventory>(options, "item", fieldSet: "inventory:unit_price");
                break;
            case "delete":
                await documents.DeleteAsync<Inventory>(options, "item", condition: condition);
                break;
            case "update":
                await documents.UpdateFieldsAsync<Inventory>(options, "item", fields, createIfMissing: true, condition: condition);
                break;
            case "update-builder":
                await documents.UpdateFieldsAsync(options, "item", configure, createIfMissing: true, condition: condition);
                break;
            case "group-get":
                await documents.GetByGroupAsync<Inventory>(options, "team", "item", fieldSet: "inventory:unit_price");
                break;
            case "number-get":
                await documents.GetByNumberAsync<Inventory>(options, 42, "item", fieldSet: "inventory:unit_price");
                break;
            case "group-update":
                await documents.UpdateFieldsByGroupAsync<Inventory>(options, "team", "item", fields, createIfMissing: true, condition: condition);
                break;
            case "group-update-builder":
                await documents.UpdateFieldsByGroupAsync(options, "team", "item", configure, createIfMissing: true, condition: condition);
                break;
            case "number-update":
                await documents.UpdateFieldsByNumberAsync<Inventory>(options, 42, "item", fields, createIfMissing: true, condition: condition);
                break;
            case "number-update-builder":
                await documents.UpdateFieldsByNumberAsync(options, 42, "item", configure, createIfMissing: true, condition: condition);
                break;
            case "get-many":
                await documents.GetManyAsync<Inventory>(options, ["item"]);
                break;
            case "get-many-explicit":
                await documents.GetManyAsync<Inventory>(options, ["item"], "inventory", "tenant");
                break;
            case "group-put":
                await documents.PutByGroupAsync("team", "item", new Inventory { Price = 12 }, condition: condition, create: true, requestOptions: options);
                break;
            case "number-put":
                await documents.PutByNumberAsync(42, "item", new Inventory { Price = 12 }, condition: condition, create: true, requestOptions: options);
                break;
            case "group-delete":
                await documents.DeleteByGroupAsync<Inventory>("team", "item", condition: condition, requestOptions: options);
                break;
            case "number-delete":
                await documents.DeleteByNumberAsync<Inventory>(42, "item", condition: condition, requestOptions: options);
                break;
        }

        var request = Assert.Single(handler.Requests);
        Assert.Equal(method, request.Method.Method);
        Assert.Equal($"/document/v1/tenant/inventory/{path}", request.RequestUri!.AbsolutePath);
        var query = Query(request.RequestUri);
        Assert.Equal("2000ms", query["timeout"]);
        Assert.Equal("3", query["tracelevel"]);
        if (method == "GET")
        {
            Assert.Equal("storage", query["cluster"]);
            Assert.Equal("long", query["format.tensors"]);
            if (!operation.StartsWith("get-many", StringComparison.Ordinal))
                Assert.Equal("inventory:unit_price", query["fieldSet"]);
        }
        else
        {
            Assert.Equal("default/chain.indexing", query["route"]);
            Assert.Equal("true", query["dryRun"]);
            Assert.Equal(condition, query["condition"]);
            if (method != "DELETE")
            {
                Assert.Equal("true", query["create"]);
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                var price = body.RootElement.GetProperty("fields").GetProperty("unit_price");
                Assert.Equal(12, method == "POST" ? price.GetInt32() : price.GetProperty("assign").GetInt32());
            }
        }
    }

    [Fact]
    public async Task ExistingPositionalSignatures_AcceptDefaultCancellationTokens()
    {
        var handler = new MockHttpMessageHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8080") };
        IDocumentOperations documents = new DocumentOperations(http, new() { DefaultNamespace = "fallback" });
        for (var i = 0; i < 13; i++)
            handler.QueueResponse(new(HttpStatusCode.OK) { Content = new StringContent("{}") });

        Dictionary<string, FieldOperation> fields = new() { ["unit_price"] = FieldOp.Assign(12) };
        Action<TypedFieldUpdateBuilder<Inventory>> configure = update => update.Field(x => x.Price, FieldOp.Assign(12));

        await DocumentOperationsExtensions.PutAsync(documents, "item", new Inventory(), null, false, default);
        await DocumentOperationsExtensions.GetAsync<Inventory>(documents, "item", null, default);
        await DocumentOperationsExtensions.DeleteAsync<Inventory>(documents, "item", null, default);
        await DocumentOperationsExtensions.UpdateFieldsAsync<Inventory>(documents, "item", fields, false, null, default);
        await DocumentOperationsExtensions.UpdateFieldsAsync(documents, "item", configure, false, null, default);
        await DocumentOperationsExtensions.UpdateFieldsByGroupAsync<Inventory>(documents, "team", "item", fields, false, null, default);
        await DocumentOperationsExtensions.UpdateFieldsByGroupAsync(documents, "team", "item", configure, false, null, default);
        await DocumentOperationsExtensions.UpdateFieldsByNumberAsync<Inventory>(documents, 42, "item", fields, false, null, default);
        await DocumentOperationsExtensions.UpdateFieldsByNumberAsync(documents, 42, "item", configure, false, null, default);
        await DocumentOperationsExtensions.GetByGroupAsync<Inventory>(documents, "team", "item", null, default);
        await DocumentOperationsExtensions.GetByNumberAsync<Inventory>(documents, 42, "item", null, default);
        await DocumentOperationsExtensions.GetManyAsync<Inventory>(documents, ["item"], "inventory", "tenant", 1, default);
        await DocumentOperationsExtensions.GetManyAsync<Inventory>(documents, ["item"], 1, default);

        Assert.Equal(13, handler.Requests.Count);
        Assert.All(handler.Requests, request => Assert.StartsWith("/document/v1/tenant/inventory/", request.RequestUri!.AbsolutePath));
    }

    [Fact]
    public async Task OptionsOverload_ForwardsCallerCancellation()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        using var http = new HttpClient(new MockHttpMessageHandler()) { BaseAddress = new Uri("http://localhost:8080") };
        IDocumentOperations documents = new DocumentOperations(http, new());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            documents.GetAsync<Inventory>(new DocumentRequestOptions { Cluster = "storage" }, "item", cancellationToken: source.Token));
    }

    private static Dictionary<string, string> Query(Uri uri) => uri.Query.TrimStart('?').Split('&')
        .Select(part => part.Split('=', 2))
        .ToDictionary(pair => pair[0], pair => Uri.UnescapeDataString(pair[1]));

    [VespaDocument("inventory", Namespace = "tenant")]
    public sealed class Inventory
    {
        [VespaField(Name = "unit_price")]
        public int Price { get; init; }
    }
}
