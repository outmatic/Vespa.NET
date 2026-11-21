using System.Linq.Expressions;
using System.Net;
using System.Text.Json;
using Vespa.Documents;
using Vespa.Models;
using Vespa.Models.Attributes;
using Vespa.Tests.Helpers;
using Xunit;

namespace Vespa.Tests.Unit;

public sealed class TypedDocumentConditionTests
{
    [Theory]
    [InlineData("put", "POST", "docid/item")]
    [InlineData("delete", "DELETE", "docid/item")]
    [InlineData("update", "PUT", "docid/item")]
    [InlineData("update-builder", "PUT", "docid/item")]
    [InlineData("group-put", "POST", "group/team/item")]
    [InlineData("number-put", "POST", "number/42/item")]
    [InlineData("group-delete", "DELETE", "group/team/item")]
    [InlineData("number-delete", "DELETE", "number/42/item")]
    [InlineData("group-update", "PUT", "group/team/item")]
    [InlineData("group-update-builder", "PUT", "group/team/item")]
    [InlineData("number-update", "PUT", "number/42/item")]
    [InlineData("number-update-builder", "PUT", "number/42/item")]
    public async Task TypedConditions_ForwardMetadataOptionsAndMutationBody(string operation, string method, string path)
    {
        var handler = new MockHttpMessageHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8080") };
        IDocumentOperations documents = new DocumentOperations(http, new() { DefaultNamespace = "fallback" });
        handler.QueueResponse(new(HttpStatusCode.OK) { Content = new StringContent("{}") });

        var options = new DocumentRequestOptions { Timeout = TimeSpan.FromSeconds(2), TraceLevel = 3, DryRun = true };
        var product = new Inventory { Price = 12 };
        Dictionary<string, FieldOperation> fields = new() { ["unit_price"] = FieldOp.Assign(12) };
        Action<TypedFieldUpdateBuilder<Inventory>> configure = update => update.Field(p => p.Price, FieldOp.Assign(12));
        Expression<Func<Inventory, bool>> condition = p => p.Price > 0;

        switch (operation)
        {
            case "put": await documents.PutAsync("item", product, condition: p => p.Price > 0, create: true, requestOptions: options); break;
            case "delete": await documents.DeleteAsync("item", condition, requestOptions: options); break;
            case "update": await documents.UpdateFieldsAsync("item", fields, condition: condition, createIfMissing: true, requestOptions: options); break;
            case "update-builder": await documents.UpdateFieldsAsync<Inventory>("item", update => update.Field(p => p.Price, FieldOp.Assign(12)), condition: p => p.Price > 0, createIfMissing: true, requestOptions: options); break;
            case "group-put": await documents.PutByGroupAsync("team", "item", product, condition, create: true, requestOptions: options); break;
            case "number-put": await documents.PutByNumberAsync(42, "item", product, condition, create: true, requestOptions: options); break;
            case "group-delete": await documents.DeleteByGroupAsync("team", "item", condition, requestOptions: options); break;
            case "number-delete": await documents.DeleteByNumberAsync(42, "item", condition, requestOptions: options); break;
            case "group-update": await documents.UpdateFieldsByGroupAsync("team", "item", fields, condition: condition, createIfMissing: true, requestOptions: options); break;
            case "group-update-builder": await documents.UpdateFieldsByGroupAsync("team", "item", configure, condition: condition, createIfMissing: true, requestOptions: options); break;
            case "number-update": await documents.UpdateFieldsByNumberAsync(42, "item", fields, condition: condition, createIfMissing: true, requestOptions: options); break;
            case "number-update-builder": await documents.UpdateFieldsByNumberAsync(42, "item", configure, condition: condition, createIfMissing: true, requestOptions: options); break;
        }

        var request = Assert.Single(handler.Requests);
        Assert.Equal(method, request.Method.Method);
        Assert.Equal($"/document/v1/tenant/inventory/{path}", request.RequestUri!.AbsolutePath);
        var query = Query(request.RequestUri);
        Assert.Equal("(inventory.unit_price > 0)", query["condition"]);
        Assert.Equal("2000ms", query["timeout"]);
        Assert.Equal("3", query["tracelevel"]);
        Assert.Equal("true", query["dryRun"]);
        if (method != "DELETE")
        {
            Assert.Equal("true", query["create"]);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var value = body.RootElement.GetProperty("fields").GetProperty("unit_price");
            Assert.Equal(12, method == "POST" ? value.GetInt32() : value.GetProperty("assign").GetInt32());
        }
    }

    [Fact]
    public async Task FailedCondition_PreservesTheExistingException()
    {
        var handler = new MockHttpMessageHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8080") };
        IDocumentOperations documents = new DocumentOperations(http, new());
        handler.QueueResponse(new(HttpStatusCode.PreconditionFailed)
        {
            Content = new StringContent("""{"message":"Condition did not match"}""")
        });
        await Assert.ThrowsAsync<VespaConditionNotMetException>(() => documents.DeleteAsync<Inventory>("item", condition: p => p.Price > 0));
    }

    [Fact]
    public async Task CallerCancellation_StopsTheRequest()
    {
        var handler = new MockHttpMessageHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8080") };
        IDocumentOperations documents = new DocumentOperations(http, new());
        using var source = new CancellationTokenSource();
        source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => documents.PutAsync("item", new Inventory(), p => p.Price > 0, cancellationToken: source.Token));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task UnsupportedPredicate_FailsBeforeSendingARequest()
    {
        var handler = new MockHttpMessageHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8080") };
        IDocumentOperations documents = new DocumentOperations(http, new());
        await Assert.ThrowsAsync<NotSupportedException>(() => documents.DeleteAsync<Inventory>("item", p => Math.Abs(p.Price) > 0));
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("put")]
    [InlineData("delete")]
    [InlineData("update")]
    [InlineData("update-builder")]
    [InlineData("group-put")]
    [InlineData("number-put")]
    [InlineData("group-delete")]
    [InlineData("number-delete")]
    [InlineData("group-update")]
    [InlineData("group-update-builder")]
    [InlineData("number-update")]
    [InlineData("number-update-builder")]
    public async Task ConflictingConditions_FailBeforeHttpOrBuilderCallbacks(string operation)
    {
        var handler = new MockHttpMessageHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8080") };
        IDocumentOperations documents = new DocumentOperations(http, new());
        Expression<Func<Inventory, bool>> condition = p => p.Price > 0;
        Dictionary<string, FieldOperation> fields = new() { ["unit_price"] = FieldOp.Assign(1) };
        var callbackInvoked = false;
        Action<TypedFieldUpdateBuilder<Inventory>> configure = _ => callbackInvoked = true;

        await Assert.ThrowsAsync<ArgumentException>(() => operation switch
        {
            "put" => documents.PutAsync("item", new Inventory(), condition: condition, rawCondition: ""),
            "delete" => documents.DeleteAsync("item", condition: condition, rawCondition: ""),
            "update" => documents.UpdateFieldsAsync("item", fields, condition: condition, rawCondition: ""),
            "update-builder" => documents.UpdateFieldsAsync("item", configure, condition: condition, rawCondition: ""),
            "group-put" => documents.PutByGroupAsync("team", "item", new Inventory(), condition: condition, rawCondition: ""),
            "number-put" => documents.PutByNumberAsync(42, "item", new Inventory(), condition: condition, rawCondition: ""),
            "group-delete" => documents.DeleteByGroupAsync("team", "item", condition: condition, rawCondition: ""),
            "number-delete" => documents.DeleteByNumberAsync(42, "item", condition: condition, rawCondition: ""),
            "group-update" => documents.UpdateFieldsByGroupAsync("team", "item", fields, condition: condition, rawCondition: ""),
            "group-update-builder" => documents.UpdateFieldsByGroupAsync("team", "item", configure, condition: condition, rawCondition: ""),
            "number-update" => documents.UpdateFieldsByNumberAsync(42, "item", fields, condition: condition, rawCondition: ""),
            "number-update-builder" => documents.UpdateFieldsByNumberAsync(42, "item", configure, condition: condition, rawCondition: ""),
            _ => throw new InvalidOperationException(operation)
        });
        Assert.False(callbackInvoked);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task NullDefaultAndOmittedConditions_AreUnconditional()
    {
        var handler = new MockHttpMessageHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8080") };
        IDocumentOperations documents = new DocumentOperations(http, new() { DefaultNamespace = "fallback" });
        for (var i = 0; i < 6; i++)
            handler.QueueResponse(new(HttpStatusCode.OK) { Content = new StringContent("{}") });
        var product = new Inventory();
        await documents.PutAsync("item", product);
        await documents.PutAsync("item", product, condition: null);
        await documents.PutAsync("item", product, condition: default);
        await documents.DeleteAsync<Inventory>("item", condition: null);
        await documents.UpdateFieldsAsync<Inventory>("item", update => update.Field(p => p.Price, FieldOp.Assign(1)), condition: default);
        await documents.PutByGroupAsync("team", "item", product, condition: null, rawCondition: null);
        Assert.Equal(6, handler.Requests.Count);
        Assert.All(handler.Requests, request =>
        {
            Assert.StartsWith("/document/v1/tenant/inventory/", request.RequestUri!.AbsolutePath);
            Assert.False(Query(request.RequestUri).ContainsKey("condition"));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcreteAndInterfaceReceivers_DistinguishInferredConditionsFromExplicitTargets(bool useInterface)
    {
        var handler = new MockHttpMessageHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8080") };
        var concrete = new DocumentOperations(http, new() { DefaultNamespace = "fallback" });
        IDocumentOperations documents = concrete;
        for (var i = 0; i < 3; i++)
            handler.QueueResponse(new(HttpStatusCode.OK) { Content = new StringContent("{}") });
        var product = new Inventory();
        const string rawCondition = "inventory.unit_price > 0";
        if (useInterface)
        {
            await documents.PutAsync("item", product, rawCondition: rawCondition);
            await documents.PutAsync("item", product, documentType: "explicit", @namespace: "target", condition: "explicit.unit_price > 0");
            await documents.PutAsync("item", product, "positional_target");
        }
        else
        {
            await concrete.PutAsync("item", product, rawCondition: rawCondition);
            await concrete.PutAsync("item", product, documentType: "explicit", @namespace: "target", condition: "explicit.unit_price > 0");
            await concrete.PutAsync("item", product, "positional_target");
        }
        Assert.Equal("/document/v1/tenant/inventory/docid/item", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal(rawCondition, Query(handler.Requests[0].RequestUri!)["condition"]);
        Assert.Equal("/document/v1/target/explicit/docid/item", handler.Requests[1].RequestUri!.AbsolutePath);
        Assert.Equal("explicit.unit_price > 0", Query(handler.Requests[1].RequestUri!)["condition"]);
        Assert.Equal("/document/v1/fallback/positional_target/docid/item", handler.Requests[2].RequestUri!.AbsolutePath);
        Assert.False(Query(handler.Requests[2].RequestUri!).ContainsKey("condition"));
    }

    [Fact]
    public async Task PredicateRawAndCreationSlots_ForwardToTheNativeOperation()
    {
        var handler = new MockHttpMessageHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8080") };
        IDocumentOperations documents = new DocumentOperations(http, new());
        for (var i = 0; i < 3; i++)
            handler.QueueResponse(new(HttpStatusCode.OK) { Content = new StringContent("{}") });
        var product = new Inventory();
        Dictionary<string, FieldOperation> fields = new() { ["unit_price"] = FieldOp.Assign(1) };
        Action<TypedFieldUpdateBuilder<Inventory>> configure = update => update.Field(p => p.Price, FieldOp.Assign(1));
        const string selector = "inventory.unit_price > 0";

        await DocumentOperationsExtensions.PutAsync(documents, "item", product, null, selector, true);
        await DocumentOperationsExtensions.UpdateFieldsAsync<Inventory>(documents, "item", fields, null, selector, true);
        await DocumentOperationsExtensions.UpdateFieldsAsync(documents, "item", configure, null, selector, true);

        Assert.Equal(3, handler.Requests.Count);
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal("/document/v1/tenant/inventory/docid/item", request.RequestUri!.AbsolutePath);
            var query = Query(request.RequestUri);
            Assert.Equal(selector, query["condition"]);
            Assert.Equal("true", query["create"]);
        });
    }

    private static Dictionary<string, string> Query(Uri uri) => uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(part => part.Split('=', 2)).ToDictionary(part => Uri.UnescapeDataString(part[0]), part => Uri.UnescapeDataString(part[1]));

    [VespaDocument("inventory", Namespace = "tenant")]
    private sealed class Inventory
    {
        [VespaField(Name = "unit_price")] public int Price { get; init; }
    }
}
