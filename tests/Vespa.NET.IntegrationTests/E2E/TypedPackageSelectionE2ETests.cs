using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Vespa.Documents;
using Vespa.IntegrationTests.Fixtures;
using Vespa.Models;
using Vespa.Models.Attributes;
using Vespa.Models.Schema;
using Vespa.Testcontainers;
using Xunit;

namespace Vespa.IntegrationTests.E2E;

[CollectionDefinition("Package selection", DisableParallelization = true)]
public sealed class PackageSelectionCollection { }

[Collection("Package selection")]
[Trait("Category", "E2E")]
public sealed class TypedPackageSelectionE2ETests
{
    private const string ItemType = "package_item";
    private const string RawType = "package_expiry";
    private const string HomeNamespace = "package_tenant";
    private const string OtherNamespace = "other_tenant";
    private static readonly Type[] Schemas = [typeof(PackageItem), typeof(ExpiryItem)];
    private static readonly DocumentRequestOptions ReadCluster = new() { Cluster = "content" };

    [Fact]
    public async Task PackageSelection_RoutesNewPutsAndCollectsPreviouslyStoredDocuments()
    {
        if (!VespaFixture.IntegrationEnabled) return;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        var token = deadline.Token;
        await using var container = new VespaContainer(VespaFixture.Image);
        await container.StartAsync(token);
        var client = container.CreateClient(HomeNamespace);
        var documents = client.Documents;
        await client.Admin.DeploySchemaAsync(Schemas, cancellationToken: token);
        await WaitUntilAsync(async () =>
        {
            if (!await client.IsReadyAsync(token)) return false;
            await ReadItem("ready");
            return true;
        }, "initial document storage", token);

        var label = "music & \"kept\"\\catalog";
        var cutoff = 42L;
        var keep = new PackageItem { Label = label, Sequence = cutoff, Note = "música réservée" };
        var below = keep with { Sequence = cutoff - 1 };
        var present = keep with { OptionalCount = 0 };
        var future = new ExpiryItem { KeepUntil = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds() };
        var past = new ExpiryItem { KeepUntil = 1 };

        await documents.PutAsync("old-keep", keep, cancellationToken: token);
        await documents.PutAsync("old-below", below, cancellationToken: token);
        await documents.PutAsync("old-present", present, cancellationToken: token);
        await documents.PutAsync("old-future", future, cancellationToken: token);
        await documents.PutAsync("old-past", past, cancellationToken: token);
        Assert.Equal(keep, await ReadItem("old-keep"));
        Assert.Equal(below, await ReadItem("old-below"));
        Assert.Equal(present, await ReadItem("old-present"));
        Assert.Equal(future, await ReadExpiry("old-future"));
        Assert.Equal(past, await ReadExpiry("old-past"));

        var raw = new Dictionary<string, string> { [RawType] = "package_expiry.keep_until > now()" };
        var options = new ApplicationPackageOptions { DocumentSelections = raw }
            .WithDocumentSelection<PackageItem>(item =>
                item.Label == label && item.Sequence >= cutoff && item.OptionalCount == null);
        raw[RawType] = "false";
        label = "changed after configuration";
        cutoff++;

        // Turn GC off for the routing phase. A missing new document can then only be attributed
        // to routing once the same document is positively read from storage on the control path.
        await DeployWithGcAsync(client, options, enabled: false, token);
        await WaitUntilAsync(async () =>
        {
            var id = $"route-probe-{Guid.NewGuid():N}";
            Assert.True((await documents.PutAsync(id, past, cancellationToken: token)).IsSuccess);
            return await ReadExpiry(id) is null;
        }, "default route to use the new selection", token);
        Assert.Equal(below, await ReadItem("old-below"));
        Assert.Equal(past, await ReadExpiry("old-past"));

        Assert.True((await documents.PutAsync("new-keep", keep, ItemType, OtherNamespace, cancellationToken: token)).IsSuccess);
        Assert.Equal(keep, await ReadItem("new-keep", OtherNamespace));
        Assert.True((await documents.PutAsync("new-reject", present, ItemType, OtherNamespace, cancellationToken: token)).IsSuccess);
        Assert.Null(await ReadItem("new-reject", OtherNamespace));
        Assert.True((await documents.PutAsync("new-future", future, cancellationToken: token)).IsSuccess);
        Assert.Equal(future, await ReadExpiry("new-future"));

        Assert.True((await documents.PutAsync("editable", keep, cancellationToken: token)).IsSuccess);
        Assert.Equal(keep, (await documents.GetAsync<PackageItem>("editable", cancellationToken: token))?.Fields);
        Assert.True((await documents.UpdateFieldsAsync<PackageItem>("editable",
            update => update.Field(p => p.Note, FieldOp.Assign("updated")), cancellationToken: token)).IsSuccess);
        Assert.Equal(keep with { Note = "updated" }, await ReadItem("editable"));
        Assert.True((await documents.DeleteAsync<PackageItem>("editable", cancellationToken: token)).IsSuccess);
        Assert.Null(await ReadItem("editable"));

        // Both rejected originals were visible with GC disabled. Read straight from the content
        // cluster so the routing selector cannot hide them while we wait for actual removal.
        await DeployWithGcAsync(client, options, enabled: true, token);
        await WaitUntilAsync(async () =>
            await ReadItem("old-below") is null && await ReadItem("old-present") is null && await ReadExpiry("old-past") is null,
            "GC to remove pre-existing nonmatching documents", token);
        Assert.Equal(keep, await ReadItem("old-keep"));
        Assert.Equal(keep, await ReadItem("new-keep", OtherNamespace));
        Assert.Equal(future, await ReadExpiry("old-future"));

        async Task<PackageItem?> ReadItem(string id, string ns = HomeNamespace) =>
            (await documents.GetAsync<PackageItem>(id, ItemType, ns, requestOptions: ReadCluster, cancellationToken: token))?.Fields;

        async Task<ExpiryItem?> ReadExpiry(string id) =>
            (await documents.GetAsync<ExpiryItem>(id, requestOptions: ReadCluster, cancellationToken: token))?.Fields;
    }

    private static async Task DeployWithGcAsync(VespaClient client, ApplicationPackageOptions options, bool enabled, CancellationToken token)
    {
        using var package = new MemoryStream();
        VespaSchemaBuilder.GenerateApplicationPackage(package, Schemas, options);
        package.Position = 0;
        using (var zip = new ZipArchive(package, ZipArchiveMode.Update, leaveOpen: true))
        {
            using var entry = zip.GetEntry("services.xml")!.Open();
            var services = XDocument.Load(entry);
            var documents = Assert.Single(services.Descendants("documents"));
            Assert.Equal("true", (string?)documents.Attribute("garbage-collection"));
            if (enabled)
                documents.SetAttributeValue("garbage-collection-interval", "1");
            else
                documents.SetAttributeValue("garbage-collection", "false");
            entry.SetLength(0);
            entry.Position = 0;
            using (var writer = new StreamWriter(entry, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true))
                services.Save(writer);
            entry.Position = 0;
            Assert.Equal((int)'<', entry.ReadByte());
        }
        package.Position = 0;
        await client.Admin.DeployAsync(package, cancellationToken: token);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> ready, string operation, CancellationToken token)
    {
        var timer = Stopwatch.StartNew();
        Exception? lastTransientFailure = null;
        while (timer.Elapsed < TimeSpan.FromMinutes(2))
        {
            try
            {
                if (await ready()) return;
            }
            catch (Exception ex) when (!token.IsCancellationRequested &&
                ex is OperationCanceledException or HttpRequestException or VespaException { StatusCode: 429 or 503 or 504 })
            {
                // Serving and content nodes can adopt the new config at different times.
                lastTransientFailure = ex;
            }
            await Task.Delay(TimeSpan.FromSeconds(1), token);
        }
        token.ThrowIfCancellationRequested();
        throw new TimeoutException($"Timed out waiting for {operation}.", lastTransientFailure);
    }

    [VespaDocument(ItemType, Mode = DocumentMode.StoreOnly)]
    private sealed record PackageItem
    {
        [VespaField(Name = "label_text", IndexingMode = IndexingMode.AttributeSummary)]
        public string Label { get; init; } = "";
        [VespaField(IndexingMode = IndexingMode.AttributeSummary)]
        public long Sequence { get; init; }
        [VespaField(IndexingMode = IndexingMode.AttributeSummary)]
        public int? OptionalCount { get; init; }
        [VespaField(IndexingMode = IndexingMode.Summary)]
        public string Note { get; init; } = "";
    }

    [VespaDocument(RawType, Mode = DocumentMode.StoreOnly)]
    private sealed record ExpiryItem
    {
        [VespaField(IndexingMode = IndexingMode.AttributeSummary)]
        public long KeepUntil { get; init; }
    }
}
