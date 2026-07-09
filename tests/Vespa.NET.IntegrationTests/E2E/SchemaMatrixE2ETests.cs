using Vespa.Documents;
using Vespa.IntegrationTests.Fixtures;
using Vespa.Models;
using Vespa.Query;
using Vespa.Search;
using Xunit;

namespace Vespa.IntegrationTests.E2E;

/// <summary>
/// E2E validation of the schema-generation matrix: the shared fixture deploys one
/// application package containing inheritance (base_item/special_item), complex
/// fields with struct-field attribute blocks (complex_item), a predicate field
/// (target_item), a GC selection (expiring_item) and a streaming-mode type (event).
/// The deploy itself proves the config server accepts the generated output; these
/// tests additionally feed and query each shape.
/// </summary>
[Collection("Vespa")]
[Trait("Category", "E2E")]
public class SchemaMatrixE2ETests(VespaFixture fixture)
{
    private static bool Enabled => VespaFixture.IntegrationEnabled;

    private static string NewId() => $"schema-{Guid.NewGuid():N}";

    // ── Deployment ───────────────────────────────────────────────────────────

    [Fact]
    public async Task MultiSchemaPackage_Deployed_ApplicationStatusAvailable()
    {
        if (!Enabled) return;

        var status = await fixture.Client.Admin.GetApplicationStatusAsync();
        Assert.NotNull(status);
    }

    // ── Inheritance ──────────────────────────────────────────────────────────

    [Fact]
    public async Task InheritedSchema_ChildDocument_RoundTripsInheritedFields()
    {
        if (!Enabled) return;

        var id = NewId();
        var marker = $"inh-{Guid.NewGuid():N}";
        try
        {
            var put = await fixture.Client.Documents.PutAsync(id, new SpecialItem
            {
                Title = marker,
                BaseScore = 7,
                SpecialNote = "child-only field"
            });
            Assert.True(put.IsSuccess);
            await Task.Delay(2000);

            var doc = await fixture.Client.Documents.GetAsync<SpecialItem>(id);
            Assert.Equal(marker, doc?.Fields?.Title);
            Assert.Equal(7, doc?.Fields?.BaseScore);
            Assert.Equal("child-only field", doc?.Fields?.SpecialNote);

            // Searching the child type on the inherited (indexed) field must match
            var yql = YqlBuilder.Select().From("special_item")
                .Where(w => w.Field("title").Contains(marker))
                .Build();
            var result = await fixture.Client.Search.QueryAsync<SpecialItem>(yql);
            Assert.Single(result.Root.Children);
        }
        finally
        {
            await fixture.Client.Documents.DeleteAsync<SpecialItem>(id);
        }
    }

    [Fact]
    public async Task InheritedSchema_RankFunctions_ComputeRelevance()
    {
        if (!Enabled) return;

        var id = NewId();
        var marker = $"rank-{Guid.NewGuid():N}";
        try
        {
            await fixture.Client.Documents.PutAsync(id, new SpecialItem
            {
                Title = marker,
                BaseScore = 21,
                SpecialNote = "fn"
            });
            await Task.Delay(2000);

            // fn_profile first-phase = boosted_score(2) = attribute(base_score) * 2
            var request = YqlBuilder.Select().From("special_item")
                .Where(w => w.Field("title").Contains(marker))
                .ToSearchRequest()
                .WithRankProfile("fn_profile");

            var result = await fixture.Client.Search.SearchAsync<SpecialItem>(request);
            var hit = Assert.Single(result.Root.Children);
            Assert.Equal(42.0, hit.Relevance);
        }
        finally
        {
            await fixture.Client.Documents.DeleteAsync<SpecialItem>(id);
        }
    }

    // ── Complex fields (map + array-of-struct with struct-field attributes) ──

    [Fact]
    public async Task ComplexFields_MapAndArrayOfStruct_RoundTrip()
    {
        if (!Enabled) return;

        var id = NewId();
        try
        {
            var item = new ComplexItem
            {
                Name = "complex round trip",
                Ratings = new Dictionary<string, long> { ["quality"] = 5, ["value"] = 3 },
                Labels = new Dictionary<string, string> { ["origin"] = "e2e", ["lang"] = "en" },
                Dimensions =
                [
                    new DimensionInfo { Width = 2.5, Label = "narrow" },
                    new DimensionInfo { Width = 10.0, Label = "wide" }
                ]
            };

            var put = await fixture.Client.Documents.PutAsync(id, item);
            Assert.True(put.IsSuccess);
            await Task.Delay(2000);

            var doc = await fixture.Client.Documents.GetAsync<ComplexItem>(id);
            Assert.NotNull(doc?.Fields);
            Assert.Equal(5, doc.Fields.Ratings?["quality"]);
            Assert.Equal(3, doc.Fields.Ratings?["value"]);
            Assert.Equal("e2e", doc.Fields.Labels?["origin"]);
            Assert.Equal(2, doc.Fields.Dimensions?.Count);
            Assert.Contains(doc.Fields.Dimensions!, d => d is { Label: "wide", Width: 10.0 });
        }
        finally
        {
            await fixture.Client.Documents.DeleteAsync<ComplexItem>(id);
        }
    }

    [Fact]
    public async Task ComplexFields_SameElementQuery_MatchesStructFieldAttributes()
    {
        if (!Enabled) return;

        var id = NewId();
        var marker = $"same-{Guid.NewGuid():N}";
        try
        {
            await fixture.Client.Documents.PutAsync(id, new ComplexItem
            {
                Name = marker,
                Dimensions =
                [
                    new DimensionInfo { Width = 1.0, Label = "small" },
                    new DimensionInfo { Width = 8.0, Label = marker }
                ]
            });
            await Task.Delay(2000);

            // sameElement over the struct-field attributes generated for array<struct>
            var yql = YqlBuilder.Select().From("complex_item")
                .Where(w => w.Field("dimensions").SameElement(e => e
                    .Field("width", ">", 5.0)
                    .Contains("label", marker)))
                .Build();

            var result = await fixture.Client.Search.QueryAsync<ComplexItem>(yql);
            Assert.Single(result.Root.Children);
        }
        finally
        {
            await fixture.Client.Documents.DeleteAsync<ComplexItem>(id);
        }
    }

    // ── Predicate field ──────────────────────────────────────────────────────

    [Fact]
    public async Task PredicateField_Query_MatchesStoredPredicate()
    {
        if (!Enabled) return;

        var id = NewId();
        var marker = $"pred-{Guid.NewGuid():N}";
        try
        {
            var put = await fixture.Client.Documents.PutAsync(id, new TargetItem
            {
                Target = "gender in ['male'] and age in [20..30]",
                Note = marker
            });
            Assert.True(put.IsSuccess);
            await Task.Delay(2000);

            var matching = YqlBuilder.Select().From("target_item")
                .Where(w =>
                {
                    w.Field("note").Contains(marker);
                    w.Predicate("target",
                        new Dictionary<string, string> { ["gender"] = "male" },
                        new Dictionary<string, long> { ["age"] = 25 });
                })
                .Build();
            var hit = await fixture.Client.Search.QueryAsync<TargetItem>(matching);
            Assert.Single(hit.Root.Children);

            var nonMatching = YqlBuilder.Select().From("target_item")
                .Where(w =>
                {
                    w.Field("note").Contains(marker);
                    w.Predicate("target",
                        new Dictionary<string, string> { ["gender"] = "female" },
                        new Dictionary<string, long> { ["age"] = 25 });
                })
                .Build();
            var miss = await fixture.Client.Search.QueryAsync<TargetItem>(nonMatching);
            Assert.Empty(miss.Root.Children);
        }
        finally
        {
            await fixture.Client.Documents.DeleteAsync<TargetItem>(id);
        }
    }

    // ── Garbage-collection selection ─────────────────────────────────────────

    [Fact]
    public async Task GcSelection_MatchingDocument_Persists()
    {
        if (!Enabled) return;

        var id = NewId();
        try
        {
            // keep_until > 0 matches the GC selection, so the document must survive
            var put = await fixture.Client.Documents.PutAsync(id, new ExpiringItem
            {
                KeepUntil = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 86400,
                Payload = "kept"
            });
            Assert.True(put.IsSuccess);
            await Task.Delay(2000);

            var doc = await fixture.Client.Documents.GetAsync<ExpiringItem>(id);
            Assert.Equal("kept", doc?.Fields?.Payload);
        }
        finally
        {
            await fixture.Client.Documents.DeleteAsync<ExpiringItem>(id);
        }
    }

    // ── Streaming-mode type ──────────────────────────────────────────────────

    [Fact]
    public async Task StreamingType_GroupAddressedFeed_StreamingSearchFindsDocument()
    {
        if (!Enabled) return;

        var group = $"grp-{Guid.NewGuid():N}";
        var lid = NewId();
        var marker = $"evt-{Guid.NewGuid():N}";
        try
        {
            var put = await fixture.Client.Documents.PutByGroupAsync(
                group, lid,
                new TestEvent { EventName = marker, Payload = "streaming" },
                "event", @namespace: "test");
            Assert.True(put.IsSuccess);
            await Task.Delay(2000);

            // Cold streaming visits routinely exceed Vespa's 500 ms default query timeout
            var request = YqlBuilder.Select().From("event")
                .Where(w => w.Field("event_name").Contains(marker))
                .ToSearchRequest()
                .WithTimeout(TimeSpan.FromSeconds(10));
            request.StreamingGroupName = group;

            var result = await fixture.Client.Search.SearchAsync<TestEvent>(request);
            var hit = Assert.Single(result.Root.Children);
            Assert.Equal(marker, hit.Fields?.EventName);
        }
        finally
        {
            await fixture.Client.Documents.DeleteByGroupAsync(group, lid, "event", @namespace: "test");
        }
    }
}
