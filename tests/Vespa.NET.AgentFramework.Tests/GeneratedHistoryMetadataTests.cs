using System.Text.Json;
using Microsoft.Extensions.AI;
using Xunit;

namespace Vespa.AgentFramework.Tests;

public sealed class GeneratedHistoryMetadataTests
{
    [Fact]
    public async Task GeneratedHistoryMetadataRoundTripsToolResultsThroughSnapshotAndFileStores()
    {
        using var fixture = new StateFixture();
        var options = new JsonSerializerOptions { TypeInfoResolver = AgentStorageJsonContext.Default };
        var typeInfo = options.GetTypeInfo(typeof(List<ChatMessage>));
        var id = Guid.Parse("da5f186f-9370-49f5-baa9-ccfdd11f7bd1");
        var utc = new DateTime(2026, 11, 26, 12, 30, 0, DateTimeKind.Utc);
        object[] results =
        [
            1, 2L, 3.25d, 4.5f, 5.75m, true, "ok", id, utc,
            new[] { 1 }, new[] { 2L }, new[] { 3.25d }, new[] { 4.5f }, new[] { 5.75m },
            new[] { true }, new[] { "ok" }, new[] { id }, new[] { utc }
        ];
        var messages = results.Select(result =>
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-1", result)])).ToList();
        var payload = JsonSerializer.SerializeToElement(messages, typeInfo);
        var snapshot = await fixture.Store.WriteAsync("history/generated", payload, null);
        var stored = await fixture.Store.ReadAsync("history/generated");
        Assert.NotNull(stored);
        Assert.Equal(snapshot.Revision, stored.Revision);
        Assert.Equal(payload.GetRawText(), stored.Payload.GetRawText());

        var files = new VespaAgentFileStore(fixture.Store);
        await files.WriteAsync("history.json", stored.Payload.GetRawText());
        var reloaded = await new VespaAgentFileStore(fixture.Store).ReadAsync("history.json");
        Assert.Equal(payload.GetRawText(), reloaded);
        var decoded = Assert.IsType<List<ChatMessage>>(JsonSerializer.Deserialize(reloaded!, typeInfo));
        Assert.Equal(results.Length, decoded.Count);
        for (var i = 0; i < results.Length; i++)
        {
            var result = Assert.IsType<FunctionResultContent>(Assert.Single(decoded[i].Contents));
            Assert.Equal("call-1", result.CallId);
            if (result.Result is JsonElement element)
                Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(results[i]), element));
            else
                Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(results[i]),
                    JsonSerializer.SerializeToElement(result.Result)));
        }
    }
}
