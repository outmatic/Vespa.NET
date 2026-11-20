#pragma warning disable MAAI001
using System.IO.Compression;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Vespa.AgentFrameworkSample;
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
        await VerifyNativeRetrievalAsync(newClient, ct);
        await VerifyLexicalMatchingAsync(newClient, ct);
    }

    private static async Task VerifyLexicalMatchingAsync(IVespaClient client, CancellationToken ct)
    {
        foreach (var (id, title, body, identifier) in new[]
        {
            ("it-coffee", "Il caffè di Giulia Rossi", "I gatti dormono vicino alla pompa di calore.", "ZX-881"),
            ("it-other", "Il tè di Giulia Bianchi", "Il calore della pompa è descritto.", "ZX-882"),
            ("it-case", "Manuale", "Nessun termine in comune.", "zx-881")
        })
        {
            var response = await client.Documents.PutAsync(id, new Dictionary<string, object>
            {
                ["language"] = "it",
                ["identifier"] = identifier,
                ["title"] = title,
                ["body"] = body,
                ["category"] = "lexical-cases",
                ["published"] = true
            }, "article", "knowledge", cancellationToken: ct);
            Assert.False(response.HadIgnoredFields);
        }
        const string filter = "published = true and category contains \"lexical-cases\"";
        var settings = new RetrievalSettings { Mode = "lexical", Language = "it", RankProfileOverride = "unranked" };
        while (true)
        {
            var visible = await client.Search.SearchAsync<Dictionary<string, JsonElement>>(new()
            {
                Yql = "select documentid from sources article where " + filter,
                Hits = 0
            }, ct);
            if (visible.Root.Fields?.TotalCount == 3)
                break;
            await Task.Delay(250, ct);
        }
        await AssertMatchesAsync("ZX-881", settings with { TextFieldSet = "identifier", Grammar = "raw" }, "it-coffee");
        await AssertMatchesAsync("zx-881", settings with { TextFieldSet = "identifier", Grammar = "raw" }, "it-case");
        await AssertMatchesAsync("ZX-88", settings with { TextFieldSet = "identifier", Grammar = "raw" });
        await AssertMatchesAsync("Giulia Rossi", settings with { TextFieldSet = "title", Grammar = "phrase" }, "it-coffee");
        await AssertMatchesAsync("caffe", settings with { TextFieldSet = "title" }, "it-coffee");
        await AssertMatchesAsync("gatto", settings with { TextFieldSet = "body" }, "it-coffee");
        await AssertMatchesAsync("pompa di calore", settings with { TextFieldSet = "body", Grammar = "phrase" }, "it-coffee");
        await AssertMatchesAsync("pompa calore", settings with { TextFieldSet = "body", Grammar = "all" }, "it-coffee", "it-other");
        await AssertMatchesAsync("pompa", settings with { TextFieldSet = "title" });
        await AssertMatchesAsync("\"" + string.Join(" ", Enumerable.Repeat("and", 12)) + "\" pompa",
            settings with { TextFieldSet = "body", Grammar = "tokenize" }, "it-coffee", "it-other");
        var projected = await client.Search.SearchAsync<Dictionary<string, JsonElement>>(NativeRetrieval.CreateRequest(
            "caffe", null, "article", filter, settings with
            {
                TextFieldSet = "title",
                ResultFields = ["body"],
                SummaryClass = "vector_data"
            }), ct);
        var fields = Assert.Single(projected.Root.Children).Fields!;
        Assert.Contains("pompa di calore", fields["body"].GetString(), StringComparison.Ordinal);
        Assert.False(fields.ContainsKey("title"));

        async Task AssertMatchesAsync(string query, RetrievalSettings options, params string[] expected)
        {
            var response = await client.Search.SearchAsync<Dictionary<string, JsonElement>>(
                NativeRetrieval.CreateRequest(query, null, "article", filter, options, hits: 10), ct);
            Assert.Empty(response.Root.Errors ?? []);
            Assert.True(response.Root.Coverage?.Full);
            Assert.Equal(expected.Order(StringComparer.Ordinal), response.Root.Children.Select(h => h.Id).Order(StringComparer.Ordinal));
        }
    }

    private static async Task VerifyNativeRetrievalAsync(IVespaClient client, CancellationToken ct)
    {
        foreach (var (id, title, body, first, published) in new[]
        {
            ("native-exact", "ZX-881 repair manual", "Recovery instructions for ZX-881.", false, true),
            ("native-dense", "General service guide", "An overview of service procedures.", true, true),
            ("native-hidden", "ZX-881 repair manual", "Unpublished ZX-881 recovery instructions.", true, false)
        })
        {
            var vector = new float[768];
            vector[first ? 0 : 1] = 1;
            var response = await client.Documents.PutAsync(id, new Dictionary<string, object>
            {
                ["title"] = title,
                ["body"] = body,
                ["category"] = "agent-framework",
                ["published"] = published,
                ["embedding"] = new Dictionary<string, object> { ["values"] = vector }
            }, "article", "knowledge", cancellationToken: ct);
            Assert.False(response.HadIgnoredFields);
        }
        using var embeddings = new NativeRetrievalTests.CountingEmbeddings();
        var lexical = new DocumentationSearch(client, embeddings, new() { Mode = "lexical" });
        while ((await lexical.SearchAsync("ZX-881", ct)).All(h => h.Id != "native-exact"))
            await Task.Delay(250, ct);
        Assert.Equal("native-exact", Assert.Single(await lexical.SearchAsync("ZX-881", ct)).Id);
        Assert.Equal(0, embeddings.Calls);
        var hybrid = new DocumentationSearch(client, embeddings, new() { Mode = "hybrid_normalized", VectorWeight = .1 });
        var hits = await hybrid.SearchAsync("ZX-881", ct);
        Assert.Equal("native-exact", hits[0].Id);
        Assert.DoesNotContain(hits, h => h.Id == "native-hidden");
        Assert.True(hits[0].GetMatchFeature("lexical_score") > 0);

        using var model = new PatternChatClient();
        var documentation = new DocumentationSearch(client, embeddings, RetrievalSettings.ForSample());
        var provider = new TextSearchProvider(async (query, token) =>
            (await documentation.SearchAsync(query, token)).Select(hit => new TextSearchProvider.TextSearchResult
            {
                Text = hit.Fields!.Title + "\n" + hit.Fields.Body,
                SourceName = hit.Id
            }));
        var agent = model.AsAIAgent(new ChatClientAgentOptions { Name = "native-retrieval", AIContextProviders = [provider] });
        await agent.RunAsync("ZX-881", cancellationToken: ct);
        Assert.Contains(model.LastMessages, m => m.Text.Contains("Recovery instructions for ZX-881", StringComparison.Ordinal));
        Assert.DoesNotContain(model.LastMessages, m => m.Text.Contains("Unpublished", StringComparison.Ordinal));
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
