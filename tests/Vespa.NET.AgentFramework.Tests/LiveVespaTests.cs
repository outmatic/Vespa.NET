#pragma warning disable MAAI001
using System.IO.Compression;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Vespa.Models;
using Vespa.Testcontainers;
using Xunit;

namespace Vespa.AgentFramework.Tests;

public sealed class LiveVespaTests
{
    [AgentPersistenceFact]
    public async Task OrderedHistorySessionsFileMemoryAndConditionalWrites_WorkAgainstRealVespa()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        var ct = timeout.Token;
        await using var container = new VespaContainer(Environment.GetEnvironmentVariable("VESPA_VECTOR_DATA_IMAGE") ?? VespaContainer.DefaultImage);
        await container.StartAsync(ct);
        using var client = container.CreateClient("knowledge");
        using var package = new MemoryStream();
        using (var archive = new ZipArchive(package, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var path in Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "app"), "*", SearchOption.AllDirectories))
            {
                var entry = archive.CreateEntry(Path.GetRelativePath(Path.Combine(AppContext.BaseDirectory, "app"), path).Replace('\\', '/'));
                await using var target = entry.Open();
                var text = await File.ReadAllTextAsync(path, ct);
                if (path.EndsWith("services.xml", StringComparison.Ordinal))
                    text = text.Replace("</content>", "<tuning><resource-limits><disk>0.95</disk></resource-limits></tuning></content>", StringComparison.Ordinal);
                await using var writer = new StreamWriter(target);
                await writer.WriteAsync(text.AsMemory(), ct);
            }
        package.Position = 0;
        await client.Admin.DeployAsync(package, cancellationToken: ct);
        while (!await client.IsReadyAsync(ct))
            await Task.Delay(500, ct);

        var store = new VespaAgentStateStore(client, new() { Namespace = "knowledge", Scope = "alice" });
        var first = await store.WriteAsync("concurrency", JsonSerializer.SerializeToElement("first"), null, ct);
        await store.WriteAsync("concurrency", JsonSerializer.SerializeToElement("next"), first.Revision, ct);
        await Assert.ThrowsAsync<VespaConditionNotMetException>(() => store.WriteAsync("concurrency", first.Payload, first.Revision, ct));
        await Assert.ThrowsAsync<VespaConditionNotMetException>(() => store.WriteAsync("concurrency", first.Payload, null, ct));

        var history = new VespaChatHistoryProvider(store, _ => new() { ConversationId = "conversation" });
        using var model = new PatternChatClient();
        var agent = model.AsAIAgent(new ChatClientAgentOptions { Name = "persisted", ChatHistoryProvider = history });
        var session = await agent.CreateSessionAsync(ct);
        await agent.RunAsync("My project is Apollo.", session, cancellationToken: ct);
        var sessions = new VespaAgentSessionStore(store);
        var key = new AgentSessionStoreKey("conversation");
        await sessions.SaveSessionAsync(agent, key, session, ct);

        using var newClient = container.CreateClient("knowledge");
        var newStore = new VespaAgentStateStore(newClient, new() { Namespace = "knowledge", Scope = "alice" });
        var newHistory = new VespaChatHistoryProvider(newStore);
        var newAgent = model.AsAIAgent(new ChatClientAgentOptions { Name = "persisted", ChatHistoryProvider = newHistory });
        var newSessions = new VespaAgentSessionStore(newStore);
        var resumed = await newSessions.GetSessionAsync(newAgent, key, ct);
        Assert.NotNull(resumed);
        await newAgent.RunAsync("Continue Apollo.", resumed, cancellationToken: ct);
        Assert.Equal(new[] { "My project is Apollo.", "Acknowledged.", "Continue Apollo." }, model.LastMessages.Select(m => m.Text));
        Assert.Equal(4, (await newHistory.GetMessagesAsync(resumed, ct)).Count);
        await newSessions.SaveSessionAsync(newAgent, key, resumed, ct);
        await newSessions.DeleteSessionAsync(newAgent, key, ct);
        Assert.Null(await newSessions.GetSessionAsync(newAgent, key, ct));

        var files = new VespaAgentFileStore(store);
        using (var provider = new FileMemoryProvider(files))
        using (var writerModel = new PatternChatClient("file_memory_write", new() { ["fileName"] = "preferences.md", ["content"] = "Use C# examples.\r\nKeep source IDs." }))
        {
            var fileAgent = writerModel.AsAIAgent(new ChatClientAgentOptions { Name = "files", AIContextProviders = [provider] });
            await fileAgent.RunAsync("Save my preferences.", cancellationToken: ct);
        }
        var nextFiles = new VespaAgentFileStore(newStore);
        Assert.Contains("Use C# examples.", await nextFiles.ReadAsync("preferences.md", ct));
        var matches = await nextFiles.SearchAsync("", "source IDs", cancellationToken: ct);
        Assert.Equal(2, Assert.Single(Assert.Single(matches).MatchingLines).LineNumber);
        Assert.True(await nextFiles.DeleteAsync("preferences.md", ct));
        Assert.Null(await new VespaAgentStateStore(newClient, new() { Namespace = "knowledge", Scope = "bob" }).ReadAsync("concurrency", ct));
    }
}

public sealed class AgentPersistenceFactAttribute : FactAttribute
{
    public AgentPersistenceFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("VESPA_AGENT_FRAMEWORK_INTEGRATION_TESTS") != "1")
            Skip = "Set VESPA_AGENT_FRAMEWORK_INTEGRATION_TESTS=1 to run against a dedicated Vespa container.";
    }
}
