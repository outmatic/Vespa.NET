#pragma warning disable MAAI001, MEAI001
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Xunit;

namespace Vespa.AgentFramework.Tests;

public sealed class CompactionTests
{
    [Theory]
    [InlineData("sliding")]
    [InlineData("truncation")]
    [InlineData("tools")]
    [InlineData("summary")]
    [InlineData("pipeline")]
    [InlineData("reducer-adapter")]
    public async Task MicrosoftStrategies_PreserveSystemInstructionsAndToolPairIntegrity(string strategyName)
    {
        using var summarizer = new PatternChatClient();
        CompactionStrategy strategy = strategyName switch
        {
            "sliding" => new SlidingWindowCompactionStrategy(CompactionTriggers.TurnsExceed(1)),
            "truncation" => new TruncationCompactionStrategy(CompactionTriggers.MessagesExceed(3), minimumPreservedGroups: 1),
            "tools" => new ToolResultCompactionStrategy(CompactionTriggers.Always, minimumPreservedGroups: 1),
            "summary" => new SummarizationCompactionStrategy(summarizer, CompactionTriggers.Always, minimumPreservedGroups: 1),
            "pipeline" => new PipelineCompactionStrategy(
                new ToolResultCompactionStrategy(CompactionTriggers.Always, minimumPreservedGroups: 1),
                new SummarizationCompactionStrategy(summarizer, CompactionTriggers.Always, minimumPreservedGroups: 1),
                new SlidingWindowCompactionStrategy(CompactionTriggers.TurnsExceed(1)),
                new TruncationCompactionStrategy(CompactionTriggers.MessagesExceed(3), minimumPreservedGroups: 1)),
            _ => new ChatReducerCompactionStrategy(new MessageCountingChatReducer(3), CompactionTriggers.Always)
        };
        ChatMessage[] original =
        [
            new(ChatRole.System, "Keep these instructions."),
            new(ChatRole.User, "Old question."),
            new(ChatRole.Assistant, [new FunctionCallContent("call-1", "lookup", new Dictionary<string, object?>())]),
            new(ChatRole.Tool, [new FunctionResultContent("call-1", new string('x', 10000))]),
            new(ChatRole.Assistant, "Old answer."),
            new(ChatRole.User, "Current question.")
        ];
        var compacted = (await CompactionProvider.CompactAsync(strategy, original)).ToList();
        Assert.Contains(compacted, m => m.Role == ChatRole.System && m.Text == "Keep these instructions.");
        Assert.Contains(compacted, m => m.Text == "Current question.");
        Assert.True(compacted.Count < original.Length);
        var calls = compacted.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Select(c => c.CallId).Order().ToArray();
        var results = compacted.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Select(c => c.CallId).Order().ToArray();
        Assert.Equal(calls, results);
        if (strategyName is "summary" or "pipeline")
            Assert.NotEmpty(summarizer.LastMessages);
    }
}
