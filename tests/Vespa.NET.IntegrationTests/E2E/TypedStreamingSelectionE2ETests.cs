using Vespa.Documents;
using Vespa.IntegrationTests.Fixtures;
using Vespa.Models;
using Vespa.Query;
using Xunit;

namespace Vespa.IntegrationTests.E2E;

[Collection("Vespa")]
[Trait("Category", "E2E")]
public sealed class TypedStreamingSelectionE2ETests(VespaFixture fixture)
{
    [Fact]
    public async Task StreamingSelection_AppliesFieldAndGroupFiltersToTheSameYqlMatches()
    {
        if (!VespaFixture.IntegrationEnabled) return;
        var tag = Guid.NewGuid().ToString("N");
        var marker = "marker" + tag;
        var groupA = "a-" + tag;
        var groupB = "b-é\"\\inside-" + tag;
        var groupC = "c-" + tag + "\\";
        var trailingPayload = "keep\\";
        (string Group, string LocalId, string Name, string Payload)[] rows =
        [
            (groupA, "a-keep", marker, trailingPayload),
            (groupA, "a-drop", marker, "drop"),
            (groupB, "b-keep", marker, "keep"),
            (groupB, "b-other", "other" + tag, "keep"),
            (groupC, "c-keep", marker, "keep")
        ];
        var documents = fixture.Client.Documents;
        try
        {
            foreach (var row in rows)
            {
                var put = await documents.PutByGroupAsync(row.Group, row.LocalId,
                    new TestEvent { EventName = row.Name, Payload = row.Payload }, "event", @namespace: "test");
                Assert.True(put.IsSuccess);
            }

            var query = YqlBuilder.Select().From("event")
                .Where(w => w.Field("event_name").Contains(marker))
                .ToSearchRequest()
                .WithTimeout(TimeSpan.FromSeconds(10));
            var baseline = query with { StreamingSelection = "true" };
            await AssertHits(baseline, "a-keep", "a-drop", "b-keep", "c-keep");

            var left = "é";
            var right = "xC3xA9";
            var byConstants = query.WithStreamingSelection<TestEvent>(e => left != right);
            await AssertHits(byConstants, "a-keep", "a-drop", "b-keep", "c-keep");

            var wanted = "keep";
            var byField = query.WithStreamingSelection<TestEvent>(
                e => e.Payload == wanted || e.Payload == trailingPayload);
            wanted = "drop";
            await AssertHits(byField, "a-keep", "b-keep", "c-keep");

            var trailingFirst = query.WithStreamingSelection<TestEvent>(
                e => e.Payload == trailingPayload || e.Payload == "drop");
            await AssertHits(trailingFirst, "a-keep", "a-drop");

            var byGroups = query.WithStreamingGroups(groupA, groupB);
            await AssertHits(byGroups, "a-keep", "a-drop", "b-keep");

            await AssertHits(query.WithStreamingGroups(groupB, groupC), "b-keep", "c-keep");
            await AssertHits(query.WithStreamingGroups(groupC, groupB), "b-keep", "c-keep");

            var singleGroup = query with { StreamingGroupName = groupA };
            await AssertHits(singleGroup, "a-keep", "a-drop");
        }
        finally
        {
            foreach (var row in rows)
                await documents.DeleteByGroupAsync(row.Group, row.LocalId, "event", @namespace: "test");
        }

        async Task AssertHits(VespaSearchRequest request, params string[] expected)
        {
            var result = await fixture.Client.Search.SearchAsync<EventSummary>(request);
            Assert.Empty(result.Root.Errors ?? []);
            Assert.Equal(expected.Order(StringComparer.Ordinal),
                result.Root.Children.Select(hit => hit.Id).Order(StringComparer.Ordinal));
            Assert.All(result.Root.Children, hit => Assert.Equal(marker, hit.Fields?.EventName));
        }
    }

    [Fact]
    public async Task ModelVisit_MatchesTrailingBackslashBeforeAnotherFieldLiteral()
    {
        if (!VespaFixture.IntegrationEnabled) return;
        var tag = $"visit-escape-{Guid.NewGuid():N}";
        var trailing = tag + "\\";
        var plain = tag + "-plain";
        (string Id, string Category)[] rows =
        [
            (tag + "-a", trailing),
            (tag + "-b", plain),
            (tag + "-c", tag + "-other")
        ];
        var documents = fixture.Client.Documents;
        try
        {
            foreach (var row in rows)
                await documents.PutAsync(row.Id, new TestProduct { Category = row.Category });

            List<(string Id, string Category)> found = [];
            await foreach (var doc in documents.VisitAsync<TestProduct>(
                selection: p => p.Category == trailing || p.Category == plain, cluster: "content"))
                found.Add((doc.Id, doc.Fields!.Category));

            Assert.Equal(rows.Take(2), found.OrderBy(x => x.Id, StringComparer.Ordinal));
        }
        finally
        {
            foreach (var row in rows)
                await documents.DeleteAsync<TestProduct>(row.Id);
        }
    }

    private sealed class EventSummary
    {
        public string? EventName { get; init; }
    }
}
