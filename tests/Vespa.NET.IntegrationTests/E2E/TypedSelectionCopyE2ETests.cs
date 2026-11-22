using Vespa.Documents;
using Vespa.IntegrationTests.Fixtures;
using Vespa.Models;
using Vespa.Models.Attributes;
using Vespa.Models.Schema;
using Vespa.Testcontainers;
using Xunit;

namespace Vespa.IntegrationTests.E2E;

// Start the separate two-cluster application after other collections release their containers.
[CollectionDefinition("Selection copy", DisableParallelization = true)]
public sealed class SelectionCopyCollection { }

[Collection("Selection copy")]
[Trait("Category", "E2E")]
public sealed class TypedSelectionCopyE2ETests
{
    private const string Namespace = "copy_tenant";
    private const string Source = "source";
    private const string Destination = "destination";
    private const string DocumentType = "copy_item";

    private const string Services = """
        <services version="1.0">
          <container id="api" version="1.0">
            <document-api/>
          </container>
          <content id="source" version="1.0">
            <redundancy>1</redundancy>
            <documents><document type="copy_item" mode="store-only"/></documents>
            <nodes><node hostalias="node1" distribution-key="0"/></nodes>
          </content>
          <content id="destination" version="1.0">
            <redundancy>1</redundancy>
            <documents><document type="copy_item" mode="store-only"/></documents>
            <nodes><node hostalias="node1" distribution-key="1"/></nodes>
          </content>
        </services>
        """;

    [Fact]
    public async Task TypedCopy_PreservesSourceAndCopiesOnlyMatchingDocumentsAcrossClusters()
    {
        if (!VespaFixture.IntegrationEnabled) return;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(7));
        var token = deadline.Token;

        await using var container = new VespaContainer();
        await container.StartAsync(token);
        // CreateClient makes the container responsible for disposing both the client and its HTTP transport.
        var client = container.CreateClient(Namespace);
        await client.Admin.DeploySchemaAsync<CopyItem>(new() { CustomServicesXml = Services }, cancellationToken: token);
        await WaitForClustersAsync(client, token);

        var documents = client.Documents;
        var items = Enumerable.Range(0, 130).Select(i => new CopyItem
        {
            Batch = i switch { 0 or 1 => "automatic", 2 => "leave", _ => "manual" },
            Sequence = i
        }).ToArray();
        var writeToSource = new DocumentRequestOptions { Route = $"[Content:cluster={Source}]" };
        await Parallel.ForEachAsync(items, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = token },
            async (item, ct) =>
            {
                await documents.PutAsync(Id(item), item, documentType: DocumentType,
                    requestOptions: writeToSource, cancellationToken: ct);
            });

        var probeId = Id(items[0]);
        var sourceDoc = await documents.GetAsync<CopyItem>(new DocumentRequestOptions { Cluster = Source }, probeId, cancellationToken: token);
        Assert.Equal(items[0], sourceDoc?.Fields);
        Assert.Null(await documents.GetAsync<CopyItem>(new DocumentRequestOptions { Cluster = Destination }, probeId, cancellationToken: token));
        await AssertContentsAsync(documents, Destination, [], token);

        var automatic = await documents.CopyBySelectionAsync<CopyItem>(cluster: Source, destinationCluster: Destination,
            selection: p => p.Batch == "automatic", cancellationToken: token);
        Assert.True(automatic.IsSuccess);
        Assert.Equal(2L, automatic.DocumentCount);
        await AssertContentsAsync(documents, Destination, items.Where(p => p.Batch == "automatic"), token);

        // A short first chunk encourages a continuation, but Vespa may finish within its time target.
        var batch = "manual";
        var frozen = DocumentSelection.For<CopyItem>(p => p.Batch == batch);
        var page = await documents.CopyBySelectionPageAsync<CopyItem>(cluster: Source, destinationCluster: Destination,
            selection: p => p.Batch == batch, requestOptions: new() { TimeChunk = TimeSpan.FromMilliseconds(1) }, cancellationToken: token);
        long total = page.DocumentCount;
        batch = "leave";
        while (!page.IsComplete)
        {
            page = await documents.CopyBySelectionPageAsync<CopyItem>(cluster: Source, destinationCluster: Destination,
                rawSelection: frozen, continuation: page.Continuation, cancellationToken: token);
            total += page.DocumentCount;
        }

        Assert.Equal(items.LongCount(p => p.Batch == "manual"), total);
        await AssertContentsAsync(documents, Destination, items.Where(p => p.Batch != "leave"), token);
        await AssertContentsAsync(documents, Source, items, token);
    }

    private static async Task WaitForClustersAsync(VespaClient client, CancellationToken cancellationToken)
    {
        while (true)
        {
            if (await client.IsReadyAsync(cancellationToken))
            {
                try
                {
                    foreach (var cluster in new[] { Source, Destination })
                        await client.Documents.GetAsync<CopyItem>(new DocumentRequestOptions { Cluster = cluster },
                            "readiness", cancellationToken: cancellationToken);
                    return;
                }
                catch (VespaException ex) when (ex.StatusCode is 429 or 503 or 504)
                {
                    // The HTTP container is up while the content cluster is still converging.
                }
            }
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
    }

    private static async Task AssertContentsAsync(IDocumentOperations documents, string cluster,
        IEnumerable<CopyItem> expected, CancellationToken cancellationToken)
    {
        var actual = new Dictionary<string, CopyItem>();
        await foreach (var doc in documents.VisitAsync<CopyItem>(DocumentType, cluster: cluster, @namespace: Namespace,
                           cancellationToken: cancellationToken))
            actual.Add(doc.FullId, Assert.IsType<CopyItem>(doc.Fields));
        var wanted = expected.ToDictionary(item => $"id:{Namespace}:{DocumentType}::{Id(item)}");
        Assert.Equal(wanted.OrderBy(pair => pair.Key), actual.OrderBy(pair => pair.Key));
    }

    private static string Id(CopyItem item) => $"item-{item.Sequence}";

    // The client supplies its default namespace for all model-aware writes and selection requests.
    [VespaDocument(DocumentType, Mode = DocumentMode.StoreOnly)]
    private sealed record CopyItem
    {
        [VespaField(IndexingMode = IndexingMode.Summary)]
        public string Batch { get; init; } = "";
        [VespaField(IndexingMode = IndexingMode.Summary)]
        public int Sequence { get; init; }
    }
}
