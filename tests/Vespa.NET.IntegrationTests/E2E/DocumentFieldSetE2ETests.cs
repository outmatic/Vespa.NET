using Vespa.Documents;
using Vespa.IntegrationTests.Fixtures;
using Vespa.Models;
using Vespa.Models.Attributes;
using Xunit;

namespace Vespa.IntegrationTests.E2E;

[Collection("Vespa")]
[Trait("Category", "E2E")]
public sealed class DocumentFieldSetE2ETests(VespaFixture fixture)
{
    [Fact]
    public async Task Get_ReadsMappedFieldsAndNativeBuiltIns()
    {
        if (!VespaFixture.IntegrationEnabled) return;
        var documents = fixture.Client.Documents;
        var id = $"fieldset-{Guid.NewGuid():N}";
        var fullId = $"id:test:product::{id}";
        var product = new TestProduct { Name = "Catalog entry", Price = 19.75, Category = "catalog" };
        try
        {
            Assert.Null(await documents.GetAsync<TestProduct>(id, fieldSet: "[id]"));
            await documents.PutAsync(id, product);

            var fields = DocumentFieldSet.For<TestProduct>(p => p.Name, p => p.Price);
            var selected = await documents.GetAsync<TestProduct>(id, fieldSet: fields);
            Assert.Equal(fullId, selected?.FullId);
            Assert.Equal(product.Name, selected!.Fields!.Name);
            Assert.Equal(product.Price, selected.Fields.Price);

            Assert.Equal(fullId, (await documents.GetAsync<TestProduct>(id, fieldSet: "[id]"))?.FullId);
            foreach (var builtIn in new[] { "[document]", "[all]" })
            {
                var read = await documents.GetAsync<TestProduct>(id, fieldSet: builtIn);
                Assert.Equal(fullId, read?.FullId);
                Assert.Equal(product.Category, read!.Fields!.Category);
            }
        }
        finally
        {
            await documents.DeleteAsync<TestProduct>(id);
        }
    }

    [Fact]
    public async Task GroupAndNumberGets_ReadMappedEventFieldsAtTheirOwnAddresses()
    {
        if (!VespaFixture.IntegrationEnabled) return;
        var documents = fixture.Client.Documents;
        var group = $"fieldset-{Guid.NewGuid():N}";
        const long number = 71;
        var localId = $"{group}:entry";
        try
        {
            await documents.PutByGroupAsync(group, localId, new TestEvent { EventName = "Group event", Payload = "G" });
            await documents.PutByNumberAsync(number, localId, new TestEvent { EventName = "Number event", Payload = "N" });

            var grouped = await documents.GetByGroupAsync<TestEvent>(group, localId,
                fieldSet: DocumentFieldSet.For<TestEvent>(e => e.EventName));
            Assert.Equal($"id:test:event:g={group}:{localId}", grouped?.FullId);
            Assert.Equal("Group event", grouped!.Fields!.EventName);

            var numbered = await documents.GetByNumberAsync<TestEvent>(number, localId,
                fieldSet: DocumentFieldSet.ForDocumentType<EventProjection>("event", e => e.Label));
            Assert.Equal($"id:test:event:n={number}:{localId}", numbered?.FullId);
            Assert.Equal("Number event", numbered!.Fields!.EventName);
        }
        finally
        {
            await documents.DeleteByGroupAsync<TestEvent>(group, localId);
            await documents.DeleteByNumberAsync<TestEvent>(number, localId);
        }
    }

    [Fact]
    public async Task PageAndJsonlVisits_ReadNameAndPriceWhileFilteringOnCategory()
    {
        if (!VespaFixture.IntegrationEnabled) return;
        var documents = fixture.Client.Documents;
        var tag = $"fieldset-visit-{Guid.NewGuid():N}";
        (string Id, string Name, double Price, string Category)[] rows =
        [
            (tag + "-a", "First", 3.25, tag),
            (tag + "-b", "Second", 8.50, tag),
            (tag + "-c", "Other", 12.75, tag + "-other")
        ];
        try
        {
            foreach (var row in rows)
                await documents.PutAsync(row.Id, new TestProduct { Name = row.Name, Price = row.Price, Category = row.Category });

            var options = new VisitOptions
            {
                WantedDocumentCount = 1,
                FieldSet = DocumentFieldSet.For<TestProduct>(p => p.Name, p => p.Price)
            };
            var expected = rows.Take(2).Select(row => (row.Id, row.Name, row.Price)).ToArray();
            List<(string Id, string Name, double Price)> paged = [];
            string? continuation = null;
            do
            {
                var page = await documents.VisitPageAsync<TestProduct>(selection: p => p.Category == tag,
                    cluster: "content", continuation: continuation, options: options);
                paged.AddRange(page.Documents.Select(doc => (doc.Id, doc.Fields!.Name, doc.Fields.Price)));
                continuation = page.Continuation;
            }
            while (continuation is not null);
            Assert.Equal(expected, paged.OrderBy(row => row.Id, StringComparer.Ordinal));

            List<(string Id, string Name, double Price)> streamed = [];
            await foreach (var doc in documents.VisitJsonlAsync<TestProduct>(selection: p => p.Category == tag,
                cluster: "content", options: options))
                streamed.Add((doc.Id, doc.Fields!.Name, doc.Fields.Price));
            Assert.Equal(expected, streamed.OrderBy(row => row.Id, StringComparer.Ordinal));
        }
        finally
        {
            foreach (var row in rows)
                await documents.DeleteAsync<TestProduct>(row.Id);
        }
    }

    private sealed class EventProjection
    {
        [VespaField(Name = "event_name")]
        public string Label { get; init; } = "";
    }
}
