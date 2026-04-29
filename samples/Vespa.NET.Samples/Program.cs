using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Vespa;
using Vespa.Documents;
using Vespa.Feed;
using Vespa.Models;
using Vespa.Models.Attributes;
using Vespa.Models.Tensors;
using Vespa.Query;
using Vespa.Search;

namespace Vespa.Samples;

internal static class Program
{
    private static readonly string[] Categories = ["groceries", "fuel", "restaurant", "pharmacy", "electronics"];

    private static async Task Main(string[] args)
    {
        OutputFormatter.PrintHeader();

        using var loggerFactory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Warning));
        var logger = loggerFactory.CreateLogger<VespaClient>();

        var options = new VespaClientOptions
        {
            Endpoint = "http://localhost:8080",
            ConfigServerEndpoint = "http://localhost:19071",
            DefaultNamespace = "default",
            Timeout = TimeSpan.FromSeconds(30),
            EnableRetry = true,
            MaxRetryAttempts = 2
        };

        using var httpClient = new HttpClient
        {
            BaseAddress = new Uri(options.Endpoint)
        };
        var client = new VespaClient(httpClient, options, logger);

        if (!await DeployAndWaitAsync(client, options))
            return;

        var (docType, _) = VespaDocumentMeta.For<Transaction>();
        var transactions = GenerateTransactions(1000);

        await BulkIndexAsync(client, docType, transactions);
        await CrudDemoAsync(client);
        await YqlQueryDemoAsync(client);
        await NearestNeighborDemoAsync(client);
        await StreamingSearchDemoAsync(client, docType);
        await GroupingDemoAsync(client);
        await FieldUpdateDemoAsync(client);

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[bold green]All scenes complete[/]").Centered());
        AnsiConsole.WriteLine();
    }

    // ── Scenes ────────────────────────────────────────────────────────────────────

    private static async Task<bool> DeployAndWaitAsync(VespaClient client, VespaClientOptions options)
    {
        OutputFormatter.PrintSectionHeader("Deploy + Health Check + Metrics", "🔍");

        try
        {
            AnsiConsole.MarkupLine("[dim]Deploying schema for Transaction...[/]");
            await client.Admin.DeploySchemaAsync<Transaction>();
            AnsiConsole.MarkupLine("[green]Schema deployed successfully[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Schema deploy failed:[/] {ex.Message}");
            AnsiConsole.MarkupLine("[dim]Is the config server running on port 19071?[/]");
            AnsiConsole.MarkupLine("[blue]docker run --detach --name vespa --publish 8080:8080 --publish 19071:19071 vespaengine/vespa[/]");
            return false;
        }

        var sw = Stopwatch.StartNew();
        var isHealthy = false;
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("[dim]Waiting for Vespa to activate schema...[/]", async _ =>
            {
                var deadline = DateTime.UtcNow.AddSeconds(120);
                while (DateTime.UtcNow < deadline)
                {
                    if (await client.IsReadyAsync()) { isHealthy = true; break; }
                    await Task.Delay(2000);
                }
            });
        sw.Stop();
        OutputFormatter.PrintHealthStatus(isHealthy, options.Endpoint, sw.Elapsed.TotalMilliseconds);

        if (!isHealthy)
        {
            AnsiConsole.MarkupLine("[red bold]Vespa did not become ready within 120s.[/]");
            return false;
        }

        var metrics = await client.GetMetricsAsync();
        AnsiConsole.MarkupLine(metrics is not null
            ? $"[green]Metrics endpoint OK[/] — [dim]status={metrics.Status.Code}[/]"
            : "[yellow]Metrics not available[/]");

        return true;
    }

    private static async Task BulkIndexAsync(VespaClient client, string docType, List<Transaction> transactions)
    {
        OutputFormatter.PrintSectionHeader("Bulk Index (model-aware)", "📥");

        var (_, ns) = VespaDocumentMeta.For<Transaction>();
        AnsiConsole.MarkupLine($"[dim]Inferred docType=[/][cyan]{docType}[/] [dim]ns=[/][cyan]{ns}[/]");

        var feedDocs = transactions.Select(t => new FeedDocument<Transaction> { Id = t.Id, Fields = t });
        OutputFormatter.PrintBulkOperationStart(transactions.Count);

        var result = await client.Feed.BulkPutAsync(feedDocs, docType, ns, maxConcurrency: 10);
        OutputFormatter.PrintBulkOperationResult(result);
    }

    private static async Task CrudDemoAsync(VespaClient client)
    {
        OutputFormatter.PrintSectionHeader("Single CRUD — model-aware", "📝");

        var doc = new Transaction
        {
            Id = "demo-single-1",
            MerchantId = "merchant-42",
            Amount = 123.45m,
            Category = "electronics",
            Embedding = CreateEmbedding(123.45m, "electronics")
        };

        var putResp = await client.Documents.PutAsync(doc.Id, doc);
        AnsiConsole.MarkupLine($"[green]PUT[/] {doc.Id} → HTTP {putResp.StatusCode}");

        var fetched = await client.Documents.GetAsync<Transaction>(doc.Id);
        AnsiConsole.MarkupLine(fetched is not null
            ? $"[green]GET[/] amount=[cyan]{fetched.Fields?.Amount}[/] category=[cyan]{fetched.Fields?.Category}[/]"
            : "[yellow]GET — not found[/]");

        var delResp = await client.Documents.DeleteAsync<Transaction>(doc.Id);
        AnsiConsole.MarkupLine($"[green]DELETE[/] → HTTP {delResp.StatusCode}");
    }

    private static async Task YqlQueryDemoAsync(VespaClient client)
    {
        OutputFormatter.PrintSectionHeader("YQL Builder", "🔎");

        // Basic query
        var yql = YqlBuilder
            .Select()
            .From<Transaction>()
            .Where(w => w.Field(t => t.Amount).GreaterThan(100))
            .Limit(5)
            .Build();

        AnsiConsole.MarkupLine($"[dim]YQL:[/] [yellow]{Markup.Escape(yql)}[/]");

        var result = await client.Search.QueryAsync<Transaction>(yql, hits: 5);
        AnsiConsole.MarkupLine($"[green]Query returned[/] {result.Root.Children.Count} hits " +
                               $"[dim](totalCount={result.Root.Fields?.TotalCount})[/]");

        // M7: NearestNeighbor with annotations (label, approximate, distanceThreshold)
        var nnYql = YqlBuilder
            .Select()
            .From<Transaction>()
            .Where(w => w.NearestNeighbor("embedding", "q",
                targetHits: 5, label: "txEmb", approximate: true, distanceThreshold: 100.0))
            .Build();
        AnsiConsole.MarkupLine($"[dim]NN+annotations:[/] [yellow]{Markup.Escape(nnYql)}[/]");

        // M7: rank() — match by amount, rank by category
        var rankYql = YqlBuilder
            .Select()
            .From<Transaction>()
            .Where(w => w.Rank(
                match => match.Field("amount").GreaterThan(50),
                rank1 => rank1.Field("category").Contains("groceries")))
            .Limit(5)
            .Build();
        AnsiConsole.MarkupLine($"[dim]rank():[/] [yellow]{Markup.Escape(rankYql)}[/]");

        // M7: userInput with grammar annotation
        var uiYql = YqlBuilder
            .Select()
            .From<Transaction>()
            .Where(w => w.UserInput("userText", grammar: "weakAnd", defaultIndex: "category"))
            .Limit(5)
            .Build();
        AnsiConsole.MarkupLine($"[dim]userInput+grammar:[/] [yellow]{Markup.Escape(uiYql)}[/]");
    }

    private static async Task NearestNeighborDemoAsync(VespaClient client)
    {
        OutputFormatter.PrintSectionHeader("Nearest Neighbor (lambda field)", "🎯");

        var queryEmbedding = CreateEmbedding(95m, "groceries");
        var result = await client.Search.NearestNeighborSearchAsync<Transaction>(
            queryEmbedding, t => t.Embedding, topK: 5);

        AnsiConsole.MarkupLine($"[green]Nearest neighbor[/] returned {result.Root.Children.Count} hits");
        if (result.Root.Children.Count > 0)
            OutputFormatter.PrintNeighborsTable(result.Root.Children);
    }

    private static async Task StreamingSearchDemoAsync(VespaClient client, string docType)
    {
        OutputFormatter.PrintSectionHeader("Streaming Search", "🌊");

        var request = new VespaSearchRequest { Yql = $"select * from {docType} where true" };
        var totalHits = 0;

        await foreach (var _ in client.Search.SearchStreamAsync<Transaction>(request, pageSize: 50))
            totalHits++;

        AnsiConsole.MarkupLine($"[green]Stream complete[/] — {totalHits} hits");
    }

    private static async Task GroupingDemoAsync(VespaClient client)
    {
        OutputFormatter.PrintSectionHeader("Grouping — count by category", "📊");

        var request = new VespaSearchRequest
        {
            Yql = YqlBuilder
                .Select()
                .From<Transaction>()
                .GroupBy(GroupingBuilder.All()
                    .Group("category")
                    .Max(10)
                    .Each(e => e.Output(GroupingAgg.Count())))
                .Build(),
            Hits = 0
        };

        var result = await client.Search.GroupByAsync<Transaction>(request);
        OutputFormatter.PrintGroupingResults(result.GroupingResults);
    }

    private static async Task FieldUpdateDemoAsync(VespaClient client)
    {
        OutputFormatter.PrintSectionHeader("Field Update — FieldOp.Increment", "✏️");

        var doc = new Transaction
        {
            Id = "demo-update-1",
            MerchantId = "merchant-99",
            Amount = 10m,
            Category = "fuel",
            Embedding = CreateEmbedding(10m, "fuel")
        };
        await client.Documents.PutAsync(doc.Id, doc);

        var updateResp = await client.Documents.UpdateFieldsAsync<Transaction>(
            doc.Id,
            new Dictionary<string, FieldOperation> { ["amount"] = FieldOp.Increment(5) });
        AnsiConsole.MarkupLine($"[green]UpdateFields[/] (amount +5) → HTTP {updateResp.StatusCode}");

        var after = await client.Documents.GetAsync<Transaction>(doc.Id);
        AnsiConsole.MarkupLine($"[dim]amount after update:[/] [cyan]{after?.Fields?.Amount}[/]");

        await client.Documents.DeleteAsync<Transaction>(doc.Id);
    }

    // ── Data generation ───────────────────────────────────────────────────────────

    private static List<Transaction> GenerateTransactions(int count)
    {
        var rng = new Random(42);
        var result = new List<Transaction>(count);

        for (var i = 0; i < count; i++)
        {
            var amount = 20m + (decimal)(rng.NextDouble() * 480);
            var category = Categories[rng.Next(Categories.Length)];
            result.Add(new Transaction
            {
                Id = $"tx-{i:D5}",
                MerchantId = $"m-{rng.Next(1, 20)}",
                Amount = Math.Round(amount, 2),
                Category = category,
                Embedding = CreateEmbedding(amount, category)
            });
        }

        return result;
    }

    private static VespaTensor CreateEmbedding(decimal amount, string category)
    {
        // 8 dimensions: [0-4] one-hot category, [5] normalized amount, [6] log amount, [7] amount bucket
        var v = new float[8];

        var catIndex = Array.IndexOf(Categories, category);
        if (catIndex >= 0)
            v[catIndex] = 1.0f;

        v[5] = Math.Clamp((float)amount / 500f, 0f, 1f);
        v[6] = (float)(Math.Log10((double)amount + 1) / 3.0);
        v[7] = (float)amount switch
        {
            < 50f => 0.2f,
            < 150f => 0.5f,
            _ => 0.8f
        };

        return VespaTensor.FromDenseValues(v);
    }
}
