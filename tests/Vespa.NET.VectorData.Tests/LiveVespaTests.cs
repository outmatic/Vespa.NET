using System.IO.Compression;
using System.Diagnostics;
using System.Xml.Linq;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;
using Vespa.AgentFrameworkSample;
using Vespa.Testcontainers;
using Xunit;
using Xunit.Abstractions;

namespace Vespa.VectorData.Tests;

public sealed class LiveVespaTests(ITestOutputHelper output)
{
    [VespaIntegrationFact]
    public async Task ExistingSchema_CRUD_Filtering_Scores_AndAgentTool_WorkAgainstRealVespa()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        var cancellationToken = timeout.Token;
        var image = Environment.GetEnvironmentVariable("VESPA_VECTOR_DATA_IMAGE") ?? VespaContainer.DefaultImage;
        await using var container = new VespaContainer(image);
        await container.StartAsync(cancellationToken);
        output.WriteLine($"Dedicated Vespa: {container.Endpoint}, image: {image}");
        using var client = container.CreateClient("test");
        await DeploySampleAsync(client, 3, cancellationToken);
        while (!await client.IsReadyAsync(cancellationToken))
            await Task.Delay(500, cancellationToken);

        using var embeddings = new FixedEmbeddingGenerator();
        using var store = new VespaVectorStore(client, TestHarness.Options with { EmbeddingGenerator = embeddings });
        using var articles = store.GetCollection<string, Article>("article");
        await articles.UpsertAsync([
            new Article { Id = "a:1", Title = "Nearest neighbor search", Category = "Docs", Priority = 3, Published = true, Embedding = new float[] { 1, 0, 0 } },
            new Article { Id = "b", Title = "Deployment", Category = "docs", Priority = 1, Published = false, Embedding = new float[] { 0, 1, 0 } },
            new Article { Id = "c", Title = "Opposite", Category = "docs", Priority = 2, Published = true, Embedding = new float[] { -1, 0, 0 } },
            new Article { Id = "quoted", Title = "Quoted metadata", Category = "a\"b\\c", Priority = 2, Published = true, Embedding = new float[] { 1, 1, 0 } }
        ], cancellationToken);

        var document = await articles.GetAsync("a:1", new() { IncludeVectors = true }, cancellationToken);
        Assert.NotNull(document);
        Assert.Equal(new float[] { 1, 0, 0 }, document.Embedding.ToArray());
        List<VectorSearchResult<Article>> hits;
        do
        {
            hits = await articles.SearchAsync(new float[] { 1, 0, 0 }, 4, cancellationToken: cancellationToken).Collect();
            if (hits.Count < 4)
                await Task.Delay(250, cancellationToken);
        } while (hits.Count < 4);
        Assert.Equal(new[] { "a:1", "quoted", "b", "c" }, hits.Select(h => h.Record.Id));
        Assert.Equal(1, hits[0].Score!.Value, 6);
        Assert.Equal(0, hits[2].Score!.Value, 6);
        Assert.Equal(-1, hits[3].Score!.Value, 6);

        var filtered = await articles.SearchAsync(new float[] { 1, 0, 0 }, 4,
            new() { Filter = a => a.Category == "Docs" && a.Priority >= 2 && a.Published, IncludeVectors = true }, cancellationToken).Collect();
        Assert.Equal("a:1", Assert.Single(filtered).Record.Id);
        Assert.Equal(3, filtered[0].Record.Embedding.Length);
        var quoted = "a\"b\\c";
        Assert.Equal("quoted", Assert.Single(await articles.SearchAsync(new float[] { 1, 0, 0 }, 4,
            new() { Filter = a => a.Category == quoted }, cancellationToken).Collect()).Record.Id);
        Assert.Equal("a:1", Assert.Single(await articles.GetAsync(a => a.Priority > 2, 4, cancellationToken: cancellationToken).Collect()).Id);
        Assert.Equal("b", Assert.Single(await articles.SearchAsync(new float[] { 1, 0, 0 }, 4,
            new() { Filter = a => a.Priority != 2 && a.Published != true }, cancellationToken).Collect()).Record.Id);
        var page = await articles.SearchAsync(new float[] { 1, 0, 0 }, 2, new() { Skip = 1 }, cancellationToken).Collect();
        Assert.Equal(new[] { "quoted", "b" }, page.Select(h => h.Record.Id));
        Assert.Single(await articles.SearchAsync(new float[] { 1, 0, 0 }, 4, new() { ScoreThreshold = 0.9 }, cancellationToken).Collect());

        var tool = AIFunctionFactory.Create(async (string query, CancellationToken token) =>
        {
            var results = await articles.SearchAsync(query, 1, cancellationToken: token).Collect();
            return string.Join("\n", results.Select(h => $"[{h.Record.Id}] {h.Record.Title}"));
        }, "search_vespa", "Search documentation.");
        using var model = new ToolCallingChatClient();
        AIAgent agent = model.AsAIAgent(instructions: "Answer using the search tool.", tools: [tool]);
        var response = await agent.RunAsync("How does vector search work?", cancellationToken: cancellationToken);
        Assert.Contains("[a:1] Nearest neighbor search", response.Text);
        Assert.Equal(2, model.Calls);

        const string preference = "The user prefers code examples in C#.";
        using (var memories = PreferenceMemoryTests.GetCollection(store))
        using (var rememberModel = new MemoryToolCallingChatClient("remember_preference", "preference", preference))
        {
            var memory = new PreferenceMemory(memories, embeddings, "memory-test-user");
            var remembered = await memory.CreateAgent(rememberModel).RunAsync(
                "Remember that I prefer code examples in C#.", cancellationToken: cancellationToken);
            Assert.Contains("Preference saved.", remembered.Text);
        }

        using (var nextStore = new VespaVectorStore(client, TestHarness.Options with { EmbeddingGenerator = embeddings }))
        using (var memories = PreferenceMemoryTests.GetCollection(nextStore))
        using (var recallModel = new MemoryToolCallingChatClient("recall_preferences", "query", "code-example language"))
        {
            var memory = new PreferenceMemory(memories, embeddings, "memory-test-user");
            var recalled = await memory.CreateAgent(recallModel).RunAsync(
                "Which language should you use for my examples?", cancellationToken: cancellationToken);
            Assert.DoesNotContain("C#", recallModel.InitialMessages);
            Assert.Contains(preference, recalled.Text);
            var otherUser = new PreferenceMemory(memories, embeddings, "another-test-user");
            Assert.Equal("No saved preference found.", await otherUser.RecallAsync("code examples", cancellationToken));
            await memory.RememberAsync("The user now prefers F# examples.", cancellationToken);
            Assert.Equal("The user now prefers F# examples.", await memory.RecallAsync("code examples", cancellationToken));
        }

        await articles.DeleteAsync("b", cancellationToken);
        Assert.Null(await articles.GetAsync("b", cancellationToken: cancellationToken));
        output.WriteLine("Verified CRUD, filters, cosine scores, pagination, vector projection, retrieval tools and preference memory across fresh agents.");
    }

    [VespaIntegrationFact]
    public async Task ReadmeQuickstart_RunsUnmodifiedAgainstTheSampleApplication()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        var cancellationToken = timeout.Token;
        var image = Environment.GetEnvironmentVariable("VESPA_VECTOR_DATA_IMAGE") ?? VespaContainer.DefaultImage;
        await using var container = new VespaContainer(image);
        await container.StartAsync(cancellationToken);
        using var client = container.CreateClient("knowledge");
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Vespa.NET.slnx")))
            root = root.Parent;
        Assert.NotNull(root);
        var project = Path.Combine(root.FullName, "src", "Vespa.NET.VectorData", "Vespa.NET.VectorData.csproj");
        var readme = await File.ReadAllTextAsync(Path.Combine(Path.GetDirectoryName(project)!, "README.md"), cancellationToken);
        var start = readme.IndexOf("```csharp\n", StringComparison.Ordinal);
        Assert.True(start >= 0, "README must contain its complete C# quickstart.");
        start += "```csharp\n".Length;
        var end = readme.IndexOf("```", start, StringComparison.Ordinal);
        Assert.True(end > start);
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"vespa-readme-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            var published = Path.Combine(temporaryDirectory, "sample");
            await RunDotnetAsync(root.FullName,
                ["publish", "samples/Vespa.NET.AgentFramework", "-c", "Release", "-o", published, "--disable-build-servers", "--verbosity", "quiet"],
                null, cancellationToken);
            // Keep the shared Docker disk allowance in the disposable test copy only.
            var servicesPath = Path.Combine(published, "app", "services.xml");
            var services = await File.ReadAllTextAsync(servicesPath, cancellationToken);
            await File.WriteAllTextAsync(servicesPath,
                services.Replace("</content>", "<tuning><resource-limits><disk>0.95</disk></resource-limits></tuning></content>", StringComparison.Ordinal),
                cancellationToken);
            var deployment = await RunDotnetAsync(temporaryDirectory,
                [Path.Combine(published, "Vespa.NET.AgentFramework.dll"), "--deploy-only"], container.Endpoint, cancellationToken,
                container.ConfigEndpoint);
            Assert.Contains("Sample application is ready.", deployment);

            await File.WriteAllTextAsync(Path.Combine(temporaryDirectory, "Program.cs"), readme[start..end], cancellationToken);
            var consumer = new XElement("Project", new XAttribute("Sdk", "Microsoft.NET.Sdk"),
                new XElement("PropertyGroup", new XElement("OutputType", "Exe"), new XElement("TargetFramework", "net10.0"),
                    new XElement("ImplicitUsings", "enable"), new XElement("Nullable", "enable")),
                new XElement("ItemGroup", new XElement("ProjectReference", new XAttribute("Include", project))));
            await File.WriteAllTextAsync(Path.Combine(temporaryDirectory, "Quickstart.csproj"), consumer.ToString(), cancellationToken);
            await RunDotnetAsync(temporaryDirectory,
                ["build", "Quickstart.csproj", "-c", "Release", "--disable-build-servers", "--verbosity", "quiet"], null, cancellationToken);
            var console = await RunDotnetAsync(temporaryDirectory,
                [Path.Combine(temporaryDirectory, "bin", "Release", "net10.0", "Quickstart.dll")], container.Endpoint, cancellationToken);
            Assert.Contains("dimensions: 768", console);
            Assert.Contains("quickstart-search: Vector search in Vespa", console);
            Assert.Contains("quickstart-deploy: Deploying a Vespa application", console);
            Assert.Null(await client.Documents.GetAsync<Dictionary<string, object>>("quickstart-search", "article", "knowledge", cancellationToken: cancellationToken));
            Assert.Null(await client.Documents.GetAsync<Dictionary<string, object>>("quickstart-deploy", "article", "knowledge", cancellationToken: cancellationToken));
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    private async Task<string> RunDotnetAsync(string directory, IReadOnlyList<string> arguments,
        string? endpoint, CancellationToken cancellationToken, string? configEndpoint = null)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        startInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        if (endpoint is not null)
            startInfo.Environment["VESPA_ENDPOINT"] = endpoint;
        if (configEndpoint is not null)
            startInfo.Environment["VESPA_CONFIG_ENDPOINT"] = configEndpoint;
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start dotnet.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
            output.WriteLine($"dotnet {arguments[0]} exited; collecting output.");
            var console = await stdout.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            var errors = await stderr.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            output.WriteLine(console);
            output.WriteLine(errors);
            Assert.True(process.ExitCode == 0, $"dotnet {arguments[0]} failed with exit code {process.ExitCode}.");
            return console;
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
    }

    private static async Task DeploySampleAsync(VespaClient client, int dimensions, CancellationToken cancellationToken)
    {
        using var package = new MemoryStream();
        using (var archive = new ZipArchive(package, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var path in Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "app"), "*", SearchOption.AllDirectories))
            {
                var entry = archive.CreateEntry(Path.GetRelativePath(Path.Combine(AppContext.BaseDirectory, "app"), path).Replace('\\', '/'));
                await using var stream = entry.Open();
                await using var writer = new StreamWriter(stream);
                var contents = (await File.ReadAllTextAsync(path, cancellationToken)).Replace("[768]", $"[{dimensions}]", StringComparison.Ordinal);
                // Relax the shared Docker disk threshold only for this disposable test application.
                if (Path.GetFileName(path) == "services.xml")
                    contents = contents.Replace("</content>", "<tuning><resource-limits><disk>0.95</disk></resource-limits></tuning></content>", StringComparison.Ordinal);
                await writer.WriteAsync(contents);
            }
        package.Position = 0;
        await client.Admin.DeployAsync(package, cancellationToken: cancellationToken);
    }
}

public sealed class VespaIntegrationFactAttribute : FactAttribute
{
    public VespaIntegrationFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("VESPA_VECTOR_DATA_INTEGRATION_TESTS") != "1")
            Skip = "Set VESPA_VECTOR_DATA_INTEGRATION_TESTS=1 to run against a dedicated Docker container.";
    }
}
