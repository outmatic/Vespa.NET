using System.Net;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Vespa.AgentFrameworkSample;
using Xunit;

namespace Vespa.VectorData.Tests;

public sealed class MemoryLoggingTests
{
    [Fact]
    public async Task NativeMemoryFailures_AreVisibleEvenWhenTheModelRespondsSuccessfully()
    {
        using var output = new StringWriter();
        using var loggerFactory = LoggerFactory.Create(builder => builder
            .SetMinimumLevel(LogLevel.Warning).AddProvider(new SampleDiagnosticLoggerProvider(output)));
        using var harness = new TestHarness((_, _) => TestHarness.Json("""{"message":"private backend detail"}""", HttpStatusCode.ServiceUnavailable));
        using var embeddings = new BatchEmbeddingGenerator();
        using var store = new VespaVectorStore(harness.Client, TestHarness.Options with { EmbeddingGenerator = embeddings });
        using var provider = new ChatHistoryMemoryProvider(store, "chathistory", 3,
            _ => new ChatHistoryMemoryProvider.State(new() { UserId = "alice" }, new() { UserId = "alice" }), loggerFactory: loggerFactory);
        using var model = new NativeContextChatClient();
        var agent = model.AsAIAgent(new ChatClientAgentOptions { AIContextProviders = [provider] });
        var response = await agent.RunAsync("private conversation detail");
        Assert.Equal("Acknowledged.", response.Text);
        Assert.Contains("Memory persistence or retrieval may be incomplete", output.ToString());
        Assert.DoesNotContain("private backend detail", output.ToString());
        Assert.DoesNotContain("private conversation detail", output.ToString());
    }
}
